#!/usr/bin/env python3
"""Preflight or upload an existing, published GitHub release without rebuilding it."""
import argparse
import hashlib
import io
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import time
from urllib.error import HTTPError, URLError
from urllib.parse import urlsplit
from urllib.request import HTTPRedirectHandler, Request, build_opener
import zipfile

from package_format import DEPENDENCY, REPOSITORY, VERSION, release_links, validate_archive, validate_manifest, validate_release_links

ROOT = Path(__file__).resolve().parents[1]
THUNDERSTORE = 'https://thunderstore.io'
COMMUNITY = 'valheim'
CATEGORIES = ('mods', 'building', 'utility')


class CredentialRedirect(HTTPRedirectHandler):
    def redirect_request(self, request, response, code, message, headers, url):
        redirected = super().redirect_request(request, response, code, message, headers, url)
        if redirected and urlsplit(request.full_url).netloc != urlsplit(url).netloc:
            redirected.remove_header('Authorization')
        return redirected


class Http:
    """GET-only transport. Tokens are restricted to the GitHub API origin."""
    def __init__(self, token=''):
        self.token = token
        self.opener = build_opener(CredentialRedirect())

    def get(self, url, *, optional=False, binary=False):
        if urlsplit(url).scheme != 'https':
            raise ValueError('Downloads must use HTTPS.')
        # Thunderstore's CDN can retain a pre-publication 404 after an upload
        # succeeds. Each metadata check must reach a fresh cache key so retries
        # see the existing version before considering another submission.
        parts = urlsplit(url)
        if parts.netloc == 'thunderstore.io' and parts.path.startswith('/api/experimental/package/'):
            separator = '&' if parts.query else '?'
            url += f'{separator}guildchest_refresh={time.time_ns()}'
        headers = {'User-Agent': 'GuildChest-release',
                   'Accept': 'application/octet-stream' if binary else 'application/json'}
        if urlsplit(url).netloc == 'api.github.com' and self.token:
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


def check_availability(http, manifest):
    url = f'{THUNDERSTORE}/api/experimental/community/{COMMUNITY}/category/'
    slugs = set()
    seen = set()
    while url:
        if url in seen or not url.startswith(f'{THUNDERSTORE}/api/experimental/community/{COMMUNITY}/category/'):
            raise ValueError('Invalid category pagination URL.')
        seen.add(url)
        page = http.json(url)
        slugs.update(item['slug'] for item in page['results'])
        url = page['pagination']['next_link']
    missing = set(CATEGORIES) - slugs
    if missing:
        raise ValueError(f'Unavailable Valheim categories: {", ".join(sorted(missing))}.')
    for dependency in manifest['dependencies']:
        namespace, name, version = DEPENDENCY.fullmatch(dependency).groups()
        data = http.json(f'{THUNDERSTORE}/api/experimental/package/{namespace}/{name}/{version}/', optional=True)
        if not data or data.get('is_active') is not True or data.get('full_name') != dependency:
            raise ValueError(f'Required dependency is unavailable or inactive: {dependency}.')


def version_url(namespace, manifest):
    return (f'{THUNDERSTORE}/api/experimental/package/{namespace}/'
            f'{manifest["name"]}/{manifest["version_number"]}/')


def matching_version(http, namespace, manifest, contents):
    data = http.json(version_url(namespace, manifest), optional=True)
    if data is None:
        return False
    if (data.get('namespace'), data.get('name'), data.get('version_number')) != (
            namespace, manifest['name'], manifest['version_number']):
        raise ValueError('Thunderstore returned a different package version.')
    if data.get('is_active') is not True or not data.get('download_url'):
        raise ValueError('Existing Thunderstore version is inactive or has no download URL.')
    existing = http.get(data['download_url'], binary=True)
    if hashlib.sha256(existing).digest() != hashlib.sha256(contents).digest():
        raise ValueError('Existing Thunderstore version has different archive bytes; choose a new version.')
    return True


def configuration(namespace, manifest):
    # JSON string quoting is compatible with TOML basic strings.
    quote = json.dumps
    lines = ['[config]', 'schemaVersion = "0.0.1"', '', '[package]',
             f'namespace = {quote(namespace)}', f'name = {quote(manifest["name"])}',
             f'versionNumber = {quote(manifest["version_number"])}',
             f'description = {quote(manifest["description"])}',
             f'websiteUrl = {quote(manifest["website_url"])}', 'containsNsfwContent = false',
             '', '[package.dependencies]']
    for dependency in manifest['dependencies']:
        author, name, version = DEPENDENCY.fullmatch(dependency).groups()
        lines.append(f'{quote(author + "-" + name)} = {quote(version)}')
    lines.extend(['', '[build]', 'outdir = "unused"', 'copy = []', '', '[publish]',
                  f'repository = {quote(THUNDERSTORE)}', f'communities = ["{COMMUNITY}"]',
                  '', '[publish.categories]', f'{COMMUNITY} = {quote(CATEGORIES)}', ''])
    return '\n'.join(lines)


def publish(tag, *, repository=ROOT, namespace='', token='', http=None, preflight=True,
            cli='tcli', run=subprocess.run, sleep=time.sleep):
    if not re.fullmatch(f'v{VERSION}', tag):
        raise ValueError('Only stable vX.Y.Z release tags are accepted.')
    if not re.fullmatch(r'[A-Za-z0-9_]+', namespace):
        raise ValueError('THUNDERSTORE_NAMESPACE is missing or invalid.')
    if not token.strip():
        raise ValueError('TCLI_AUTH_TOKEN is missing (configure THUNDERSTORE_API_TOKEN).')
    source = tagged_manifest(repository, tag)
    validate_manifest(source)
    if tag != f'v{source["version_number"]}':
        raise ValueError('Tagged manifest version does not match the release tag.')
    http = http or Http(os.environ.get('GH_TOKEN', ''))
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
    check_availability(http, manifest)
    listing = f'{THUNDERSTORE}/c/{COMMUNITY}/p/{namespace}/{manifest["name"]}/'
    existing = matching_version(http, namespace, manifest, contents)
    if preflight:
        return f'Preflight passed: {tag}; SHA-256 {digest}; existing version: {existing}. Target: {listing}'
    if existing:
        return f'Already published with matching archive bytes: {listing}'
    with tempfile.TemporaryDirectory(prefix='guildchest-publish-') as directory:
        archive_path = Path(directory) / filename
        archive_path.write_bytes(contents)
        config = Path(directory) / 'thunderstore.toml'
        config.write_text(configuration(namespace, manifest), encoding='utf-8')
        environment = {key: value for key, value in os.environ.items() if not key.startswith('TCLI_')}
        environment['TCLI_AUTH_TOKEN'] = token
        result = run([cli, 'publish', '--config-path', str(config), '--file', str(archive_path),
                      '--tcli-directory', str(Path(directory) / 'tcli-state')],
                     cwd=directory, env=environment, timeout=3600)
    # The submission may have succeeded even if tcli lost its response. Check
    # the public archive in either case, allowing for delayed API visibility.
    for attempt in range(6):
        if matching_version(http, namespace, manifest, contents):
            return f'Published and verified SHA-256 {digest}: {listing}'
        if attempt < 5:
            sleep(10)
    raise ValueError(f'tcli exited {result.returncode}; publication is not yet verified. '
                     'Retry this release tag; an existing matching version will skip upload.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('tag')
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument('--preflight', action='store_true', help='GET-only validation; never invokes tcli (default).')
    mode.add_argument('--upload', action='store_true', help='Publish the reviewed archive after validation.')
    parser.add_argument('--cli', default='tcli')
    args = parser.parse_args()
    try:
        message = publish(args.tag, namespace=os.environ.get('THUNDERSTORE_NAMESPACE', ''),
                          token=os.environ.get('TCLI_AUTH_TOKEN', ''), preflight=not args.upload, cli=args.cli)
        print(message)
        summary = os.environ.get('GITHUB_STEP_SUMMARY')
        if summary:
            with Path(summary).open('a', encoding='utf-8') as output:
                output.write(message + '\n')
    except (ValueError, OSError, zipfile.BadZipFile, subprocess.TimeoutExpired) as error:
        raise SystemExit(str(error)) from error


if __name__ == '__main__':
    main()
