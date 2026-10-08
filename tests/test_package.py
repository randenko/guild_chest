"""Check release packaging rejects stale or replaced binaries before writing a ZIP."""
import hashlib
import json
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
        receipt = ET.Element('build', version='1.2.3')
        for name in ('GuildChest.dll', 'GuildChest.Core.dll'):
            contents = name.encode()
            (self.binaries / name).write_bytes(contents)
            ET.SubElement(receipt, 'file', name=name, hash=hashlib.sha256(contents).hexdigest())
        self.receipt = self.binaries / 'GuildChest.build.xml'
        ET.ElementTree(receipt).write(self.receipt)

    def package(self):
        return subprocess.run([sys.executable, str(self.root / 'scripts/package.py')], capture_output=True, text=True)

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
