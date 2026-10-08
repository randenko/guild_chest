#!/usr/bin/env python3
"""Create a deterministic mod ZIP containing only our binaries and package metadata."""
import json
import hashlib
from pathlib import Path
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[1]
manifest = json.loads((ROOT / 'manifest.json').read_text())
binary_dir = ROOT / 'src/GuildChest/bin/Release/net48'
files = {
    'manifest.json': ROOT / 'manifest.json',
    'README.md': ROOT / 'README.md',
    'icon.png': ROOT / 'icon.png',
    'LICENSE': ROOT / 'LICENSE',
    'docs/VALIDATION.md': ROOT / 'docs/VALIDATION.md',
    'docs/COMPATIBILITY.md': ROOT / 'docs/COMPATIBILITY.md',
    'docs/ARCHITECTURE.md': ROOT / 'docs/ARCHITECTURE.md',
    'BepInEx/plugins/GuildChest/GuildChest.dll': binary_dir / 'GuildChest.dll',
    'BepInEx/plugins/GuildChest/GuildChest.Core.dll': binary_dir / 'GuildChest.Core.dll',
}
for path in files.values():
    if not path.is_file():
        raise SystemExit(f'Missing package input: {path}')
receipt_path = binary_dir / 'GuildChest.build.xml'
if not receipt_path.is_file():
    raise SystemExit('Missing build receipt. Run bash scripts/dev.sh package to rebuild.')
receipt = ET.parse(receipt_path).getroot()
if receipt.get('version') != manifest['version_number']:
    raise SystemExit('Release binaries have a different version. Run bash scripts/dev.sh package to rebuild.')
hashes = {entry.get('name'): entry.get('hash') for entry in receipt.findall('file')}
for name in ('GuildChest.dll', 'GuildChest.Core.dll'):
    actual = hashlib.sha256((binary_dir / name).read_bytes()).hexdigest()
    if actual.upper() != (hashes.get(name) or '').upper():
        raise SystemExit(f'Release binary changed since its build: {name}. Rebuild before packaging.')
destination = ROOT / 'artifacts' / f'GuildChest-{manifest["version_number"]}.zip'
destination.parent.mkdir(exist_ok=True)
with zipfile.ZipFile(destination, 'w', compression=zipfile.ZIP_DEFLATED) as archive:
    for name, path in sorted(files.items()):
        info = zipfile.ZipInfo(name, (2026, 1, 1, 0, 0, 0))
        info.compress_type = zipfile.ZIP_DEFLATED
        info.external_attr = 0o100644 << 16
        archive.writestr(info, path.read_bytes())
print(destination)
