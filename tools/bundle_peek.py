"""Minimaler UnityFS-Leser: entpackt die LZ4-Bloecke und sucht Asset-Namen.

Nur zum Nachweis, dass ein Asset wirklich in der Bundle liegt - Bytesuche auf der
komprimierten Datei findet nichts (siehe docs/conventions/modding.md).
"""
import struct, sys, re, lzma


def lz4_block_decompress(src, uncompressed_size):
    dst = bytearray()
    i = 0
    n = len(src)
    while i < n:
        token = src[i]; i += 1
        lit = token >> 4
        if lit == 15:
            while True:
                b = src[i]; i += 1
                lit += b
                if b != 255:
                    break
        dst += src[i:i + lit]; i += lit
        if i >= n:
            break
        offset = src[i] | (src[i + 1] << 8); i += 2
        ml = token & 0x0F
        if ml == 15:
            while True:
                b = src[i]; i += 1
                ml += b
                if b != 255:
                    break
        ml += 4
        start = len(dst) - offset
        for k in range(ml):
            dst.append(dst[start + k])
    assert len(dst) == uncompressed_size, (len(dst), uncompressed_size)
    return bytes(dst)


def read(path):
    d = open(path, 'rb').read()
    p = d.index(b'\x00'); p += 1
    ver = struct.unpack('>I', d[p:p + 4])[0]; p += 4
    for _ in range(2):
        p = d.index(b'\x00', p) + 1
    size, cbi, ubi, flags = struct.unpack('>qIII', d[p:p + 20]); p += 20
    if flags & 0x200:
        p = (p + 15) & ~15
    comp = flags & 0x3F
    raw = d[p:p + cbi]; p += cbi
    info = lz4_block_decompress(raw, ubi) if comp in (2, 3) else raw
    if flags & 0x200:
        p = (p + 15) & ~15

    q = 16  # hash
    count = struct.unpack('>I', info[q:q + 4])[0]; q += 4
    blocks = []
    for _ in range(count):
        u, c, f = struct.unpack('>IIH', info[q:q + 10]); q += 10
        blocks.append((u, c, f))

    data = bytearray()
    for u, c, f in blocks:
        chunk = d[p:p + c]; p += c
        mode = f & 0x3F          # 0x40 = Streamed, gehoert nicht zur Kompressionsart
        if mode == 1:            # LZMA: 5 Byte props, danach roher Stream ohne Groesse
            # FORMAT_ALONE (5 Byte props + 8 Byte Groesse davorkleben) wird von
            # Python abgelehnt - der Stream hat keinen End-Marker. FORMAT_RAW mit
            # den aus dem props-Byte gerechneten Parametern funktioniert.
            props = chunk[0]
            pb, rest = divmod(props, 45)
            lp, lc = divmod(rest, 9)
            dict_size = struct.unpack('<I', chunk[1:5])[0]
            dec = lzma.LZMADecompressor(format=lzma.FORMAT_RAW, filters=[{
                'id': lzma.FILTER_LZMA1, 'dict_size': dict_size,
                'lc': lc, 'lp': lp, 'pb': pb}])
            data += dec.decompress(chunk[5:], max_length=u)
        elif mode in (2, 3):     # LZ4 / LZ4HC
            data += lz4_block_decompress(chunk, u)
        else:
            data += chunk
    return bytes(data)


if __name__ == '__main__':
    blob = read(sys.argv[1])
    print('entpackt: %d bytes' % len(blob))
    names = sorted(set(re.findall(rb'[A-Za-z_][A-Za-z0-9_ ]{3,40}', blob)))
    for needle in sys.argv[2:]:
        hits = [n.decode() for n in names if needle.lower() in n.decode().lower()]
        print('%-16s %s' % (needle, hits if hits else 'NICHT GEFUNDEN'))
