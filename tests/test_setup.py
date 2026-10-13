"""Check supplied references bypass SteamCMD and retain accurate provenance."""
from contextlib import redirect_stdout
import hashlib
import importlib.util
import io
import json
from pathlib import Path
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
        managed = self.setup_module.LOCAL / 'valheim-server/valheim_server_Data/Managed'
        managed.mkdir(parents=True)
        (managed / 'assembly_valheim.dll').write_bytes(b'game references')
        steam_script = self.setup_module.LOCAL / 'steamcmd/steamcmd.sh'
        steam_script.parent.mkdir()
        steam_script.write_text('fixture SteamCMD')
        with patch.object(self.setup_module, 'download', side_effect=self.dependency_download) as download, \
                patch.object(self.setup_module.subprocess, 'run') as steam:
            self.run_setup('--update')
        steam.assert_called_once()
        self.assertIn('+app_update', steam.call_args.args[0])
        self.assertIn('896660', steam.call_args.args[0])
        arguments = steam.call_args.args[0]
        self.assertEqual(arguments[arguments.index('-beta') + 1], 'public')
        self.assertEqual(download.call_count, 2)


if __name__ == '__main__':
    unittest.main()
