#!/usr/bin/env python3
"""Create a deterministic mod ZIP containing only our binaries and package metadata."""
import json
from pathlib import Path
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
    'BepInEx/plugins/GuildChest/GuildChest.dll': binary_dir / 'GuildChest.dll',
    'BepInEx/plugins/GuildChest/GuildChest.Core.dll': binary_dir / 'GuildChest.Core.dll',
}
for path in files.values():
    if not path.is_file():
        raise SystemExit(f'Missing package input: {path}')
destination = ROOT / 'artifacts' / f'GuildChest-{manifest["version_number"]}.zip'
destination.parent.mkdir(exist_ok=True)
with zipfile.ZipFile(destination, 'w', compression=zipfile.ZIP_DEFLATED) as archive:
    for name, path in sorted(files.items()):
        info = zipfile.ZipInfo(name, (2026, 1, 1, 0, 0, 0))
        info.compress_type = zipfile.ZIP_DEFLATED
        info.external_attr = 0o100644 << 16
        archive.writestr(info, path.read_bytes())
print(destination)
