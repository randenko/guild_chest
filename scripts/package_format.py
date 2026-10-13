"""Shared validation for Guild Chest packages; uses only the Python standard library."""
import re
import struct
from urllib.parse import urlsplit
import zlib

REPOSITORY = 'https://github.com/randenko/valheim-guildchest'
VERSION = r'(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)'
DEPENDENCY = re.compile(rf'([A-Za-z0-9_]+)-([A-Za-z0-9_]+)-({VERSION})')
DLLS = ('GuildChest.dll', 'GuildChest.Core.dll')
PACKAGE_FILES = frozenset({
    'manifest.json', 'README.md', 'CHANGELOG.md', 'icon.png', 'LICENSE',
    'docs/VALIDATION.md', 'docs/COMPATIBILITY.md', 'docs/ARCHITECTURE.md',
    *(f'BepInEx/plugins/GuildChest/{name}' for name in DLLS),
})
LINK = re.compile(r'(!?\[[^\[\]\n]*\]\()([^\s)]+)(\))')


def validate_manifest(manifest):
    if not isinstance(manifest, dict):
        raise ValueError('Manifest must be an object.')
    if manifest.get('name') != 'GuildChest':
        raise ValueError('Manifest name must be GuildChest.')
    if not isinstance(manifest.get('version_number'), str) or not re.fullmatch(VERSION, manifest['version_number']):
        raise ValueError('Manifest version must be stable X.Y.Z.')
    if manifest.get('website_url') != REPOSITORY:
        raise ValueError('Manifest website must be the GitHub repository.')
    description = manifest.get('description')
    if not isinstance(description, str) or not 1 <= len(description) <= 250:
        raise ValueError('Manifest description must contain 1 to 250 characters.')
    dependencies = manifest.get('dependencies')
    if not isinstance(dependencies, list) or not dependencies or any(
            not isinstance(item, str) or not DEPENDENCY.fullmatch(item) for item in dependencies):
        raise ValueError('Manifest requires versioned dependencies.')
    if len(set(dependencies)) != len(dependencies):
        raise ValueError('Manifest contains duplicate dependencies.')


def validate_icon(contents):
    # Validate PNG framing, checksums, dimensions, and compressed image data.
    if not contents.startswith(b'\x89PNG\r\n\x1a\n'):
        raise ValueError('Icon must be a valid 256 x 256 PNG.')
    offset, kinds, compressed = 8, [], bytearray()
    while offset < len(contents):
        if offset + 12 > len(contents):
            raise ValueError('Truncated icon PNG.')
        size = struct.unpack('>I', contents[offset:offset + 4])[0]
        kind = contents[offset + 4:offset + 8]
        end = offset + 8 + size
        if end + 4 > len(contents):
            raise ValueError('Truncated icon PNG chunk.')
        data = contents[offset + 8:end]
        if zlib.crc32(kind + data) != struct.unpack('>I', contents[end:end + 4])[0]:
            raise ValueError('Icon PNG checksum mismatch.')
        if not kinds:
            if kind != b'IHDR' or size != 13 or struct.unpack('>II', data[:8]) != (256, 256):
                raise ValueError('Icon must be a valid 256 x 256 PNG.')
        if kind == b'IDAT':
            compressed.extend(data)
        kinds.append(kind)
        offset = end + 4
        if kind == b'IEND':
            break
    if offset != len(contents) or not kinds or kinds[-1] != b'IEND' or not compressed:
        raise ValueError('Incomplete icon PNG.')
    try:
        decoder = zlib.decompressobj()
        pixels = decoder.decompress(compressed, 2_000_000)
        if not pixels or not decoder.eof or decoder.unused_data:
            raise ValueError('Invalid icon PNG image data.')
    except zlib.error as error:
        raise ValueError('Invalid icon PNG image data.') from error


def release_links(markdown, version, directory=''):
    """Resolve repository links, including images, against an immutable release tag."""
    tag = f'v{version}'

    def replace(match):
        prefix, target, suffix = match.groups()
        if target == f'{REPOSITORY}/releases/latest':
            return prefix + f'{REPOSITORY}/releases/tag/{tag}' + suffix
        if urlsplit(target).scheme or target.startswith(('#', '//')):
            return match.group(0)
        path = f'{directory}/{target}'.lstrip('/') if directory else target
        if prefix.startswith('!'):
            target = f'https://raw.githubusercontent.com/randenko/valheim-guildchest/{tag}/{path}'
        else:
            target = f'{REPOSITORY}/blob/{tag}/{path}'
        return prefix + target + suffix

    return LINK.sub(replace, markdown)


def validate_release_links(markdown, version):
    for _, target, _ in LINK.findall(markdown):
        for base in (f'{REPOSITORY}/blob/',
                     'https://raw.githubusercontent.com/randenko/valheim-guildchest/'):
            if target.startswith(base) and not target.startswith(f'{base}v{version}/'):
                raise ValueError('Packaged listing links must point to the package release tag.')
        if target == f'{REPOSITORY}/releases/latest':
            raise ValueError('Packaged download link must point to the package release tag.')


def validate_archive(archive):
    names = archive.namelist()
    if len(names) != len(set(names)) or set(names) != PACKAGE_FILES:
        raise ValueError('Archive structure does not match the Guild Chest package allowlist.')
    if any(entry.flag_bits & 1 or (entry.external_attr >> 16) & 0o170000 == 0o120000
           for entry in archive.infolist()):
        raise ValueError('Encrypted or symlink archive entries are not allowed.')
    if any(entry.file_size > 20_000_000 for entry in archive.infolist()):
        raise ValueError('Archive entry exceeds size limit.')
    if archive.testzip():
        raise ValueError('Archive CRC check failed.')
    validate_icon(archive.read('icon.png'))
    for name in PACKAGE_FILES - {'manifest.json', 'icon.png'}:
        if not archive.read(name):
            raise ValueError(f'Empty package input: {name}')
    for name in ('README.md', 'CHANGELOG.md'):
        markdown = archive.read(name).decode('utf-8')
        if any(not urlsplit(match[1]).scheme and not match[1].startswith('#')
               for match in LINK.findall(markdown)):
            raise ValueError(f'{name} contains relative links.')
