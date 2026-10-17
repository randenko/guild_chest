"""Exercise Nexus publication and recovery without making remote writes."""
import copy
import hashlib
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch
from urllib.error import HTTPError, URLError
from urllib.request import Request

PROJECT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT / 'scripts'))
import publish_nexusmods as nexus
from release_assets import CredentialRedirect, ReleaseAssets


TARGET = nexus.Target('15749645078908', '8177514', '4476')
GAME = {'id': 3667, 'domainName': 'valheim'}


def file_record(version='1.1.0', file_id=25735, *, primary=1, category='MAIN'):
    return {'fileId': file_id, 'groupId': 8177514, 'modId': 4476,
            'modUid': TARGET.mod_id, 'uid': str(3667 * 2**32 + file_id),
            'version': version, 'category': category, 'primary': primary,
            'sizeInBytes': '16', 'changelogText': [], 'requirementsAlert': 1,
            'scannedV2': 'VERIFIED',
            'mod': {'uid': TARGET.mod_id, 'modId': 4476, 'gameId': 3667,
                    'name': 'Guild Chest', 'status': 'published', 'version': version,
                    'legacyModRequirementsEnabled': False}}


def raw_dependencies(minimum='1234', dlc='20'):
    return {'dependency_definitions': [{'id': '10', 'ranges': [
        {'id': '11', 'min_version': {'id': minimum}, 'max_version': None}]}],
        'dlc_dependency_definitions': [{'id': '12', 'dlc_targets': [{'dlc_id': dlc}]}]}


EMPTY_DEPENDENCIES = {'dependency_definitions': [], 'dlc_dependency_definitions': []}


class FakeGithub:
    def __init__(self):
        self.records = {'file': [], 'notes': []}
        self.calls = []

    def json(self, url):
        self.calls.append(url)
        stage = 'notes' if 'name=nexusmods-notes-' in url else 'file'
        return {'total_count': len(self.records[stage]), 'artifacts': self.records[stage]}


class FakeNexus:
    def __init__(self, release):
        self.release = release
        self.files = [file_record()]
        self.dependencies = {self.files[0]['uid']: raw_dependencies()}
        self.calls = []
        self.hash_available = True
        self.append_behavior = 'success'
        self.put_loses_response = False

    @property
    def writes(self):
        return [call for call in self.calls if call[0] not in ('GET', 'query')]

    def published(self):
        for item in self.files:
            item.update(primary=0, category='ARCHIVED')
            item['mod']['version'] = self.release.version
        file = file_record(self.release.version, max(item['fileId'] for item in self.files) + 1)
        file['sizeInBytes'] = str(len(self.release.contents))
        self.files.append(file)
        self.dependencies[file['uid']] = copy.deepcopy(EMPTY_DEPENDENCIES)
        return file

    def query(self, query, variables=None):
        self.calls.append(('query', query, variables))
        if 'GuildChestGame' in query:
            return {'game': GAME}
        if 'GuildChestFiles' in query:
            return {'modFiles': copy.deepcopy(self.files)}
        if 'GuildChestHash' in query:
            matches = [file for file in self.files if file['version'] == self.release.version]
            return {'fileHashes': [
                {'md5': self.release.md5, 'fileSize': str(len(self.release.contents)),
                 'gameId': GAME['id'], 'modFileId': file['fileId'], 'modFile': file}
                for file in matches] if self.hash_available else []}
        raise AssertionError(f'Unexpected query: {query}')

    def request(self, path, *, method='GET', body=None):
        self.calls.append((method, path, body))
        if method == 'POST' and path.endswith('/changelogs'):
            if self.append_behavior != 'rejected':
                for file in self.files:
                    if file['version'] == body['version']:
                        file['changelogText'].extend(body['changelog'].splitlines())
            if self.append_behavior != 'success':
                raise ValueError('Lost or rejected changelog response')
            return {'data': body}
        if '/dependencies' in path:
            version_id = path.split('/')[3]
            if method == 'GET':
                return copy.deepcopy(self.dependencies[version_id])
            if path.endswith('/ranges'):
                self.dependencies[version_id]['dependency_definitions'] = [
                    {'id': str(index), 'ranges': [
                        {'min_version': {'id': entry['min_version_id']},
                         'max_version': None if entry['max_version_id'] is None else {'id': entry['max_version_id']}}
                        for entry in definition['ranges']]}
                    for index, definition in enumerate(body['dependency_definitions'])]
            elif path.endswith('/dlc'):
                self.dependencies[version_id]['dlc_dependency_definitions'] = [
                    {'id': str(index), 'dlc_targets': [{'dlc_id': value} for value in definition['dlc_ids']]}
                    for index, definition in enumerate(body['dlc_dependency_definitions'])]
            else:
                raise AssertionError(path)
            if self.put_loses_response:
                raise ValueError('Lost PUT response')
            return None
        if '/games/valheim/mods/' in path:
            return {'data': {'id': TARGET.mod_id, 'game_scoped_id': TARGET.page_id}}
        if path.endswith('/files'):
            return {'data': {'mod_files': [{'id': TARGET.file_id}]}}
        if path.endswith('/versions'):
            return {'data': {'versions': [
                {'id': file['uid'], 'game_scoped_id': str(file['fileId']), 'version': file['version'],
                 'file': {'id': str(file['groupId'])}} for file in self.files]}}
        raise AssertionError(f'Unexpected API call: {method} {path}')


class NexusTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.directory = Path(self.temp.name)
        manifest = json.loads((PROJECT / 'manifest.json').read_text())
        manifest['version_number'] = '1.2.3'
        contents = b'verified archive'
        self.release = ReleaseAssets('v1.2.3', manifest, 'GuildChest-1.2.3.zip', contents,
                                     hashlib.sha256(contents).hexdigest(),
                                     '# Guild Chest 1.2.3\n\n## Mod changes\n\n- A fix with\n  wrapped text.\n\n'
                                     '## Other changes\n\n- Read the [guide](../DEVELOPMENT.md).\n')
        self.api, self.github = FakeNexus(self.release), FakeGithub()

    def prepare(self):
        return nexus.prepare(self.release, TARGET, self.api, self.github, self.directory)

    def verify(self):
        return nexus.verify(self.release, TARGET, self.api, self.github, self.directory, sleep=lambda _: None)

    def finalize(self):
        return nexus.finalize(self.release, TARGET, self.api, self.github, self.directory,
                              run_id='99', sleep=lambda _: None)

    def allow_notes(self):
        self.github.records['notes'] = [{'workflow_run': {'id': 99}}]

    def publish_and_verify(self):
        self.prepare()
        file = self.api.published()
        self.verify()
        self.allow_notes()
        return file

    def next_release(self):
        manifest = {**self.release.manifest, 'version_number': '1.2.4'}
        self.release = ReleaseAssets('v1.2.4', manifest, 'GuildChest-1.2.4.zip',
                                     self.release.contents, self.release.sha256,
                                     self.release.notes.replace('1.2.3', '1.2.4'))
        self.api.release = self.release

    def test_preflight_reads_only_and_uses_verified_release(self):
        result = nexus.preflight(self.release, TARGET, self.api, self.github, authenticated=True)
        self.assertIn('not yet published', result)
        self.assertIn(self.release.sha256, result)
        self.assertFalse(self.api.writes)
        self.assertFalse(list(self.directory.iterdir()))

    def test_public_preflight_verifies_manual_baseline_without_credentials(self):
        self.release.manifest['version_number'] = '1.1.0'
        baseline = ReleaseAssets('v1.1.0', self.release.manifest, 'GuildChest-1.1.0.zip',
                                 self.release.contents, self.release.sha256, 'Original GitHub notes')
        self.api.release = baseline
        self.api.files[0]['sizeInBytes'] = str(len(baseline.contents))
        self.api.files[0]['changelogText'] = ['Manual Nexus introduction']
        result = nexus.preflight(baseline, TARGET, self.api, self.github)
        self.assertIn('manual baseline retained', result)
        self.assertIn('not tested (public reads only)', result)
        self.assertFalse(self.api.writes)

    def test_baseline_and_older_versions_cannot_authorize_any_writes(self):
        for tag in ('v1.1.0', 'v1.0.12', 'v1.1.1-beta', '--help', 'v01.2.3'):
            with self.subTest(tag=tag), self.assertRaisesRegex(ValueError, 'greater than 1.1.0'):
                nexus.authorize(tag, 'true', 'key')
        for tag in ('v1.1.1', 'v1.2.0', 'v2.0.0'):
            nexus.authorize(tag, 'true', 'key')

    def test_disabled_upload_or_missing_key_is_rejected(self):
        for enabled, token, message in (('false', 'key', 'PUBLISH_ENABLED'), ('true', '', 'API_KEY')):
            with self.subTest(enabled=enabled), self.assertRaisesRegex(ValueError, message):
                nexus.authorize(self.release.tag, enabled, token)

    def test_invalid_configuration_is_rejected(self):
        for values in (('', '1', '2'), ('1', '../2', '3'), ('1', '2', '3\n')):
            with self.subTest(values=values), self.assertRaisesRegex(ValueError, 'missing or invalid'):
                nexus.Target(*values)

    def test_wrong_page_game_or_mod_id_and_missing_main_file_are_rejected(self):
        original = copy.deepcopy(self.api.files)
        changes = (lambda file: file.update(modUid='wrong'),
                   lambda file: file['mod'].update(gameId=1),
                   lambda file: file['mod'].update(status='hidden'),
                   lambda file: file.update(groupId=123))
        for change in changes:
            self.api.files = copy.deepcopy(original)
            change(self.api.files[0])
            with self.subTest(change=change), self.assertRaises(ValueError):
                self.prepare()
        self.assertFalse(self.api.writes)

    def test_authenticated_identity_must_agree_with_public_target(self):
        request = self.api.request

        def wrong_id(path, **kwargs):
            result = request(path, **kwargs)
            if '/games/' in path:
                result['data']['id'] = 'wrong'
            return result

        self.api.request = wrong_id
        with self.assertRaisesRegex(ValueError, 'different mod'):
            self.prepare()

    def test_authenticated_existing_version_blocks_upload_when_public_listing_omits_it(self):
        self.api.published()
        query = self.api.query

        def public_baseline_only(text, variables=None):
            result = query(text, variables)
            if 'GuildChestFiles' in text:
                result['modFiles'] = result['modFiles'][:1]
            return result

        self.api.query = public_baseline_only
        # No file-intent artifact exists, as with a manual upload or expired artifact.
        self.assertFalse(self.github.records['file'])
        with self.assertRaisesRegex(nexus.Pending, 'authenticated Nexus API already lists this version'):
            self.prepare()
        self.assertFalse(list(self.directory.iterdir()))
        self.assertFalse(self.api.writes)
        # Once public indexing catches up, the same version can be reconciled.
        self.api.query = query
        self.assertEqual(self.prepare()['upload_needed'], 'false')

    def test_authenticated_duplicate_versions_are_rejected_even_with_one_public_match(self):
        self.api.published()
        request = self.api.request

        def duplicate_version(path, **kwargs):
            result = request(path, **kwargs)
            if path.endswith('/versions'):
                duplicate = {**result['data']['versions'][-1], 'id': 'other-id', 'game_scoped_id': '9999'}
                result['data']['versions'].append(duplicate)
            return result

        self.api.request = duplicate_version
        with self.assertRaisesRegex(ValueError, 'Multiple authenticated Nexus'):
            self.prepare()
        self.assertFalse(list(self.directory.iterdir()))
        self.assertFalse(self.api.writes)

    def test_requested_version_must_have_the_same_file_identity_in_both_apis(self):
        self.api.published()
        request = self.api.request

        def different_file(path, **kwargs):
            result = request(path, **kwargs)
            if path.endswith('/versions'):
                result['data']['versions'][-1]['game_scoped_id'] = '9999'
            return result

        self.api.request = different_file
        with self.assertRaisesRegex(ValueError, 'identity disagree'):
            self.prepare()
        self.assertFalse(self.api.writes)

    def test_preparation_writes_exact_bytes_and_no_remote_metadata(self):
        result = self.prepare()
        self.assertEqual(result['upload_needed'], 'true')
        self.assertEqual(Path(result['filename']).read_bytes(), self.release.contents)
        self.assertIn(self.release.manifest['dependencies'][0], result['description'])
        self.assertIn('/releases/tag/v1.2.3', result['description'])
        self.assertFalse(self.api.writes)

    def test_matching_existing_file_skips_upload_and_changelog_is_reconciled(self):
        self.prepare()
        file = self.api.published()
        self.verify()
        self.allow_notes()
        result = self.finalize()
        self.assertIn('changelog verified', result)
        writes = len(self.api.writes)
        self.assertEqual(self.prepare()['upload_needed'], 'false')
        self.assertEqual(self.verify()['notes_needed'], 'false')
        self.finalize()
        self.assertEqual(len(self.api.writes), writes)
        self.assertEqual(file['changelogText'], nexus.changelog(self.release).splitlines())

    def test_different_archive_size_or_unavailable_hash_never_allows_upload(self):
        file = self.api.published()
        file['sizeInBytes'] = '999'
        with self.assertRaisesRegex(ValueError, 'different archive size'):
            self.prepare()
        file['sizeInBytes'] = str(len(self.release.contents))
        self.api.hash_available = False
        with self.assertRaisesRegex(nexus.Pending, 'hash is unavailable or differs'):
            self.prepare()
        self.assertFalse(self.api.writes)

    def test_hash_lookup_must_match_exact_game_mod_file_and_group(self):
        self.api.published()
        query = self.api.query

        def other_group(text, variables=None):
            result = query(text, variables)
            if 'GuildChestHash' in text:
                result['fileHashes'][0]['modFile'] = {**result['fileHashes'][0]['modFile'], 'groupId': 22}
            return result

        self.api.query = other_group
        with self.assertRaises(nexus.Pending):
            self.prepare()

    def test_duplicate_and_newer_versions_are_rejected(self):
        self.api.published()
        self.api.files.append(copy.deepcopy(self.api.files[-1]))
        with self.assertRaisesRegex(ValueError, 'Multiple Nexus'):
            self.prepare()
        self.api.files.pop()
        self.api.files[-1]['version'] = '1.2.4'
        with self.assertRaisesRegex(ValueError, 'newer Nexus'):
            self.prepare()

    def test_newer_page_version_also_blocks_an_older_primary_upload(self):
        self.api.files[0]['mod']['version'] = '2.0.0'
        with self.assertRaisesRegex(ValueError, 'newer Nexus'):
            self.prepare()
        self.assertFalse(self.api.writes)

    def test_retry_can_preserve_requirements_from_a_predecessor_hidden_in_public_listing(self):
        self.api.published()
        query = self.api.query

        def public_current_only(text, variables=None):
            result = query(text, variables)
            if 'GuildChestFiles' in text:
                result['modFiles'] = result['modFiles'][-1:]
            return result

        self.api.query = public_current_only
        self.assertEqual(self.prepare()['upload_needed'], 'false')
        state = json.loads((self.directory / 'state.json').read_text())
        self.assertEqual(state['predecessor'], self.api.files[0]['uid'])
        self.assertTrue(state['dependencies']['dependency_definitions'])

    def test_missing_baseline_preparation_is_rejected_inside_helper(self):
        manifest = {**self.release.manifest, 'version_number': '1.1.0'}
        baseline = ReleaseAssets('v1.1.0', manifest, 'GuildChest-1.1.0.zip',
                                 self.release.contents, self.release.sha256, self.release.notes)
        with self.assertRaisesRegex(ValueError, 'greater than 1.1.0'):
            nexus.prepare(baseline, TARGET, self.api, self.github, self.directory)
        with self.assertRaisesRegex(ValueError, 'greater than 1.1.0'):
            nexus.finalize(baseline, TARGET, self.api, self.github, self.directory)
        self.assertFalse(self.api.calls)

    def test_ambiguous_prior_file_attempt_blocks_another_submission(self):
        self.github.records['file'] = [{'workflow_run': {'id': 1}}]
        with self.assertRaisesRegex(ValueError, 'prior Nexus file attempt'):
            self.prepare()
        self.assertFalse(self.api.writes)

    def test_known_file_is_recovered_despite_prior_upload_intent(self):
        self.api.published()
        self.github.records['file'] = [{'workflow_run': {'id': 1}}]
        self.assertEqual(self.prepare()['upload_needed'], 'false')

    def test_verification_waits_for_hash_index_and_never_reuploads(self):
        self.prepare()
        self.api.published()
        self.api.hash_available = False
        sleeps = []

        def indexed(delay):
            sleeps.append(delay)
            self.api.hash_available = True

        result = nexus.verify(self.release, TARGET, self.api, self.github, self.directory, sleep=indexed)
        self.assertEqual(result['notes_needed'], 'true')
        self.assertEqual(sleeps, [10])
        self.assertFalse(self.api.writes)

    def test_unconfirmed_upload_times_out_without_creating_another_version(self):
        self.prepare()
        with self.assertRaisesRegex(nexus.Pending, 'not yet visible'):
            self.verify()
        self.assertFalse(self.api.writes)

    def test_modified_local_archive_or_state_blocks_metadata_writes(self):
        self.prepare()
        (self.directory / self.release.filename).write_bytes(b'changed')
        with self.assertRaisesRegex(ValueError, 'ZIP changed'):
            self.verify()
        (self.directory / self.release.filename).write_bytes(self.release.contents)
        state = json.loads((self.directory / 'state.json').read_text())
        state['target']['file_id'] = '123'
        (self.directory / 'state.json').write_text(json.dumps(state))
        with self.assertRaisesRegex(ValueError, 'state does not match'):
            self.verify()

    def test_conflicting_notes_fail_without_appending(self):
        file = self.publish_and_verify()
        file['changelogText'] = ['Manually edited notes']
        with self.assertRaisesRegex(ValueError, 'changelog differs'):
            self.finalize()
        self.assertFalse(self.api.writes)

    def test_lost_successful_notes_response_is_reconciled_once(self):
        self.publish_and_verify()
        self.api.append_behavior = 'lost response'
        self.assertIn('changelog verified', self.finalize())
        self.assertEqual(sum(call[0] == 'POST' for call in self.api.writes), 1)

    def test_unconfirmed_notes_append_cannot_be_blindly_repeated(self):
        self.publish_and_verify()
        self.api.append_behavior = 'rejected'
        with self.assertRaisesRegex(ValueError, 'changelog publication is unconfirmed'):
            self.finalize()
        with self.assertRaisesRegex(ValueError, 'prior Nexus notes attempt'):
            self.verify()
        with self.assertRaisesRegex(ValueError, 'append was already attempted'):
            self.finalize()
        self.assertEqual(sum(call[0] == 'POST' for call in self.api.writes), 1)

    def test_changelog_requires_current_actions_intent(self):
        self.prepare()
        self.api.published()
        self.verify()
        self.github.records['notes'] = [{'workflow_run': {'id': 12}}]
        with self.assertRaisesRegex(ValueError, 'current GitHub Actions'):
            self.finalize()
        self.assertFalse(self.api.writes)

    def test_file_requirements_and_dlc_are_preserved_on_first_release_update(self):
        file = self.publish_and_verify()
        expected = nexus.dependency_payload(self.api, self.api.files[0]['uid'])
        self.api.put_loses_response = True
        self.finalize()
        self.assertEqual(nexus.dependency_payload(self.api, file['uid']), expected)
        self.assertEqual(sum(call[0] == 'PUT' for call in self.api.writes), 2)

    def test_interrupted_predecessor_cannot_erase_requirements_in_the_next_release(self):
        self.prepare()
        predecessor = self.api.published()  # The run stops before requirement completion.
        saved_state = (self.directory / 'state.json').read_bytes()
        expected = copy.deepcopy(self.api.dependencies[self.api.files[0]['uid']])
        self.next_release()
        for missing in (('dependency_definitions', 'dlc_dependency_definitions'),
                        ('dependency_definitions',), ('dlc_dependency_definitions',)):
            self.api.dependencies[predecessor['uid']] = copy.deepcopy(expected)
            for key in missing:
                self.api.dependencies[predecessor['uid']][key] = []
            with self.subTest(missing=missing), self.assertRaisesRegex(ValueError, 'predecessor 1.2.3 has incomplete requirements'):
                self.prepare()
            self.assertFalse((self.directory / self.release.filename).exists())
            self.assertEqual((self.directory / 'state.json').read_bytes(), saved_state)
            self.assertFalse(self.api.writes)

    def test_missing_requirements_are_detected_through_multiple_incomplete_predecessors(self):
        earlier = file_record('1.2.2', 25734, primary=0, category='ARCHIVED')
        self.api.files.append(earlier)
        self.api.dependencies[earlier['uid']] = copy.deepcopy(EMPTY_DEPENDENCIES)
        predecessor = self.api.published()
        self.next_release()
        with self.assertRaisesRegex(ValueError, 'predecessor 1.2.3 has incomplete requirements'):
            self.prepare()
        self.assertEqual(self.api.dependencies[predecessor['uid']], EMPTY_DEPENDENCIES)
        self.assertFalse(self.api.writes)

    def test_repaired_predecessor_requirements_are_preserved_by_the_next_release(self):
        self.prepare()
        state = json.loads((self.directory / 'state.json').read_text())
        predecessor = self.api.published()
        self.next_release()
        with self.assertRaisesRegex(ValueError, 'incomplete requirements'):
            self.prepare()
        # Reconcile the earlier run with the intended definitions it saved.
        nexus.complete_dependencies(self.api, predecessor['uid'], state['dependencies'])
        writes_before_preparation = len(self.api.writes)
        self.assertEqual(self.prepare()['upload_needed'], 'true')
        self.assertEqual(len(self.api.writes), writes_before_preparation)
        current = self.api.published()
        self.verify()
        self.allow_notes()
        self.finalize()
        self.assertEqual(nexus.dependency_payload(self.api, current['uid']), state['dependencies'])

    def test_genuinely_empty_requirements_since_manual_baseline_remain_supported(self):
        self.api.dependencies[self.api.files[0]['uid']] = copy.deepcopy(EMPTY_DEPENDENCIES)
        self.api.published()
        self.next_release()
        self.assertEqual(self.prepare()['upload_needed'], 'true')
        state = json.loads((self.directory / 'state.json').read_text())
        self.assertEqual(state['dependencies'], EMPTY_DEPENDENCIES)
        self.assertFalse(self.api.writes)

    def test_empty_predecessor_without_baseline_history_is_rejected(self):
        self.api.published()
        self.api.files = self.api.files[-1:]
        self.next_release()
        with self.assertRaisesRegex(ValueError, 'Cannot establish the manual Nexus baseline'):
            self.prepare()
        self.assertFalse(self.api.writes)

    def test_editorial_requirements_are_never_overwritten(self):
        file = self.publish_and_verify()
        self.api.dependencies[file['uid']] = raw_dependencies('9999')
        with self.assertRaisesRegex(ValueError, 'conflicting requirements'):
            self.finalize()
        self.assertFalse(self.api.writes)

    def test_legacy_mod_requirements_do_not_create_file_requirements(self):
        self.api.files[0]['mod']['legacyModRequirementsEnabled'] = True
        self.publish_and_verify()
        self.finalize()
        self.assertFalse(any(call[0] == 'PUT' for call in self.api.writes))

    def test_changelog_preserves_sections_wraps_bullets_and_pins_links(self):
        text = nexus.changelog(self.release)
        self.assertEqual(text.splitlines()[:3], ['Mod changes', 'A fix with wrapped text.', 'Other changes'])
        self.assertIn('blob/v1.2.3/docs/releases/../DEVELOPMENT.md', text)
        self.assertNotIn('[guide]', text)

    def test_cli_rejects_baseline_prepare_before_loading_or_writing(self):
        environment = {'NEXUSMODS_MOD_ID': TARGET.mod_id, 'NEXUSMODS_FILE_ID': TARGET.file_id,
                       'NEXUSMODS_MOD_PAGE_ID': TARGET.page_id, 'NEXUSMODS_API_KEY': 'fixture',
                       'NEXUSMODS_PUBLISH_ENABLED': 'true'}
        with patch.dict(os.environ, environment), patch.object(sys, 'argv', ['publish_nexusmods.py', 'v1.1.0', '--prepare']), \
                patch.object(nexus, 'load_release') as load, self.assertRaisesRegex(SystemExit, 'greater than 1.1.0'):
            nexus.main()
        load.assert_not_called()


class TransportTests(unittest.TestCase):
    def test_nexus_key_never_leaves_fixed_api_origin_and_redirects_are_rejected(self):
        api = nexus.NexusHttp('private-key')
        with patch.object(api.opener, 'open') as request:
            request.return_value.__enter__.return_value.read.return_value = b'{"data": {}}'
            api.request('/v3/mods/123/files')
            sent = request.call_args.args[0]
            self.assertEqual(sent.full_url, nexus.API + '/v3/mods/123/files')
            self.assertEqual(sent.get_header('Apikey'), 'private-key')
            with self.assertRaisesRegex(ValueError, 'Invalid Nexus'):
                api.request('https://wrong.example/')
        with self.assertRaisesRegex(ValueError, 'no credentials were forwarded'):
            nexus.NoRedirect().redirect_request(None, None, 302, '', {}, 'https://wrong.example/')

    def test_public_graphql_has_no_credential_and_rejects_partial_responses(self):
        api = nexus.NexusHttp()
        with patch.object(api.opener, 'open') as request:
            request.return_value.__enter__.return_value.read.return_value = b'{"data": {}, "errors": [{"message": "broken"}]}'
            with self.assertRaisesRegex(ValueError, 'incomplete data'):
                api.query('query Public { game { id } }')
            self.assertIsNone(request.call_args.args[0].get_header('Apikey'))
        with self.assertRaisesRegex(ValueError, 'read-only'):
            api.query('mutation Dangerous { anything }')

    def test_http_errors_do_not_become_absence_or_disclose_response_secrets(self):
        api = nexus.NexusHttp('private-key')
        for code in (401, 403, 404, 429, 500):
            error = HTTPError(nexus.API, code, 'private-key', {}, None)
            with self.subTest(code=code), patch.object(api.opener, 'open', side_effect=error):
                with self.assertRaises(ValueError) as caught:
                    api.request('/v3/mods/123/files')
                self.assertIn(str(code), str(caught.exception))
                self.assertNotIn('private-key', str(caught.exception))

    def test_github_cross_origin_redirect_strips_both_credential_headers(self):
        original = Request('https://api.github.com/assets/1', headers={'Authorization': 'private', 'apikey': 'other-private'})
        redirected = CredentialRedirect().redirect_request(original, None, 302, '', {}, 'https://release-assets.githubusercontent.com/file')
        self.assertIsNone(redirected.get_header('Authorization'))
        self.assertIsNone(redirected.get_header('Apikey'))
        with self.assertRaisesRegex(ValueError, 'HTTPS'):
            CredentialRedirect().redirect_request(original, None, 302, '', {}, 'http://wrong.example/file')


if __name__ == '__main__':
    unittest.main()
