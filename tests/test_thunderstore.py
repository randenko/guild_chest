"""Validate the publisher with disposable tags, mocked GETs, and a recording CLI."""
import copy
import hashlib
import io
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import tomllib
import unittest
from unittest.mock import patch
from urllib.error import HTTPError
from urllib.request import Request
import zipfile

PROJECT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT / 'scripts'))
from package_format import PACKAGE_FILES, REPOSITORY, release_links
from publish_thunderstore import CATEGORIES, CredentialRedirect, Http, THUNDERSTORE, check_availability, inspect_package, publish


class FakeHttp:
    def __init__(self, responses):
        self.responses = responses
        self.calls = []

    def get(self, url, *, optional=False, binary=False):
        self.calls.append(url)
        if url not in self.responses:
            raise AssertionError(f'Unexpected GET: {url}')
        value = self.responses[url]
        if isinstance(value, list):
            value = value.pop(0) if len(value) > 1 else value[0]
        if value is None and not optional:
            raise ValueError(f'GET {url} failed with HTTP 404')
        if isinstance(value, Exception):
            raise value
        return value

    def json(self, url, *, optional=False):
        value = self.get(url, optional=optional)
        return copy.deepcopy(value)


class ThunderstoreTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.manifest = json.loads((PROJECT / 'manifest.json').read_text())
        self.manifest['version_number'] = '1.2.3'
        self.notes = '# Guild Chest 1.2.3\n\n## Mod changes\n\n- A fix.\n\n## Other changes\n\n- A build fix.\n'
        (self.root / 'manifest.json').write_text(json.dumps(self.manifest))
        notes = self.root / 'docs/releases/v1.2.3.md'
        notes.parent.mkdir(parents=True)
        notes.write_text(self.notes)
        self.git('init', '--initial-branch=main')
        self.git('config', 'user.name', 'Publisher Test')
        self.git('config', 'user.email', 'publisher@example.invalid')
        self.git('add', '.')
        self.git('commit', '-m', 'Release source')
        self.git('tag', 'v1.2.3')
        self.git('update-ref', 'refs/remotes/origin/main', 'HEAD')
        self.files = {name: b'fixture' for name in PACKAGE_FILES}
        self.files.update({
            'manifest.json': json.dumps(self.manifest).encode(),
            'README.md': release_links((PROJECT / 'README.md').read_text(), '1.2.3').encode(),
            'CHANGELOG.md': self.notes.encode(),
            'icon.png': (PROJECT / 'icon.png').read_bytes(),
        })
        self.filename = 'GuildChest-1.2.3.zip'
        self.release_url = 'https://api.github.com/repos/randenko/valheim-guildchest/releases/tags/v1.2.3'
        self.zip_url = 'https://api.github.com/repos/randenko/valheim-guildchest/releases/assets/1'
        self.sha_url = 'https://api.github.com/repos/randenko/valheim-guildchest/releases/assets/2'
        self.version_url = f'{THUNDERSTORE}/api/experimental/package/Paper_Street_Software/GuildChest/1.2.3/'
        self.download_url = f'{THUNDERSTORE}/package/download/Paper_Street_Software/GuildChest/1.2.3/'
        self.categories_url = f'{THUNDERSTORE}/api/experimental/community/valheim/category/'
        self.release = {'tag_name': 'v1.2.3', 'draft': False, 'prerelease': False,
                        'published_at': '2026-10-13T00:00:00Z', 'assets': [
                            {'name': self.filename, 'state': 'uploaded', 'url': self.zip_url},
                            {'name': self.filename + '.sha256', 'state': 'uploaded', 'url': self.sha_url}]}
        self.http = FakeHttp({
            self.release_url: self.release,
            self.categories_url: {'results': [{'slug': slug} for slug in CATEGORIES],
                                  'pagination': {'next_link': None}},
            self.version_url: None,
        })
        for dependency in self.manifest['dependencies']:
            author, name, version = dependency.split('-')
            self.http.responses[f'{THUNDERSTORE}/api/experimental/package/{author}/{name}/{version}/'] = {
                'full_name': dependency, 'is_active': True}
        self.write_archive()
        self.trace = self.root / 'cli.json'
        self.cli = self.root / 'recording-tcli'
        self.cli.write_text(f'''#!{sys.executable}
import json
import os
from pathlib import Path
import sys
args = sys.argv[1:]
config = Path(args[args.index('--config-path') + 1])
archive = Path(args[args.index('--file') + 1])
Path({str(self.trace)!r}).write_text(json.dumps({{
    'args': args, 'config': config.read_text(), 'archive': archive.read_bytes().hex(),
    'token_present': bool(os.environ.get('TCLI_AUTH_TOKEN')),
    'tcli_environment': sorted(key for key in os.environ if key.startswith('TCLI_')),
}}))
sys.exit(int(os.environ.get('GUILDCHEST_FAKE_CLI_EXIT', '0')))
''')
        self.cli.chmod(0o755)

    def git(self, *args):
        return subprocess.run(['git', *args], cwd=self.root, capture_output=True,
                              text=True, check=True).stdout.strip()

    def write_archive(self):
        stream = io.BytesIO()
        with zipfile.ZipFile(stream, 'w') as archive:
            for name, contents in sorted(self.files.items()):
                archive.writestr(name, contents)
        self.contents = stream.getvalue()
        self.http.responses[self.zip_url] = self.contents
        self.http.responses[self.sha_url] = (hashlib.sha256(self.contents).hexdigest() + '  ' + self.filename + '\n').encode()

    def existing(self):
        return {'namespace': 'Paper_Street_Software', 'name': 'GuildChest',
                'version_number': '1.2.3', 'is_active': True, 'download_url': self.download_url}

    def run_publish(self, **kwargs):
        defaults = dict(repository=self.root, namespace='Paper_Street_Software', token='fixture-token',
                        http=self.http, cli=str(self.cli), sleep=lambda seconds: None)
        return publish(kwargs.pop('tag', 'v1.2.3'), **{**defaults, **kwargs})

    def assert_rejected(self, message, **kwargs):
        with self.assertRaisesRegex(ValueError, message):
            self.run_publish(preflight=False, **kwargs)
        self.assertFalse(self.trace.exists(), 'Invalid preflight must never invoke the upload CLI.')

    def test_success_uses_zip_metadata_exact_bytes_and_file_option(self):
        self.http.responses[self.version_url] = [None, None, self.existing()]
        self.http.responses[self.download_url] = self.contents
        with patch.dict(os.environ, {'TCLI_PACKAGE_NAMESPACE': 'Wrong', 'TCLI_REPOSITORY': 'https://wrong.invalid'}):
            message = self.run_publish(preflight=False)
        self.assertIn('Published and verified SHA-256', message)
        self.assertIn('/c/valheim/p/Paper_Street_Software/GuildChest/', message)
        recorded = json.loads(self.trace.read_text())
        self.assertEqual(bytes.fromhex(recorded['archive']), self.contents)
        self.assertEqual(recorded['args'][0], 'publish')
        self.assertNotIn('--token', recorded['args'])
        self.assertTrue(recorded['token_present'])
        self.assertEqual(recorded['tcli_environment'], ['TCLI_AUTH_TOKEN'])
        config = tomllib.loads(recorded['config'])
        self.assertEqual(config['package']['versionNumber'], '1.2.3')
        self.assertEqual(config['package']['namespace'], 'Paper_Street_Software')
        self.assertEqual(config['package']['description'], self.manifest['description'])
        self.assertEqual(config['publish']['communities'], ['valheim'])
        self.assertEqual(config['publish']['categories']['valheim'], list(CATEGORIES))

    def test_preflight_is_get_only_and_never_invokes_cli(self):
        self.assertIn('Preflight passed', self.run_publish())
        self.assertFalse(self.trace.exists())

    def test_matching_existing_version_succeeds_without_upload(self):
        self.http.responses[self.version_url] = self.existing()
        self.http.responses[self.download_url] = self.contents
        self.assertIn('Already published', self.run_publish(preflight=False))
        self.assertFalse(self.trace.exists())

    def test_existing_but_unusable_version_never_permits_another_upload(self):
        for changes in ({'is_active': False}, {'download_url': ''}, {'version_number': '1.2.2'}):
            self.http.responses[self.version_url] = {**self.existing(), **changes}
            with self.subTest(changes=changes):
                self.assert_rejected('inactive or has no download URL|different package version')

    def test_upload_dependencies_come_from_tag_without_inheriting_previous_version_metadata(self):
        # Simulate incomplete moving metadata; only the verified tagged archive
        # supplies upload dependencies, and no prior package lookup is configured.
        (self.root / 'manifest.json').write_text(json.dumps({**self.manifest, 'dependencies': []}))
        self.http.responses[self.version_url] = [None, self.existing()]
        self.http.responses[self.download_url] = self.contents
        self.assertIn('Published and verified', self.run_publish(preflight=False))
        recorded = json.loads(self.trace.read_text())
        dependencies = tomllib.loads(recorded['config'])['package']['dependencies']
        expected = {dependency.rsplit('-', 1)[0]: dependency.rsplit('-', 1)[1]
                    for dependency in self.manifest['dependencies']}
        self.assertEqual(dependencies, expected)
        self.assertEqual(bytes.fromhex(recorded['archive']), self.contents)

    def test_conflicting_existing_version_fails_clearly(self):
        self.http.responses[self.version_url] = self.existing()
        self.http.responses[self.download_url] = b'other archive'
        self.assert_rejected('different archive bytes')

    def test_unpublished_prerelease_and_wrong_release_are_rejected(self):
        for field, value in (('draft', True), ('prerelease', True), ('published_at', None), ('tag_name', 'v9.9.9')):
            with self.subTest(field=field):
                self.http.responses[self.release_url] = {**self.release, field: value}
                self.assert_rejected('stable and published')

    def test_invalid_tag_and_missing_configuration_are_rejected(self):
        for tag in ('v1.2.3-beta', 'v01.2.3', '--help', 'v1.2.3\n'):
            with self.subTest(tag=tag):
                self.assert_rejected('stable vX.Y.Z', tag=tag)
        for config in ({'namespace': ''}, {'namespace': 'unsafe/namespace'}, {'token': ''}):
            with self.subTest(config=config):
                self.assert_rejected('missing or invalid|TCLI_AUTH_TOKEN', **config)

    def test_unmerged_tag_and_missing_tag_are_rejected(self):
        self.git('checkout', '-b', 'unmerged')
        self.git('commit', '--allow-empty', '-m', 'Not merged')
        self.git('tag', '-f', 'v1.2.3')
        self.assert_rejected('must belong to origin/main')
        self.git('tag', '-d', 'v1.2.3')
        self.assert_rejected('Cannot verify release source')

    def test_old_release_uses_tag_manifest_instead_of_worktree(self):
        (self.root / 'manifest.json').write_text('{}')
        self.git('commit', '--allow-empty', '-m', 'Later main commit')
        self.git('update-ref', 'refs/remotes/origin/main', 'HEAD')
        self.assertIn('Preflight passed', self.run_publish())

    def test_tag_and_archive_manifest_mismatches_are_rejected(self):
        altered = {**self.manifest, 'version_number': '1.2.4'}
        self.files['manifest.json'] = json.dumps(altered).encode()
        self.write_archive()
        self.assert_rejected('ZIP manifest version')
        self.files['manifest.json'] = json.dumps({**self.manifest, 'description': 'Other'}).encode()
        self.write_archive()
        self.assert_rejected('does not match the tagged manifest')

    def test_tagged_manifest_must_match_tag(self):
        self.manifest['version_number'] = '1.2.4'
        (self.root / 'manifest.json').write_text(json.dumps(self.manifest))
        self.git('add', 'manifest.json')
        self.git('commit', '-m', 'Mismatched version')
        self.git('tag', '-f', 'v1.2.3')
        self.git('update-ref', 'refs/remotes/origin/main', 'HEAD')
        self.assert_rejected('Tagged manifest version')

    def test_corrupt_checksum_missing_assets_and_notes_mismatch_are_rejected(self):
        self.http.responses[self.sha_url] = b'0' * 64 + b'  GuildChest-1.2.3.zip\n'
        self.assert_rejected('checksum mismatch')
        self.write_archive()
        self.files['CHANGELOG.md'] = b'Other notes'
        self.write_archive()
        self.assert_rejected('changelog does not match')
        self.release['assets'].pop()
        self.assert_rejected('Missing or ambiguous')

    def test_missing_dependencies_and_categories_are_rejected(self):
        dependency_url = next(url for url in self.http.responses if '/package/denikson/' in url)
        self.http.responses[dependency_url] = None
        self.assert_rejected('dependency is unavailable')
        self.http.responses[self.categories_url]['results'].pop()
        self.assert_rejected('Unavailable Valheim categories')

    def test_category_pagination_is_followed(self):
        second = self.categories_url + '?page=2'
        self.http.responses[self.categories_url] = {'results': [{'slug': 'mods'}], 'pagination': {'next_link': second}}
        self.http.responses[second] = {'results': [{'slug': 'building'}, {'slug': 'utility'}], 'pagination': {'next_link': None}}
        self.assertIn('Preflight passed', self.run_publish())
        self.assertIn(second, self.http.calls)

    def test_interrupted_upload_with_accepted_bytes_is_success(self):
        self.http.responses[self.version_url] = [None, self.existing()]
        self.http.responses[self.download_url] = self.contents
        with patch.dict(os.environ, {'GUILDCHEST_FAKE_CLI_EXIT': '1'}):
            self.assertIn('Published and verified', self.run_publish(preflight=False))

    def test_unconfirmed_upload_can_be_retried_without_duplicate_submission(self):
        with patch.dict(os.environ, {'GUILDCHEST_FAKE_CLI_EXIT': '1'}):
            with self.assertRaisesRegex(ValueError, 'Retry this release tag'):
                self.run_publish(preflight=False)
        original_trace = self.trace.read_bytes()
        self.http.responses[self.version_url] = self.existing()
        self.http.responses[self.download_url] = self.contents
        self.assertIn('Already published', self.run_publish(preflight=False))
        self.assertEqual(self.trace.read_bytes(), original_trace)

    def test_incomplete_upload_can_be_resubmitted(self):
        with self.assertRaisesRegex(ValueError, 'not yet verified'):
            self.run_publish(preflight=False)
        self.http.responses[self.version_url] = [None, self.existing()]
        self.http.responses[self.download_url] = self.contents
        self.assertIn('Published and verified', self.run_publish(preflight=False))

    def test_archive_structure_rejects_extra_missing_duplicate_and_relative_files(self):
        for change in ('extra', 'missing', 'relative', 'bad icon'):
            with self.subTest(change=change):
                files = dict(self.files)
                if change == 'extra':
                    files['docs/media/original.jpg'] = b'image'
                elif change == 'missing':
                    files.pop('BepInEx/plugins/GuildChest/GuildChest.Core.dll')
                elif change == 'relative':
                    files['README.md'] = b'[guide](docs/guide.md)'
                else:
                    files['icon.png'] = b'broken'
                stream = io.BytesIO()
                with zipfile.ZipFile(stream, 'w') as archive:
                    for name, value in files.items():
                        archive.writestr(name, value)
                with self.assertRaises(ValueError):
                    inspect_package(stream.getvalue())
        stream = io.BytesIO(self.contents)
        with zipfile.ZipFile(stream, 'a') as archive:
            with self.assertWarns(UserWarning):
                archive.writestr('README.md', b'duplicate')
        with self.assertRaisesRegex(ValueError, 'structure'):
            inspect_package(stream.getvalue())

    def test_absolute_links_to_other_releases_are_rejected(self):
        self.files['README.md'] = f'[guide]({REPOSITORY}/blob/main/docs/VALIDATION.md)'.encode()
        self.write_archive()
        self.assert_rejected('links must point to the package release tag')


class HttpTests(unittest.TestCase):
    def test_publication_metadata_checks_do_not_reuse_cached_absence(self):
        http = Http()
        url = 'https://thunderstore.io/api/experimental/package/Paper_Street_Software/GuildChest/1.1.0/'
        with patch.object(http.opener, 'open') as open_url, patch('publish_thunderstore.time.time_ns', side_effect=(123, 456)):
            open_url.return_value.__enter__.return_value.read.return_value = b'{}'
            http.json(url, optional=True)
            http.json(url, optional=True)
            first, second = [call.args[0].full_url for call in open_url.call_args_list]
            self.assertEqual(first, url + '?guildchest_refresh=123')
            self.assertEqual(second, url + '?guildchest_refresh=456')

    def test_transport_uses_get_and_authenticates_only_github(self):
        http = Http('private-fixture-token')
        with patch.object(http.opener, 'open') as open_url:
            open_url.return_value.__enter__.return_value.read.return_value = b'{}'
            http.json('https://api.github.com/repos/example/repo')
            request = open_url.call_args.args[0]
            self.assertEqual(request.get_method(), 'GET')
            self.assertEqual(request.get_header('Authorization'), 'Bearer private-fixture-token')
            http.json('https://thunderstore.io/api/experimental/community/')
            self.assertIsNone(open_url.call_args.args[0].get_header('Authorization'))

    def test_cross_origin_redirect_removes_authorization(self):
        request = Request('https://api.github.com/assets/1', headers={'Authorization': 'Bearer private'})
        redirected = CredentialRedirect().redirect_request(request, None, 302, 'Found', {}, 'https://release-assets.githubusercontent.com/file')
        self.assertIsNone(redirected.get_header('Authorization'))

    def test_only_404_can_mean_absent_version(self):
        http = Http()
        for code in (404, 403, 429, 500):
            with self.subTest(code=code), patch.object(http.opener, 'open', side_effect=HTTPError('https://thunderstore.io', code, 'error', {}, None)):
                if code == 404:
                    self.assertIsNone(http.json('https://thunderstore.io', optional=True))
                else:
                    with self.assertRaisesRegex(ValueError, f'HTTP {code}'):
                        http.json('https://thunderstore.io', optional=True)


if __name__ == '__main__':
    unittest.main()
