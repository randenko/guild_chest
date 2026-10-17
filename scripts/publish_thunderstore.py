#!/usr/bin/env python3
"""Preflight or upload an existing, published GitHub release without rebuilding it."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import time
import zipfile

from package_format import DEPENDENCY, VERSION
from release_assets import CredentialRedirect, Http, inspect_package, load_release

ROOT = Path(__file__).resolve().parents[1]
THUNDERSTORE = 'https://thunderstore.io'
COMMUNITY = 'valheim'
CATEGORIES = ('mods', 'building', 'utility')


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
            enabled='', cli='tcli', run=subprocess.run, sleep=time.sleep):
    if not re.fullmatch(f'v{VERSION}', tag):
        raise ValueError('Only stable vX.Y.Z release tags are accepted.')
    if not preflight and enabled != 'true':
        raise ValueError('THUNDERSTORE_PUBLISH_ENABLED must be true for publishing.')
    if not re.fullmatch(r'[A-Za-z0-9_]+', namespace):
        raise ValueError('THUNDERSTORE_NAMESPACE is missing or invalid.')
    if not token.strip():
        raise ValueError('TCLI_AUTH_TOKEN is missing (configure THUNDERSTORE_API_TOKEN).')
    http = http or Http(os.environ.get('GH_TOKEN', ''))
    assets = load_release(tag, repository=repository, http=http)
    manifest, contents = assets.manifest, assets.contents
    filename, digest = assets.filename, assets.sha256
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
                          token=os.environ.get('TCLI_AUTH_TOKEN', ''), preflight=not args.upload,
                          enabled=os.environ.get('THUNDERSTORE_PUBLISH_ENABLED', ''), cli=args.cli)
        print(message)
        summary = os.environ.get('GITHUB_STEP_SUMMARY')
        if summary:
            with Path(summary).open('a', encoding='utf-8') as output:
                output.write(message + '\n')
    except (ValueError, OSError, zipfile.BadZipFile, subprocess.TimeoutExpired) as error:
        raise SystemExit(str(error)) from error


if __name__ == '__main__':
    main()
