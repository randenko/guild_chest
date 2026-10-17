#!/usr/bin/env python3
"""Validate published assets, prepare the official Nexus action, and finish metadata."""
import argparse
from dataclasses import asdict, dataclass
import html
import json
import os
from pathlib import Path
import re
import time
from urllib.error import HTTPError, URLError
from urllib.request import HTTPRedirectHandler, Request, build_opener

from package_format import LINK, REPOSITORY, VERSION, release_links
from release_assets import Http, ROOT, load_release

API = 'https://api.nexusmods.com'
GITHUB = 'https://api.github.com/repos/' + REPOSITORY.removeprefix('https://github.com/')
BASELINE = (1, 1, 0)
FILE_FIELDS = '''fileId groupId modId modUid uid version category primary
    sizeInBytes changelogText requirementsAlert scannedV2
    mod { uid modId gameId name status version legacyModRequirementsEnabled }'''


class Pending(ValueError):
    """Remote publication or hash indexing is not yet visible."""


class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        raise ValueError('Unexpected Nexus API redirect; no credentials were forwarded.')


class NexusHttp:
    def __init__(self, token=''):
        self.token = token
        self.opener = build_opener(NoRedirect())

    def request(self, path, *, method='GET', body=None):
        # All requests, including authenticated writes, stay at this fixed origin.
        if not path.startswith(('/v3/', '/v2/graphql')) or '?' in path or '#' in path:
            raise ValueError('Invalid Nexus API path.')
        headers = {'User-Agent': 'GuildChest-release', 'Accept': 'application/json',
                   'Content-Type': 'application/json', 'Cache-Control': 'no-cache'}
        if self.token:
            headers['apikey'] = self.token
        data = None if body is None else json.dumps(body).encode('utf-8')
        try:
            with self.opener.open(Request(API + path, headers=headers, method=method, data=data), timeout=60) as response:
                contents = response.read()
            return json.loads(contents) if contents else None
        except HTTPError as error:
            # Do not print response bodies, keys, request headers, or signed URLs.
            raise ValueError(f'Nexus {method} {path} failed with HTTP {error.code}.') from error
        except (URLError, TimeoutError) as error:
            raise ValueError(f'Nexus {method} {path} failed; reconcile remote state before retrying writes.') from error

    def query(self, query, variables=None):
        if not query.lstrip().startswith('query '):
            raise ValueError('Only read-only GraphQL queries are accepted.')
        result = self.request('/v2/graphql', method='POST', body={'query': query, 'variables': variables or {}})
        if not isinstance(result, dict) or result.get('errors') or not isinstance(result.get('data'), dict):
            raise ValueError('Nexus GraphQL query failed or returned incomplete data.')
        return result['data']


@dataclass(frozen=True)
class Target:
    mod_id: str
    file_id: str
    page_id: str

    def __post_init__(self):
        for name, value in asdict(self).items():
            if not re.fullmatch(r'[1-9][0-9]*', value):
                raise ValueError(f'NEXUSMODS_{"MOD_PAGE_ID" if name == "page_id" else name.upper()} is missing or invalid.')

    @property
    def listing(self):
        return f'https://www.nexusmods.com/valheim/mods/{self.page_id}'


def version_number(version):
    if not isinstance(version, str) or not re.fullmatch(VERSION, version):
        raise ValueError('Only stable X.Y.Z Nexus versions are accepted.')
    return tuple(map(int, version.split('.')))


def authorize(tag, enabled, token):
    if not re.fullmatch(f'v{VERSION}', tag) or version_number(tag[1:]) <= BASELINE:
        raise ValueError('Nexus writes require a stable version greater than 1.1.0; the first release is manual.')
    if enabled != 'true':
        raise ValueError('NEXUSMODS_PUBLISH_ENABLED must be true for publishing.')
    if not token.strip():
        raise ValueError('NEXUSMODS_API_KEY is missing.')


def read_files(api, target):
    game = api.query('query GuildChestGame { game(domainName: "valheim") { id domainName } }')['game']
    if not game or game['domainName'] != 'valheim':
        raise ValueError('Cannot resolve the Valheim Nexus game.')
    data = api.query('query GuildChestFiles($modId: ID!, $gameId: ID!) { '
                     'modFiles(modId: $modId, gameId: $gameId) { ' + FILE_FIELDS + ' } }',
                     {'modId': target.page_id, 'gameId': str(game['id'])})
    files = data['modFiles']
    if not isinstance(files, list) or not files:
        raise ValueError('Nexus page has no visible files; verify the manual launch and target IDs.')
    for file in files:
        mod = file['mod']
        if (str(file['modUid']) != target.mod_id or str(file['modId']) != target.page_id or
                str(mod['uid']) != target.mod_id or str(mod['modId']) != target.page_id or
                str(mod['gameId']) != str(game['id']) or mod['status'].lower() != 'published'):
            raise ValueError('Nexus page, game, or API mod ID does not match the configured target.')
    group = [file for file in files if str(file['groupId']) == target.file_id]
    if not group:
        raise ValueError('NEXUSMODS_FILE_ID does not belong to the configured mod page.')
    return game, group


def authenticated_versions(api, target):
    mod = api.request(f'/v3/games/valheim/mods/{target.page_id}')['data']
    files = api.request(f'/v3/mods/{target.mod_id}/files')['data']['mod_files']
    if (str(mod['id']) != target.mod_id or str(mod['game_scoped_id']) != target.page_id or
            not any(str(file['id']) == target.file_id for file in files)):
        raise ValueError('Authenticated Nexus API returned a different mod or main file.')
    return api.request(f'/v3/mod-files/{target.file_id}/versions')['data']['versions']


def select_version(files, release):
    matches = [file for file in files if file['version'] == release.version]
    if len(matches) > 1:
        raise ValueError('Multiple Nexus files have this version; resolve the duplicate before retrying.')
    for file in files:
        if (version_number(file['version']) > version_number(release.version) or
                version_number(file['mod']['version']) > version_number(release.version)):
            raise ValueError('A newer Nexus version already exists; refusing to replace it with an older release.')
    return matches[0] if matches else None


def verify_identity(api, target, game, file, release):
    if file['category'] != 'MAIN' or file['primary'] != 1:
        raise ValueError('The intended Nexus version is not the primary main file.')
    if int(file['sizeInBytes']) != len(release.contents):
        raise ValueError('Existing Nexus version has a different archive size; choose a new version.')
    hashes = api.query('query GuildChestHash($md5s: [String!]!) { fileHashes(md5s: $md5s) { '
                       'md5 fileSize gameId modFileId modFile { modId fileId groupId uid } } }',
                       {'md5s': [release.md5]})['fileHashes']
    if not isinstance(hashes, list):
        raise ValueError('Nexus archive hash lookup returned incomplete data.')
    for entry in hashes:
        match = entry.get('modFile')
        if (match and entry['md5'] == release.md5 and str(entry['gameId']) == str(game['id']) and
                str(entry['modFileId']) == str(file['fileId']) and str(match['fileId']) == str(file['fileId']) and
                str(match['modId']) == target.page_id and str(match['groupId']) == target.file_id and
                str(match['uid']) == str(file['uid']) and int(entry['fileSize']) == len(release.contents)):
            return
    raise Pending('Nexus archive hash is unavailable or differs from GitHub; no further submission is allowed until reconciled.')


def changelog(release):
    text = release_links(release.notes, release.version, 'docs/releases')
    text = LINK.sub(lambda match: match[1].removeprefix('[').removesuffix('](') + f' ({match[2]})', text)
    entries = []
    for paragraph in re.split(r'\n\s*\n', text.strip()):
        # A wrapped Markdown bullet becomes one Nexus changelog entry.
        for item in re.split(r'\n(?=- )', paragraph):
            item = re.sub(r'^#+\s*|^-\s*', '', ' '.join(item.split()))
            if item == f'Guild Chest {release.version}':
                continue
            entries.append(item.replace('**', '').replace('`', ''))
    return '\n'.join(entries)


def normalized_notes(entries):
    if not isinstance(entries, list) or any(not isinstance(entry, str) for entry in entries):
        raise ValueError('Nexus changelog response is incomplete.')
    text = '\n'.join(html.unescape(entry) for entry in entries)
    text = re.sub(r'<br\s*/?>', '\n', text, flags=re.IGNORECASE)
    return [' '.join(line.split()) for line in text.splitlines() if line.strip()]


def notes_needed(file, release):
    actual = normalized_notes(file['changelogText'])
    if not actual:
        return True
    if actual != normalized_notes(changelog(release).splitlines()):
        raise ValueError('Nexus changelog differs from the tagged release notes; inspect it before retrying (nothing appended).')
    return False


def intent_name(release, stage):
    return f'nexusmods-{stage}-{release.tag}'


def intents(http, release, stage):
    response = http.json(f'{GITHUB}/actions/artifacts?name={intent_name(release, stage)}&per_page=100')
    if not isinstance(response.get('total_count'), int) or not isinstance(response.get('artifacts'), list):
        raise ValueError('Cannot verify prior Nexus publication attempts.')
    return response


def require_no_intent(http, release, stage):
    if intents(http, release, stage)['total_count']:
        raise ValueError(f'A prior Nexus {stage} attempt is unconfirmed. Inspect Nexus and the '
                         f'{intent_name(release, stage)} Actions artifact before authorizing another attempt.')


def dependency_payload(api, version_id):
    data = api.request(f'/v3/mod-file-versions/{version_id}/dependencies')
    payload = {
        'dependency_definitions': [
            {'ranges': [{'min_version_id': str(item['min_version']['id']),
                         'max_version_id': None if item['max_version'] is None else str(item['max_version']['id'])}
                        for item in definition['ranges']]}
            for definition in data['dependency_definitions']],
        'dlc_dependency_definitions': [
            {'dlc_ids': [str(item['dlc_id']) for item in definition['dlc_targets']]}
            for definition in data['dlc_dependency_definitions']],
    }
    # Definition and alternative order has no semantic meaning in Nexus.
    for definition in payload['dependency_definitions']:
        definition['ranges'].sort(key=lambda item: json.dumps(item, sort_keys=True))
    for definition in payload['dlc_dependency_definitions']:
        definition['dlc_ids'].sort()
    for definitions in payload.values():
        definitions.sort(key=lambda item: json.dumps(item, sort_keys=True))
    return payload


def complete_dependencies(api, version_id, expected):
    if expected is None:
        return
    actual = dependency_payload(api, version_id)
    for key, endpoint in (('dependency_definitions', 'ranges'), ('dlc_dependency_definitions', 'dlc')):
        if actual[key] == expected[key]:
            continue
        if actual[key]:
            raise ValueError('New Nexus file has conflicting requirements; refusing to overwrite editorial changes.')
        # PUT replaces this definition set and can safely be reconciled after a lost response.
        try:
            api.request(f'/v3/mod-file-versions/{version_id}/dependencies/{endpoint}', method='PUT', body={key: expected[key]})
        except ValueError:
            if dependency_payload(api, version_id)[key] != expected[key]:
                raise
    if dependency_payload(api, version_id) != expected:
        raise ValueError('Nexus file requirements are not yet verified; retry metadata completion.')


def predecessor_dependencies(api, target, versions, previous):
    expected = dependency_payload(api, str(previous['id']))
    missing = [key for key, definitions in expected.items() if not definitions]
    if not missing or version_number(previous['version']) == BASELINE:
        return expected
    history = [item for item in versions
               if BASELINE <= version_number(item['version']) < version_number(previous['version'])]
    if sum(version_number(item['version']) == BASELINE for item in history) != 1:
        raise ValueError('Cannot establish the manual Nexus baseline requirements; reconcile the predecessor before publishing.')
    # Each requirements PUT is atomic for one definition set, but a run can stop
    # before either PUT. Look through archived history to distinguish genuinely
    # empty sets from omissions propagated by an interrupted metadata update.
    for earlier in sorted(history, key=lambda item: version_number(item['version']), reverse=True):
        if str(earlier['file']['id']) != target.file_id:
            raise ValueError('Nexus requirements history contains a different main file.')
        definitions = dependency_payload(api, str(earlier['id']))
        for key in missing:
            if definitions[key]:
                raise ValueError(f'Nexus predecessor {previous["version"]} has incomplete requirements '
                                 f'({key} is empty but {earlier["version"]} requires it). '
                                 'Reconcile that predecessor using its saved intent or the last complete '
                                 'version\'s definitions before publishing another release.')
    return expected


def inspect(release, target, api, http, *, authenticated=False):
    game, files = read_files(api, target)
    file = select_version(files, release)
    versions = authenticated_versions(api, target) if authenticated else None
    if versions is not None and any(version_number(item['version']) > version_number(release.version) for item in versions):
        raise ValueError('A newer Nexus version already exists; refusing to replace it with an older release.')
    same_versions = [] if versions is None else [item for item in versions if item['version'] == release.version]
    if len(same_versions) > 1:
        raise ValueError('Multiple authenticated Nexus files have this version; resolve the duplicate before retrying.')
    if same_versions and file is None:
        raise Pending('The authenticated Nexus API already lists this version, but the public API omits it. '
                      'Reconcile the API disagreement before retrying; another upload is not allowed.')
    if file:
        verify_identity(api, target, game, file, release)
        if versions is not None:
            if (len(same_versions) != 1 or str(same_versions[0]['game_scoped_id']) != str(file['fileId']) or
                    str(same_versions[0]['file']['id']) != target.file_id):
                raise ValueError('Nexus public file and authenticated file-version identity disagree.')
    else:
        require_no_intent(http, release, 'file')
    return files, file, versions


def preflight(release, target, api, http, *, authenticated=False):
    _, file, _ = inspect(release, target, api, http, authenticated=authenticated)
    if version_number(release.version) < BASELINE:
        raise ValueError('Nexus publishing starts with the manually released 1.1.0 baseline.')
    baseline = version_number(release.version) == BASELINE
    if baseline and not file:
        raise ValueError('The manual Nexus 1.1.0 baseline is missing; automation will not upload it.')
    needed = None if baseline or not file else notes_needed(file, release)
    message = (f'Preflight passed: {release.tag}; source SHA-256 {release.sha256}; '
               f'Nexus archive identity: {"matching MD5, size, game and file IDs" if file else "not yet published"}; '
               f'changelog: {"manual baseline retained" if baseline else "missing" if needed or not file else "matching"}; '
               f'credential checks: {"authenticated reads passed" if authenticated else "not tested (public reads only)"}. '
               f'Target: {target.listing}')
    return message


def prepare(release, target, api, http, directory):
    if version_number(release.version) <= BASELINE:
        raise ValueError('Nexus preparation requires a version greater than 1.1.0.')
    files, file, versions = inspect(release, target, api, http, authenticated=True)
    if file:
        notes_needed(file, release)  # Fail on a conflict before any workflow write.
    # The authenticated version list also includes archived predecessors that
    # may be omitted from a public listing after the action archives them.
    predecessors = [item for item in versions if version_number(item['version']) < version_number(release.version)]
    if not predecessors:
        raise ValueError('Cannot find a preceding Nexus version from which to preserve requirements.')
    previous = max(predecessors, key=lambda item: version_number(item['version']))
    if sum(item['version'] == previous['version'] for item in predecessors) != 1:
        raise ValueError('Multiple preceding Nexus versions exist; resolve the duplicate requirements source.')
    if str(previous['file']['id']) != target.file_id:
        raise ValueError('Cannot resolve the preceding Nexus API file-version ID.')
    previous_id = str(previous['id'])
    legacy = files[0]['mod']['legacyModRequirementsEnabled']
    expected = None if legacy else predecessor_dependencies(api, target, versions, previous)
    directory = Path(directory).resolve()
    directory.mkdir(parents=True, exist_ok=True)
    (directory / release.filename).write_bytes(release.contents)
    state = {'tag': release.tag, 'sha256': release.sha256, 'target': asdict(target),
             'dependencies': expected, 'predecessor': previous_id}
    (directory / 'state.json').write_text(json.dumps(state, indent=2) + '\n', encoding='utf-8')
    (directory / 'file-intent.json').write_text(json.dumps(state, indent=2) + '\n', encoding='utf-8')
    dependencies = ', '.join(release.manifest['dependencies'])
    return {'upload_needed': str(file is None).lower(), 'version': release.version,
            'filename': str(directory / release.filename), 'file_intent': intent_name(release, 'file'),
            'description': f'Install on every client and the host/server with matching Guild Chest versions. '
                           f'Requires {dependencies}. Release notes: {REPOSITORY}/releases/tag/{release.tag}'}


def read_state(directory, release, target):
    directory = Path(directory).resolve()
    state = json.loads((directory / 'state.json').read_text(encoding='utf-8'))
    if (state['tag'], state['sha256'], state['target']) != (release.tag, release.sha256, asdict(target)):
        raise ValueError('Prepared Nexus state does not match the verified release and target.')
    if (directory / release.filename).read_bytes() != release.contents:
        raise ValueError('Prepared Nexus ZIP changed after validation.')
    return state


def wait_for_file(release, target, api, http, sleep):
    for attempt in range(7):
        game, files = read_files(api, target)
        file = select_version(files, release)
        try:
            if file is None:
                raise Pending('The requested Nexus version is not yet visible.')
            verify_identity(api, target, game, file, release)
            if file['mod']['version'] != release.version or file['requirementsAlert'] != 1:
                raise Pending('Nexus page version or requirements popup has not updated yet.')
            return file
        except Pending:
            if attempt == 6:
                raise
            sleep(10)


def verify(release, target, api, http, directory, *, sleep=time.sleep):
    state = read_state(directory, release, target)
    file = wait_for_file(release, target, api, http, sleep)
    versions = authenticated_versions(api, target)
    matches = [item for item in versions if item['version'] == release.version]
    if (len(matches) != 1 or str(matches[0]['file']['id']) != target.file_id or
            str(matches[0]['game_scoped_id']) != str(file['fileId'])):
        raise ValueError('Cannot verify the uploaded Nexus file-version ID.')
    needed = notes_needed(file, release)
    if needed:
        require_no_intent(http, release, 'notes')
    state.update({'version_id': str(matches[0]['id']), 'notes_needed': needed})
    Path(directory, 'state.json').write_text(json.dumps(state, indent=2) + '\n', encoding='utf-8')
    Path(directory, 'notes-intent.json').write_text(json.dumps(state, indent=2) + '\n', encoding='utf-8')
    return {'notes_needed': str(needed).lower(), 'notes_intent': intent_name(release, 'notes'),
            'version_id': state['version_id']}


def finalize(release, target, api, http, directory, *, run_id='', sleep=time.sleep):
    if version_number(release.version) <= BASELINE:
        raise ValueError('Nexus metadata writes require a version greater than 1.1.0.')
    state = read_state(directory, release, target)
    if 'version_id' not in state:
        raise ValueError('Verify the Nexus upload before completing metadata.')
    file = wait_for_file(release, target, api, http, sleep)
    needed = notes_needed(file, release)
    if needed:
        if state.get('notes_submitted'):
            raise ValueError('A changelog append was already attempted from this prepared state; reconcile it before retrying.')
        records = intents(http, release, 'notes')['artifacts']
        if not run_id or not state['notes_needed'] or not any(
                str(item.get('workflow_run', {}).get('id')) == run_id for item in records):
            raise ValueError('A current GitHub Actions notes-intent artifact is required before appending a changelog.')
    complete_dependencies(api, state['version_id'], state['dependencies'])
    if needed:
        # Append exactly once. An error may mean a lost response; read back, never repost.
        state['notes_submitted'] = True
        Path(directory, 'state.json').write_text(json.dumps(state, indent=2) + '\n', encoding='utf-8')
        try:
            api.request(f'/v3/mods/{target.mod_id}/changelogs', method='POST',
                        body={'version': release.version, 'changelog': changelog(release)})
        except ValueError:
            pass
        for attempt in range(7):
            _, files = read_files(api, target)
            file = select_version(files, release)
            if file and not notes_needed(file, release):
                break
            if attempt == 6:
                raise ValueError('Nexus file is verified but changelog publication is unconfirmed. '
                                 'Inspect the notes-intent artifact and Nexus before retrying; no duplicate was appended.')
            sleep(10)
    return (f'Nexus publication verified: {release.tag}; source SHA-256 {release.sha256}; '
            f'Nexus MD5/size/game/file identity matched; file-version ID {state["version_id"]}; '
            f'requirements preserved; changelog verified. {target.listing}')


def emit(values):
    for key, value in values.items():
        if '\n' in value or '\r' in value:
            raise ValueError('Unexpected multiline workflow output.')
    output = os.environ.get('GITHUB_OUTPUT')
    if output:
        with Path(output).open('a', encoding='utf-8') as stream:
            for key, value in values.items():
                stream.write(f'{key}={value}\n')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('tag')
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument('--preflight', action='store_true', help='Read-only validation (default); public reads work without a key.')
    mode.add_argument('--prepare', action='store_true', help='Prepare verified inputs for the official upload action.')
    mode.add_argument('--verify', action='store_true', help='Reconcile the action result using read-only requests.')
    mode.add_argument('--finalize', action='store_true', help='Preserve requirements and append missing changelog entries once.')
    parser.add_argument('--directory', type=Path)
    args = parser.parse_args()
    try:
        token = os.environ.get('NEXUSMODS_API_KEY', '')
        target = Target(*(os.environ.get(name, '') for name in (
            'NEXUSMODS_MOD_ID', 'NEXUSMODS_FILE_ID', 'NEXUSMODS_MOD_PAGE_ID')))
        if args.prepare or args.verify or args.finalize:
            authorize(args.tag, os.environ.get('NEXUSMODS_PUBLISH_ENABLED', ''), token)
            if args.directory is None:
                raise ValueError('--directory is required for preparation, verification and completion.')
        http, api = Http(os.environ.get('GH_TOKEN', '')), NexusHttp(token)
        release = load_release(args.tag, http=http)
        if args.prepare:
            emit(prepare(release, target, api, http, args.directory))
            message = f'Prepared exact {release.tag} assets for Nexus; source SHA-256 {release.sha256}.'
        elif args.verify:
            emit(verify(release, target, api, http, args.directory))
            message = f'Nexus {release.tag} file identity verified; metadata completion pending.'
        elif args.finalize:
            message = finalize(release, target, api, http, args.directory, run_id=os.environ.get('GITHUB_RUN_ID', ''))
        else:
            message = preflight(release, target, api, http, authenticated=bool(token))
        print(message)
        if os.environ.get('GITHUB_STEP_SUMMARY'):
            with Path(os.environ['GITHUB_STEP_SUMMARY']).open('a', encoding='utf-8') as stream:
                stream.write(message + '\n')
    except (ValueError, OSError, KeyError, TypeError) as error:
        raise SystemExit(str(error)) from error


if __name__ == '__main__':
    main()
