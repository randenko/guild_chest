"""Check release packaging, reproducibility, and timestamps used during upgrades."""
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET
import zipfile


class PackageTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        script = self.root / 'scripts/package.py'
        script.parent.mkdir()
        shutil.copyfile(Path(__file__).resolve().parents[1] / 'scripts/package.py', script)
        (self.root / 'manifest.json').write_text(json.dumps({'version_number': '1.2.3'}))
        for name in ('README.md', 'icon.png', 'LICENSE', 'docs/VALIDATION.md', 'docs/COMPATIBILITY.md', 'docs/ARCHITECTURE.md'):
            path = self.root / name
            path.parent.mkdir(exist_ok=True)
            path.write_bytes(b'fixture package input')
        self.binaries = self.root / 'src/GuildChest/bin/Release/net48'
        self.binaries.mkdir(parents=True)
        for name in ('GuildChest.dll', 'GuildChest.Core.dll'):
            (self.binaries / name).write_bytes(name.encode())
        self.receipt = self.binaries / 'GuildChest.build.xml'
        self.write_receipt()

    def write_receipt(self, version='1.2.3'):
        receipt = ET.Element('build', version=version)
        for name in ('GuildChest.dll', 'GuildChest.Core.dll'):
            contents = (self.binaries / name).read_bytes()
            ET.SubElement(receipt, 'file', name=name, hash=hashlib.sha256(contents).hexdigest())
        ET.ElementTree(receipt).write(self.receipt)

    def package(self, env=None):
        return subprocess.run([sys.executable, str(self.root / 'scripts/package.py')],
                              capture_output=True, text=True, env=env)

    def packaged_dlls(self, version='1.2.3'):
        result = self.package()
        self.assertEqual(result.returncode, 0, result.stderr)
        with zipfile.ZipFile(self.root / f'artifacts/GuildChest-{version}.zip') as archive:
            return {name: archive.getinfo(f'BepInEx/plugins/GuildChest/{name}')
                    for name in ('GuildChest.dll', 'GuildChest.Core.dll')}

    def assert_rejected(self, message):
        result = self.package()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn(message, result.stderr)
        self.assertFalse((self.root / 'artifacts').exists())

    def test_matching_build_packages_only_release_inputs(self):
        result = self.package()
        self.assertEqual(result.returncode, 0, result.stderr)
        with zipfile.ZipFile(self.root / 'artifacts/GuildChest-1.2.3.zip') as archive:
            self.assertIsNone(archive.testzip())
            self.assertEqual(archive.read('BepInEx/plugins/GuildChest/GuildChest.Core.dll'), b'GuildChest.Core.dll')
            self.assertFalse(any(name.endswith('.xml') for name in archive.namelist()))
            self.assertEqual(len(archive.namelist()), 9)

    def test_changed_manifest_rejects_old_build(self):
        (self.root / 'manifest.json').write_text(json.dumps({'version_number': '9.9.9'}))
        self.assert_rejected('different version')

    def test_same_size_dll_updates_change_timestamps_across_releases_and_rebuilds(self):
        previous = self.packaged_dlls()
        for update, version in (('release', '1.2.4'), ('rebuild', '1.2.4')):
            with self.subTest(update=update):
                (self.root / 'manifest.json').write_text(json.dumps({'version_number': version}))
                for name in previous:
                    path = self.binaries / name
                    path.write_bytes(path.read_bytes().swapcase())
                self.write_receipt(version)
                original, previous = previous, self.packaged_dlls(version)
                for name in original:
                    self.assertEqual(original[name].file_size, previous[name].file_size)
                    self.assertNotEqual(original[name].CRC, previous[name].CRC)
                    self.assertNotEqual(original[name].date_time, previous[name].date_time,
                                        'Changed DLLs must invalidate rsync and BepInEx timestamp checks.')

    def test_dll_timestamps_differ_from_legacy_release_timestamp(self):
        for name, entry in self.packaged_dlls().items():
            with self.subTest(binary=name):
                self.assertNotEqual(entry.date_time, (2026, 1, 1, 0, 0, 0))

    def test_identical_inputs_produce_identical_zip_despite_mtimes_and_timezone(self):
        result = self.package(env={**os.environ, 'TZ': 'UTC'})
        self.assertEqual(result.returncode, 0, result.stderr)
        archive = self.root / 'artifacts/GuildChest-1.2.3.zip'
        original = archive.read_bytes()
        for path in self.root.rglob('*'):
            if path.is_file():
                os.utime(path, (946684800, 946684800))
        result = self.package(env={**os.environ, 'TZ': 'America/New_York'})
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(archive.read_bytes(), original)

    def test_replaced_plugin_or_core_requires_rebuild(self):
        for name in ('GuildChest.dll', 'GuildChest.Core.dll'):
            with self.subTest(binary=name):
                path = self.binaries / name
                original = path.read_bytes()
                path.write_bytes(b'replaced DLL')
                self.assert_rejected(f'Release binary changed since its build: {name}')
                path.write_bytes(original)

    def test_missing_receipt_requires_rebuild(self):
        self.receipt.unlink()
        self.assert_rejected('Missing build receipt')


if __name__ == '__main__':
    unittest.main()
