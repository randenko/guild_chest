#!/usr/bin/env python3
"""Fetch local-only build references. Steam account credentials are never used."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import tarfile
import zipfile

ROOT = Path(__file__).resolve().parents[1]
LOCAL = ROOT / '.local'
DEPENDENCIES = LOCAL / 'dependencies'
PACKAGES = {
    'BepInExPack': ('denikson', 'BepInExPack_Valheim', '5.4.2351', ('BepInEx.dll', '0Harmony.dll')),
    'Jotunn': ('ValheimModding', 'Jotunn', '2.30.2', ('Jotunn.dll',)),
}


def download(url, destination):
    subprocess.run(['curl', '--fail', '--location', '--retry', '3', '--output', str(destination), url], check=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--update', action='store_true', help='Explicitly update the game and refetch pinned mod dependencies.')
    args = parser.parse_args()
    LOCAL.mkdir(exist_ok=True)
    DEPENDENCIES.mkdir(exist_ok=True)
    server = LOCAL / 'valheim-server'
    managed = server / 'valheim_server_Data' / 'Managed'
    if args.update or not (managed / 'assembly_valheim.dll').is_file():
        steam = LOCAL / 'steamcmd'
        steam.mkdir(exist_ok=True)
        if not (steam / 'steamcmd.sh').is_file():
            archive = LOCAL / 'steamcmd_linux.tar.gz'
            download('https://steamcdn-a.akamaihd.net/client/installer/steamcmd_linux.tar.gz', archive)
            with tarfile.open(archive) as source:
                source.extractall(steam, filter='data')
        subprocess.run([str(steam / 'steamcmd.sh'), '+force_install_dir', str(server), '+login', 'anonymous',
                        '+app_update', '896660', 'validate', '+quit'], check=True)
        if not (managed / 'assembly_valheim.dll').is_file():
            raise SystemExit('SteamCMD did not install the expected Managed folder. Check its output and retry setup.')
    for label, (team, name, version, wanted) in PACKAGES.items():
        if not args.update and all((DEPENDENCIES / item).is_file() for item in wanted):
            continue
        archive = LOCAL / f'{label}-{version}.zip'
        download(f'https://thunderstore.io/package/download/{team}/{name}/{version}/', archive)
        with zipfile.ZipFile(archive) as source:
            for filename in wanted:
                matches = [item for item in source.namelist() if Path(item.replace('\\', '/')).name == filename]
                if len(matches) != 1:
                    raise SystemExit(f'{label}: expected exactly one {filename}, got {matches}')
                (DEPENDENCIES / filename).write_bytes(source.read(matches[0]))
    manifest = server / 'steamapps' / 'appmanifest_896660.acf'
    refs = [managed / 'assembly_valheim.dll', managed / 'assembly_utils.dll', *DEPENDENCIES.glob('*.dll')]
    metadata = {
        'steam_app_id': 896660,
        'steam_manifest': manifest.read_text() if manifest.exists() else None,
        'dependencies': {name: data[2] for name, data in PACKAGES.items()},
        'sha256': {str(path.relative_to(ROOT)): hashlib.sha256(path.read_bytes()).hexdigest() for path in refs if path.exists()},
    }
    (LOCAL / 'references.json').write_text(json.dumps(metadata, indent=2) + '\n')
    print('References ready. Run bash scripts/dev.sh build.')


if __name__ == '__main__':
    main()
