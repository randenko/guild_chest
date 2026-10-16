"""Check reference provenance, offline reuse, and SteamCMD update recovery."""
from contextlib import redirect_stdout
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
import zipfile

PROJECT = Path(__file__).resolve().parents[1]


class SetupTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name) / 'repository'
        self.root.mkdir()
        self.managed = Path(self.directory.name) / 'installed game/Managed'
        self.managed.mkdir(parents=True)
        for name in ('assembly_valheim.dll', 'assembly_utils.dll'):
            (self.managed / name).write_bytes(name.encode())
        spec = importlib.util.spec_from_file_location('setup_under_test', PROJECT / 'scripts/setup.py')
        self.setup_module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.setup_module)
        self.setup_module.ROOT = self.root
        self.setup_module.LOCAL = self.root / '.local'
        self.setup_module.DEPENDENCIES = self.root / '.local/dependencies'
        self.setup_module.DEPENDENCIES.mkdir(parents=True)
        self.manifest = self.root / '.local/valheim-server/steamapps/appmanifest_896660.acf'
        self.manifest.parent.mkdir(parents=True)
        self.manifest.write_text('previously downloaded server manifest')

    def cache_dependencies(self):
        for name in ('BepInEx.dll', '0Harmony.dll', 'Jotunn.dll'):
            (self.setup_module.DEPENDENCIES / name).write_bytes(name.encode())

    def cache_server(self):
        managed = self.setup_module.LOCAL / 'valheim-server/valheim_server_Data/Managed'
        managed.mkdir(parents=True)
        (managed / 'assembly_valheim.dll').write_bytes(b'game references')
        steam_script = self.setup_module.LOCAL / 'steamcmd/steamcmd.sh'
        steam_script.parent.mkdir()
        steam_script.write_text('fixture SteamCMD')
        return managed

    def run_setup(self, *arguments):
        with patch.object(sys, 'argv', ['setup.py', *arguments]), redirect_stdout(io.StringIO()):
            self.setup_module.main()
        return json.loads((self.setup_module.LOCAL / 'references.json').read_text())

    def dependency_download(self, url, destination):
        self.assertTrue(url.startswith('https://thunderstore.io/package/download/'), url)
        label = 'BepInExPack' if '/denikson/' in url else 'Jotunn'
        with zipfile.ZipFile(destination, 'w') as archive:
            for name in self.setup_module.PACKAGES[label][3]:
                archive.writestr(f'plugins/{name}', name.encode())

    def test_cached_custom_references_work_offline_and_record_external_hashes(self):
        self.cache_dependencies()
        with patch.object(self.setup_module, 'download', side_effect=AssertionError('Unexpected download')) as download, \
                patch.object(self.setup_module.subprocess, 'run', side_effect=AssertionError('Unexpected SteamCMD')) as steam:
            metadata = self.run_setup('--valheim-managed-dir', str(self.managed))
        download.assert_not_called()
        steam.assert_not_called()
        self.assertEqual(metadata['managed_dir'], str(self.managed))
        self.assertIsNone(metadata['steam_app_id'])
        self.assertIsNone(metadata['steam_manifest'])
        assembly = self.managed / 'assembly_valheim.dll'
        self.assertEqual(metadata['sha256'][str(assembly)], hashlib.sha256(assembly.read_bytes()).hexdigest())
        self.assertIn('.local/dependencies/Jotunn.dll', metadata['sha256'])
        self.assertFalse((self.setup_module.LOCAL / 'steamcmd').exists())

    def test_custom_references_download_only_missing_mod_dependencies(self):
        (self.setup_module.DEPENDENCIES / 'Jotunn.dll').write_bytes(b'cached Jotunn')
        with patch.object(self.setup_module, 'download', side_effect=self.dependency_download) as download, \
                patch.object(self.setup_module.subprocess, 'run', side_effect=AssertionError('Unexpected SteamCMD')):
            metadata = self.run_setup('--valheim-managed-dir', str(self.managed))
        self.assertEqual(download.call_count, 1)
        self.assertIn('/denikson/BepInExPack_Valheim/5.4.2351/', download.call_args.args[0])
        self.assertEqual((self.setup_module.DEPENDENCIES / 'Jotunn.dll').read_bytes(), b'cached Jotunn')
        self.assertEqual(metadata['dependencies']['Jotunn'], '2.30.2')

    def test_custom_update_refreshes_mods_without_changing_game_files(self):
        self.cache_dependencies()
        assembly = self.managed / 'assembly_valheim.dll'
        original = assembly.read_bytes()
        with patch.object(self.setup_module, 'download', side_effect=self.dependency_download) as download, \
                patch.object(self.setup_module.subprocess, 'run', side_effect=AssertionError('Unexpected SteamCMD')):
            self.run_setup('--valheim-managed-dir', str(self.managed), '--update')
        self.assertEqual(download.call_count, 2)
        self.assertEqual(assembly.read_bytes(), original)

    def test_invalid_custom_directory_fails_without_falling_back_to_downloads(self):
        with patch.object(self.setup_module, 'download') as download, \
                patch.object(self.setup_module.subprocess, 'run') as steam:
            with self.assertRaisesRegex(SystemExit, 'missing assembly_valheim.dll'):
                self.run_setup('--valheim-managed-dir', str(self.managed / 'missing'))
        download.assert_not_called()
        steam.assert_not_called()
        self.assertFalse((self.setup_module.LOCAL / 'references.json').exists())

    def test_cached_default_references_preserve_steam_provenance_and_relative_hashes(self):
        self.cache_dependencies()
        managed = self.setup_module.LOCAL / 'valheim-server/valheim_server_Data/Managed'
        managed.mkdir(parents=True)
        (managed / 'assembly_valheim.dll').write_bytes(b'default game references')
        with patch.object(self.setup_module, 'download', side_effect=AssertionError('Unexpected download')), \
                patch.object(self.setup_module.subprocess, 'run', side_effect=AssertionError('Unexpected SteamCMD')):
            metadata = self.run_setup()
        self.assertEqual(metadata['steam_app_id'], 896660)
        self.assertEqual(metadata['steam_manifest'], self.manifest.read_text())
        self.assertIn('.local/valheim-server/valheim_server_Data/Managed/assembly_valheim.dll', metadata['sha256'])

    def test_default_update_still_refreshes_game_and_mod_dependencies(self):
        self.cache_dependencies()
        self.cache_server()
        with patch.object(self.setup_module, 'download', side_effect=self.dependency_download) as download, \
                patch.object(self.setup_module.subprocess, 'run') as steam:
            self.run_setup('--update')
        self.assertEqual(steam.call_count, 2)
        refresh, install = [call.args[0] for call in steam.call_args_list]
        self.assertIn('+app_info_update', refresh)
        self.assertIn('+app_info_print', refresh)
        self.assertIn('896660', refresh)
        self.assertNotIn('+app_update', refresh)
        self.assertIn('+app_update', install)
        self.assertIn('896660', install)
        arguments = install
        self.assertEqual(arguments[arguments.index('-beta') + 1], 'public')
        self.assertIn('+@ShutdownOnFailedCommand', arguments)
        self.assertIn('+@NoPromptForPassword', arguments)
        self.assertEqual(download.call_count, 2)
        self.assertFalse(self.manifest.with_suffix('.acf.before-recovery').exists())

    def test_failed_cached_updates_recover_across_successive_game_releases(self):
        managed = self.cache_server()
        assembly = managed / 'assembly_valheim.dll'
        unrelated = self.manifest.parent / 'appmanifest_123.acf'
        unrelated.write_text('another app')
        backup = self.manifest.with_suffix('.acf.before-recovery')
        for release in ('new release', 'another release'):
            with self.subTest(release=release):
                previous_manifest = self.manifest.read_bytes()
                previous_assembly = assembly.read_bytes()
                installs = []

                def steam_command(command, **kwargs):
                    if '+app_update' not in command:
                        return
                    installs.append(command)
                    if len(installs) == 1:
                        self.manifest.write_text('failed update metadata')
                        raise subprocess.CalledProcessError(8, command)
                    self.assertFalse(self.manifest.exists())
                    self.assertEqual(backup.read_bytes(), previous_manifest)
                    self.assertEqual(assembly.read_bytes(), previous_assembly)
                    self.assertEqual(unrelated.read_text(), 'another app')
                    self.manifest.write_text(release)
                    assembly.write_bytes(release.encode())

                with patch.object(self.setup_module, 'download', side_effect=self.dependency_download), \
                        patch.object(self.setup_module.subprocess, 'run', side_effect=steam_command) as steam:
                    metadata = self.run_setup('--update')
                self.assertEqual(steam.call_count, 3)
                self.assertEqual(installs[0], installs[1])
                self.assertEqual(metadata['steam_manifest'], release)
                key = '.local/valheim-server/valheim_server_Data/Managed/assembly_valheim.dll'
                self.assertEqual(metadata['sha256'][key], hashlib.sha256(release.encode()).hexdigest())

    def test_failed_recovery_restores_metadata_and_does_not_publish_new_provenance(self):
        self.cache_server()
        previous_manifest = self.manifest.read_bytes()
        provenance = self.setup_module.LOCAL / 'references.json'
        provenance.write_text('previous successful provenance')
        installs = []

        def steam_command(command, **kwargs):
            if '+app_update' in command:
                installs.append(command)
                self.manifest.write_text('partial update metadata')
                raise subprocess.CalledProcessError(8, command)

        with patch.object(self.setup_module, 'download') as download, \
                patch.object(self.setup_module.subprocess, 'run', side_effect=steam_command):
            with self.assertRaises(subprocess.CalledProcessError):
                self.run_setup('--update')
        self.assertEqual(len(installs), 2)
        self.assertEqual(self.manifest.read_bytes(), previous_manifest)
        self.assertEqual(self.manifest.with_suffix('.acf.before-recovery').read_bytes(), previous_manifest)
        self.assertEqual(provenance.read_text(), 'previous successful provenance')
        download.assert_not_called()

    def test_failed_cold_install_does_not_retry_without_cached_metadata(self):
        self.cache_server()
        self.manifest.unlink()

        def steam_command(command, **kwargs):
            if '+app_update' in command:
                self.manifest.write_text('partial first install')
                raise subprocess.CalledProcessError(8, command)

        with patch.object(self.setup_module, 'download') as download, \
                patch.object(self.setup_module.subprocess, 'run', side_effect=steam_command) as steam:
            with self.assertRaises(subprocess.CalledProcessError):
                self.run_setup('--update')
        self.assertEqual(steam.call_count, 2)
        self.assertEqual(self.manifest.read_text(), 'partial first install')
        self.assertFalse(self.manifest.with_suffix('.acf.before-recovery').exists())
        self.assertFalse((self.setup_module.LOCAL / 'references.json').exists())
        download.assert_not_called()

    def test_failed_app_metadata_refresh_does_not_reset_installation(self):
        self.cache_server()
        previous_manifest = self.manifest.read_bytes()
        with patch.object(self.setup_module, 'download') as download, \
                patch.object(self.setup_module.subprocess, 'run',
                             side_effect=subprocess.CalledProcessError(1, 'steamcmd')) as steam:
            with self.assertRaises(subprocess.CalledProcessError):
                self.run_setup('--update')
        self.assertEqual(steam.call_count, 1)
        self.assertEqual(self.manifest.read_bytes(), previous_manifest)
        self.assertFalse(self.manifest.with_suffix('.acf.before-recovery').exists())
        download.assert_not_called()

    def test_recovery_without_new_manifest_fails_and_restores_cached_metadata(self):
        self.cache_server()
        previous_manifest = self.manifest.read_bytes()
        installs = []

        def steam_command(command, **kwargs):
            if '+app_update' in command:
                installs.append(command)
                if len(installs) == 1:
                    raise subprocess.CalledProcessError(8, command)

        with patch.object(self.setup_module, 'download') as download, \
                patch.object(self.setup_module.subprocess, 'run', side_effect=steam_command):
            with self.assertRaisesRegex(SystemExit, 'manifest'):
                self.run_setup('--update')
        self.assertEqual(len(installs), 2)
        self.assertEqual(self.manifest.read_bytes(), previous_manifest)
        self.assertFalse((self.setup_module.LOCAL / 'references.json').exists())
        download.assert_not_called()


if __name__ == '__main__':
    unittest.main()
