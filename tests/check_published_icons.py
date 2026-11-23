"""Verify the shell icon group of each published Windows app without running it."""
from pathlib import Path
from struct import iter_unpack, unpack_from
import sys


def ico_frames(ico):
    count, = unpack_from('<H', ico, 4)
    return {(w or 256, h or 256): ico[pos:pos+n]
            for w, h, _, _, _, _, n, pos in iter_unpack('<BBBBHHII', ico[6:6+16*count])}


def shell_frames(exe):
    pe, = unpack_from('<I', exe, 60)
    if exe[pe:pe+4] != b'PE\x00\x00':
        raise ValueError('Not a PE executable')
    sections, = unpack_from('<H', exe, pe+6)
    optional_size, = unpack_from('<H', exe, pe+20)
    optional = pe+24
    magic, = unpack_from('<H', exe, optional)
    resource_rva, = unpack_from('<I', exe, optional + {0x10b: 96, 0x20b: 112}[magic] + 16)

    def file_offset(rva):
        for i in range(sections):
            _, start, size, offset = unpack_from('<IIII', exe, optional+optional_size+40*i+8)
            if start <= rva < start+size:
                return offset+rva-start
        raise ValueError(f'Unmapped resource address {rva}')

    root = file_offset(resource_rva)

    def children(relative):
        names, ids = unpack_from('<HH', exe, root+relative+12)
        return {key: ptr & 0x7fffffff for key, ptr in
                iter_unpack('<II', exe[root+relative+16:root+relative+16+8*(names+ids)])}

    def data(relative):
        leaf = next(iter(children(relative).values()))  # first language
        rva, size = unpack_from('<II', exe, root+leaf)
        pos = file_offset(rva)
        return exe[pos:pos+size]

    types = children(0)
    icons = children(types[3])  # RT_ICON
    group = data(next(iter(children(types[14]).values())))  # first shell icon group
    return {(w or 256, h or 256): data(icons[ident])
            for w, h, _, _, _, _, _, ident in iter_unpack('<BBBBHHIH', group[6:])}


if __name__ == '__main__':
    out = Path(sys.argv[1])
    root = Path(__file__).resolve().parents[1]
    for name, icon in [('PasskeyDesk', 'src/YubiEnroll.UI/Assets/App.ico'),
                       ('PasskeyDesk.ConsoleHost', 'src/YubiEnroll.ConsoleHost/ConsoleHost.ico')]:
        expected = ico_frames((root/icon).read_bytes())
        if not expected or shell_frames((out/(name+'.exe')).read_bytes()) != expected:
            sys.exit(f'{name}.exe: shell icon does not match {icon}')
        print(f'{name}.exe: all {len(expected)} embedded icon sizes match {icon}')
