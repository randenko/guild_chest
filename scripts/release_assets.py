"""Read and validate the exact published GitHub assets shared by both publishers."""
from dataclasses import dataclass
import hashlib
import io
import json
from pathlib import Path
import re
import subprocess
import time
from urllib.error import HTTPError, URLError
from urllib.parse import urlsplit
from urllib.request import HTTPRedirectHandler, Request, build_opener
import zipfile

from package_format import REPOSITORY, VERSION, release_links, validate_archive, validate_manifest, validate_release_links

ROOT = Path(__file__).resolve().parents[1]


class CredentialRedirect(HTTPRedirectHandler):
    def redirect_request(self, request, response, code, message, headers, url):
        if urlsplit(url).scheme != 'https':
            raise ValueError('Downloads must use HTTPS.')
        redirected = super().redirect_request(request, response, code, message, headers, url)
        if redirected and urlsplit(request.full_url).netloc != urlsplit(url).netloc:
            redirected.remove_header('Authorization')
            redirected.remove_header('Apikey')
        return redirected


class Http:
    """GET-only transport. Tokens are restricted to the GitHub API origin."""
    def __init__(self, token=''):
        self.token = token
        self.opener = build_opener(CredentialRedirect())

    def get(self, url, *, optional=False, binary=False):
        parts = urlsplit(url)
        if parts.scheme != 'https':
            raise ValueError('Downloads must use HTTPS.')
        # Avoid Thunderstore CDN-cached pre-publication 404s on reconciliation.
        if parts.netloc == 'thunderstore.io' and parts.path.startswith('/api/experimental/package/'):
            separator = '&' if parts.query else '?'
            url += f'{separator}guildchest_refresh={time.time_ns()}'
        headers = {'User-Agent': 'GuildChest-release',
                   'Accept': 'application/octet-stream' if binary else 'application/json'}
        if parts.netloc == 'api.github.com' and self.token:
            headers['Authorization'] = f'Bearer {self.token}'
        try:
            with self.opener.open(Request(url, headers=headers), timeout=60) as response:
                return response.read()
        except HTTPError as error:
            if optional and error.code == 404:
                return None
            raise ValueError(f'GET {url} failed with HTTP {error.code}; retry after resolving the error.') from error
        except URLError as error:
            raise ValueError(f'GET {url} failed: {error.reason}; retry after resolving the error.') from error

    def json(self, url, *, optional=False):
        data = self.get(url, optional=optional)
        return None if data is None else json.loads(data)


def git(repository, *args):
    result = subprocess.run(['git', *args], cwd=repository, capture_output=True, text=True)
    if result.returncode:
        if args[:2] == ('merge-base', '--is-ancestor') and result.returncode == 1:
            raise ValueError('Release tag must belong to origin/main. Merge it before publishing.')
        raise ValueError(f'Cannot verify release source: {result.stderr.strip()}')
    return result.stdout


def tagged_manifest(repository, tag):
    ref = f'refs/tags/{tag}'
    git(repository, 'rev-parse', '--verify', f'{ref}^{{commit}}')
    git(repository, 'merge-base', '--is-ancestor', ref, 'refs/remotes/origin/main')
    return json.loads(git(repository, 'show', f'{ref}:manifest.json'))


def inspect_package(contents, version=None):
    with zipfile.ZipFile(io.BytesIO(contents)) as archive:
        validate_archive(archive)
        manifest = json.loads(archive.read('manifest.json'))
        validate_manifest(manifest)
        if version and manifest['version_number'] != version:
            raise ValueError('ZIP manifest version does not match the release tag.')
        for name in ('README.md', 'CHANGELOG.md'):
            validate_release_links(archive.read(name).decode(), manifest['version_number'])
        return manifest


@dataclass(frozen=True)
class ReleaseAssets:
    tag: str
    manifest: dict
    filename: str
    contents: bytes
    sha256: str
    notes: str

    @property
    def version(self):
        return self.manifest['version_number']

    @property
    def md5(self):
        # Nexus uses MD5 as a lookup identifier, not as our integrity check.
        return hashlib.md5(self.contents, usedforsecurity=False).hexdigest()


def load_release(tag, *, repository=ROOT, http=None):
    if not re.fullmatch(f'v{VERSION}', tag):
        raise ValueError('Only stable vX.Y.Z release tags are accepted.')
    source = tagged_manifest(repository, tag)
    validate_manifest(source)
    if tag != f'v{source["version_number"]}':
        raise ValueError('Tagged manifest version does not match the release tag.')
    http = http or Http()
    github_repo = REPOSITORY.removeprefix('https://github.com/')
    release = http.json(f'https://api.github.com/repos/{github_repo}/releases/tags/{tag}')
    if (release.get('tag_name') != tag or release.get('draft') is not False or
            release.get('prerelease') is not False or not release.get('published_at')):
        raise ValueError('GitHub release must be stable and published, with the requested tag.')
    filename = f'GuildChest-{source["version_number"]}.zip'
    assets = {}
    for name in (filename, filename + '.sha256'):
        matches = [asset for asset in release['assets'] if asset['name'] == name and asset.get('state') == 'uploaded']
        if len(matches) != 1:
            raise ValueError(f'Missing or ambiguous uploaded release asset: {name}.')
        assets[name] = matches[0]
    contents = http.get(assets[filename]['url'], binary=True)
    checksum = http.get(assets[filename + '.sha256']['url'], binary=True).decode('ascii')
    match = re.fullmatch(r'([0-9a-fA-F]{64})  ' + re.escape(filename) + r'\n?', checksum)
    digest = hashlib.sha256(contents).hexdigest()
    if not match or match[1].lower() != digest:
        raise ValueError('GitHub ZIP checksum mismatch or invalid checksum file.')
    manifest = inspect_package(contents, source['version_number'])
    if manifest != source:
        raise ValueError('ZIP manifest does not match the tagged manifest.')
    notes = git(repository, 'show', f'refs/tags/{tag}:docs/releases/{tag}.md')
    with zipfile.ZipFile(io.BytesIO(contents)) as archive:
        if archive.read('CHANGELOG.md') != release_links(notes, source['version_number'], 'docs/releases').encode():
            raise ValueError('Packaged changelog does not match the tagged release notes.')
    return ReleaseAssets(tag, manifest, filename, contents, digest, notes)
