"""Prints pixel colors of PNG renders without third-party packages (issue #48).

Usage: python3 -I png_pixels.py IMAGE.png [X,Y ...]
Default points are inside the empty VST of a 504 x 721 palette render.
"""
import struct
import sys
import zlib


def load(path):
    data = open(path, "rb").read()
    pos, idat = 8, b""
    while pos < len(data):
        size, = struct.unpack(">I", data[pos:pos + 4])
        kind, chunk = data[pos + 4:pos + 8], data[pos + 8:pos + 8 + size]
        pos += 12 + size
        if kind == b"IHDR":
            width, height, _, color = struct.unpack(">IIBB", chunk[:10])
        elif kind == b"IDAT":
            idat += chunk
    bpp = 4 if color == 6 else 3
    raw, stride, rows, prev, i = zlib.decompress(idat), width * bpp, [], bytearray(width * bpp), 0
    for _ in range(height):
        kind, line = raw[i], bytearray(raw[i + 1:i + 1 + stride])
        i += 1 + stride
        for x in range(stride):
            a = line[x - bpp] if x >= bpp else 0
            b = prev[x]
            c = prev[x - bpp] if x >= bpp else 0
            if kind == 1:
                line[x] = (line[x] + a) & 255
            elif kind == 2:
                line[x] = (line[x] + b) & 255
            elif kind == 3:
                line[x] = (line[x] + (a + b) // 2) & 255
            elif kind == 4:
                p = a + b - c
                pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                line[x] = (line[x] + (a if pa <= pb and pa <= pc else b if pb <= pc else c)) & 255
        rows.append(line)
        prev = line
    return lambda x, y: "#%02X%02X%02X" % tuple(rows[y][x * bpp:x * bpp + 3])


if __name__ == "__main__":
    pixel = load(sys.argv[1])
    points = [tuple(map(int, arg.split(","))) for arg in sys.argv[2:]] or [(10, 680), (230, 680), (455, 400)]
    for x, y in points:
        print(f"{x},{y}: {pixel(x, y)}")
