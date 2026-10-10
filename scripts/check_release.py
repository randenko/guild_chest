#!/usr/bin/env python3
"""Reject release tags that differ from the manifest or point outside main."""
import argparse
import json
from pathlib import Path
import re
import subprocess

ROOT = Path(__file__).resolve().parents[1]
RELEASE_TAG = re.compile(r'v(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)')


def check_release(tag, version, repository=ROOT):
    if not RELEASE_TAG.fullmatch(tag):
        raise ValueError('Release tag must have the form vX.Y.Z (for example, v1.0.9).')
    if tag != f'v{version}':
        raise ValueError(f'Release tag {tag} does not match manifest version {version}.')
    result = subprocess.run(['git', 'merge-base', '--is-ancestor', 'HEAD', 'refs/remotes/origin/main'],
                            cwd=repository, capture_output=True, text=True)
    if result.returncode == 1:
        raise ValueError('Release commit must belong to origin/main. Merge it before tagging.')
    if result.returncode != 0:
        raise ValueError(f'Cannot verify origin/main: {result.stderr.strip()}')
    return version


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('tag')
    args = parser.parse_args()
    version = json.loads((ROOT / 'manifest.json').read_text())['version_number']
    try:
        print(check_release(args.tag, version))
    except ValueError as error:
        raise SystemExit(str(error)) from error


if __name__ == '__main__':
    main()
