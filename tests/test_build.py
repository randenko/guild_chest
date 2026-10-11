"""Run the real NUKE graph against disposable repositories and recording tools."""
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import unittest

PROJECT = Path(__file__).resolve().parents[1]
RUNNER = os.environ.get('GUILDCHEST_BUILD_RUNNER')
DOTNET = shutil.which('dotnet')

TOOL = '''import json
import os
from pathlib import Path
import subprocess
import sys

root = Path.cwd()
args = sys.argv[1:]
kind = 'dotnet' if Path(sys.argv[0]).name == 'dotnet' else 'python'
with (root / 'calls.jsonl').open('a') as output:
    output.write(json.dumps({'kind': kind, 'args': args, 'cwd': str(root)}) + '\\n')
if kind == 'dotnet':
    if args[0] == 'test' and os.environ.get('FAKE_FAIL_TEST'):
        sys.exit(3)
    if args[0] == 'build' and os.environ.get('FAKE_DOTNET_DIAGNOSTIC'):
        print(f'{root}/Broken.cs(3,5): error CS1002: ; expected')
        sys.exit(1)
elif args[0] != '-m':
    script = Path(args[0]).name
    if script == 'check_release.py':
        sys.exit(subprocess.run([sys.executable, *args]).returncode)
    if script == 'setup.py' and os.environ.get('USE_REAL_SETUP'):
        sys.exit(subprocess.run([sys.executable, *args]).returncode)
    if script == 'setup.py' and os.environ.get('FAKE_FAIL_SETUP'):
        sys.exit(4)
    if script == 'package.py':
        version = json.loads((root / 'manifest.json').read_text())['version_number']
        artifacts = root / 'artifacts'
        artifacts.mkdir(exist_ok=True)
        (artifacts / f'GuildChest-{version}.zip').write_bytes(b'fixture package')
'''


@unittest.skipUnless(RUNNER and DOTNET, 'Run the NUKE Test target to exercise build orchestration.')
class BuildTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name) / 'checkout with spaces'
        self.root.mkdir()
        (self.root / '.nuke').mkdir()
        (self.root / '.nuke/parameters.json').write_text('{}\n')
        (self.root / 'manifest.json').write_text('{"version_number":"1.2.3"}\n')
        (self.root / 'scripts').mkdir()
        shutil.copyfile(PROJECT / 'scripts/check_release.py', self.root / 'scripts/check_release.py')
        self.git('init', '--initial-branch=main')
        self.git('config', 'user.name', 'NUKE Test')
        self.git('config', 'user.email', 'nuke-test@example.invalid')
        self.git('add', 'manifest.json')
        self.git('commit', '-m', 'Release commit')
        self.git('update-ref', 'refs/remotes/origin/main', 'HEAD')
        tools = self.root / 'recording tools'
        tools.mkdir()
        for name in ('dotnet', 'python'):
            path = tools / name
            path.write_text(f'#!{sys.executable}\n{TOOL}')
            path.chmod(0o755)
        self.python = str(tools / 'python')
        self.env = {key: value for key, value in os.environ.items()
                    if not key.startswith(('GITHUB_', 'NUKE_', 'TEAMCITY_')) and key != 'CI'}
        self.env.update({
            'PATH': str(tools) + os.pathsep + self.env.get('PATH', ''),
            'DOTNET_EXE': str(tools / 'dotnet'),
            'NUKE_TELEMETRY_OPTOUT': '1',
            'GITHUB_OUTPUT': str(self.root / 'github-output'),
        })

    def git(self, *args):
        return subprocess.run(['git', *args], cwd=self.root, capture_output=True,
                              text=True, check=True).stdout.strip()

    def run_build(self, *args, success=True):
        result = subprocess.run([DOTNET, RUNNER, '--root', str(self.root), '--no-logo',
                                 '--python', self.python, *args],
                                cwd=self.root, env=self.env, capture_output=True,
                                text=True, timeout=30)
        if success:
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        else:
            self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
        trace = self.root / 'calls.jsonl'
        calls = [json.loads(line) for line in trace.read_text().splitlines()] if trace.exists() else []
        self.assertTrue(all(call['cwd'] == str(self.root) for call in calls))
        return calls, result

    @staticmethod
    def scripts(calls, name):
        return [call for call in calls if call['kind'] == 'python'
                and Path(call['args'][0]).name == name]

    @staticmethod
    def dotnet_calls(calls, command):
        return [call for call in calls if call['kind'] == 'dotnet' and call['args'][0] == command]

    def test_test_target_restores_only_core_tests_and_preserves_arguments(self):
        logger = 'trx;LogFileName=results with spaces.trx'
        results = str(self.root / 'results with spaces')
        calls, _ = self.run_build('--target', 'Test', '--configuration', 'Release',
                                 '--test-logger', logger, '--test-results-directory', results)
        restore, = self.dotnet_calls(calls, 'restore')
        self.assertEqual(restore['args'][1], str(self.root / 'tests/GuildChest.Tests/GuildChest.Tests.csproj'))
        self.assertIn('--locked-mode', restore['args'])
        test, = self.dotnet_calls(calls, 'test')
        self.assertEqual(test['args'][test['args'].index('--configuration') + 1], 'Release')
        self.assertEqual(test['args'][test['args'].index('--logger') + 1], logger)
        self.assertEqual(test['args'][test['args'].index('--results-directory') + 1], results)
        self.assertIn('--no-restore', test['args'])
        self.assertEqual(calls[-1]['args'], ['-m', 'unittest', 'discover', '-s', 'tests', '-p', 'test_*.py'])
        self.assertFalse(self.scripts(calls, 'setup.py'))
        self.assertFalse(self.scripts(calls, 'package.py'))

    def test_compile_and_package_share_dependencies_and_package_builds_release(self):
        managed = str(self.root / 'custom Managed')
        calls, _ = self.run_build('--target', 'Compile', 'Package', '--valheim-managed-dir', managed)
        setup, = self.scripts(calls, 'setup.py')
        self.assertEqual(setup['args'][1:], ['--valheim-managed-dir', managed])
        restore, = self.dotnet_calls(calls, 'restore')
        self.assertEqual(restore['args'][1], str(self.root / 'GuildChest.sln'))
        self.assertIn('--locked-mode', restore['args'])
        debug, release = self.dotnet_calls(calls, 'build')
        self.assertEqual(debug['args'][debug['args'].index('--configuration') + 1], 'Debug')
        self.assertEqual(release['args'][release['args'].index('--configuration') + 1], 'Release')
        self.assertEqual(release['args'][1], str(self.root / 'src/GuildChest/GuildChest.csproj'))
        for build in (debug, release):
            self.assertIn('--no-restore', build['args'])
            self.assertTrue(any('ValheimManagedDir=' + managed in arg for arg in build['args']))
        self.assertEqual(len(self.scripts(calls, 'package.py')), 1)
        self.assertLess(calls.index(release), calls.index(self.scripts(calls, 'package.py')[0]))

    def test_release_validates_before_downloads_and_writes_version_and_checksum(self):
        calls, _ = self.run_build('--target', 'Release', '--release-tag', 'v1.2.3')
        validate, = self.scripts(calls, 'check_release.py')
        self.assertEqual(calls[0], validate)
        setup, = self.scripts(calls, 'setup.py')
        self.assertEqual(setup['args'][1:], ['--update'])
        test, = self.dotnet_calls(calls, 'test')
        self.assertLess(calls.index(test), calls.index(setup))
        self.assertEqual(len(self.scripts(calls, 'package.py')), 1)
        package = self.root / 'artifacts/GuildChest-1.2.3.zip'
        checksum = package.with_suffix('.zip.sha256').read_text()
        self.assertEqual(checksum, hashlib.sha256(package.read_bytes()).hexdigest() + '  GuildChest-1.2.3.zip\n')
        self.assertEqual((self.root / 'github-output').read_text(), 'version=1.2.3\n')

    def test_invalid_release_tag_stops_before_tests_downloads_and_packaging(self):
        calls, result = self.run_build('--target', 'Release', '--release-tag', 'v1.2.4', success=False)
        self.assertIn('does not match manifest version', result.stdout + result.stderr)
        self.assertEqual(len(calls), 1)
        self.assertFalse((self.root / 'artifacts').exists())
        self.assertFalse((self.root / 'github-output').exists())

    def test_unmerged_release_commit_stops_before_tests_and_downloads(self):
        self.git('checkout', '-b', 'unmerged')
        self.git('commit', '--allow-empty', '-m', 'Unmerged change')
        calls, result = self.run_build('--target', 'Release', '--release-tag', 'v1.2.3', success=False)
        self.assertIn('must belong to origin/main', result.stdout + result.stderr)
        self.assertEqual(len(calls), 1)

    def test_failed_csharp_tests_stop_before_python_tests(self):
        self.env['FAKE_FAIL_TEST'] = '1'
        calls, _ = self.run_build('--target', 'Test', success=False)
        self.assertEqual(len(self.dotnet_calls(calls, 'test')), 1)
        self.assertFalse(any(call['kind'] == 'python' for call in calls))

    def test_setup_failure_stops_before_restore_and_build(self):
        self.env['FAKE_FAIL_SETUP'] = '1'
        calls, _ = self.run_build('--target', 'Compile', success=False)
        self.assertEqual(len(calls), 1)
        self.assertEqual(len(self.scripts(calls, 'setup.py')), 1)

    def test_setup_refresh_is_explicit(self):
        calls, _ = self.run_build('--target', 'Setup')
        self.assertEqual(len(calls), 1)
        self.assertEqual(len(calls[0]['args']), 1)
        calls, _ = self.run_build('--target', 'Setup', '--update-references')
        self.assertEqual(len(calls), 2)
        self.assertEqual(calls[-1]['args'][1:], ['--update'])

    def test_compatibility_refreshes_references_and_builds_native_fixture(self):
        calls, _ = self.run_build('--target', 'Compatibility')
        setup, = self.scripts(calls, 'setup.py')
        self.assertEqual(setup['args'][1:], ['--update'])
        self.assertEqual(len(self.dotnet_calls(calls, 'test')), 1)
        native = [call for call in self.dotnet_calls(calls, 'build')
                  if call['args'][1].endswith('GuildChest.NativeSmoke.csproj')]
        self.assertEqual(len(native), 1)
        self.assertEqual(native[0]['args'][native[0]['args'].index('--configuration') + 1], 'Release')
        self.assertLess(calls.index(self.scripts(calls, 'package.py')[0]), calls.index(native[0]))
        self.assertFalse(self.scripts(calls, 'check_release.py'))

    def test_existing_custom_references_compile_offline_with_the_real_setup_helper(self):
        managed = self.root.parent / 'installed game/Managed'
        managed.mkdir(parents=True)
        (managed / 'assembly_valheim.dll').write_bytes(b'installed game references')
        dependencies = self.root / '.local/dependencies'
        dependencies.mkdir(parents=True)
        for name in ('BepInEx.dll', '0Harmony.dll', 'Jotunn.dll'):
            (dependencies / name).write_bytes(name.encode())
        shutil.copyfile(PROJECT / 'scripts/setup.py', self.root / 'scripts/setup.py')
        curl = self.root / 'recording tools/curl'
        curl.write_text(f'#!{sys.executable}\nraise SystemExit("Unexpected network download")\n')
        curl.chmod(0o755)
        self.env['USE_REAL_SETUP'] = '1'
        calls, _ = self.run_build('--target', 'Compile', '--valheim-managed-dir', '../installed game/Managed')
        setup, = self.scripts(calls, 'setup.py')
        self.assertEqual(setup['args'][1:], ['--valheim-managed-dir', str(managed)])
        build, = self.dotnet_calls(calls, 'build')
        self.assertTrue(any('ValheimManagedDir=' + str(managed) in arg for arg in build['args']))
        metadata = json.loads((self.root / '.local/references.json').read_text())
        self.assertEqual(metadata['managed_dir'], str(managed))
        self.assertIsNone(metadata['steam_manifest'])
        self.assertFalse((self.root / '.local/steamcmd').exists())

    def test_fresh_reference_targets_reject_custom_directories_before_any_work(self):
        for target in ('Release', 'Compatibility'):
            with self.subTest(target=target):
                calls, _ = self.run_build('--target', target, '--release-tag', 'v1.2.3',
                                          '--valheim-managed-dir', '/existing/Managed', success=False)
                self.assertEqual(calls, [])

    def test_vscode_matcher_parses_an_actual_nuke_compiler_diagnostic(self):
        self.env['FAKE_DOTNET_DIAGNOSTIC'] = '1'
        _, result = self.run_build('--target', 'Compile', success=False)
        output = re.sub(r'\x1b\[[0-9;]*m', '', result.stdout + result.stderr)
        diagnostic = next(line for line in output.splitlines() if 'Broken.cs(3,5): error' in line)
        tasks = json.loads((PROJECT / '.vscode/tasks.json').read_text())
        matcher = tasks['problemMatcher']['pattern']
        match = re.match(matcher['regexp'], diagnostic)
        self.assertIsNotNone(match)
        self.assertEqual(match.group(matcher['file']), str(self.root / 'Broken.cs'))
        self.assertEqual(match.group(matcher['location']), '3,5')


if __name__ == '__main__':
    unittest.main()
