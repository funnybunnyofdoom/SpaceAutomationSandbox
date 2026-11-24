"""Read and write Pixelorama .pxo projects and plain PNGs with the standard library only (#46).

The .pxo layout (Pixelorama 1.2.3, pxo_version 7) is a ZIP holding data.json and one raw RGBA8 image per
cel at image_data/frames/<frame>/layer_<layer> (1-based), width*height*4 bytes, row-major from top-left.
Used by make_part_art.py (which writes the sources) and to check exports; Pixelorama itself does the export.
"""
import json
import struct
import zipfile
import zlib

PXO_VERSION = 7
RGBA8 = 5  # Godot Image.FORMAT_RGBA8, Pixelorama's color_mode for RGBA projects


def write_pxo(path, width, height, layers):
    """layers: list of (name, rows) where rows[y][x] = (r, g, b, a). One frame."""
    data = {
        "pixelorama_version": "v1.2.3-stable",
        "pxo_version": PXO_VERSION,
        "size_x": width,
        "size_y": height,
        "color_mode": RGBA8,
        "layers": [
            {"name": name, "visible": True, "locked": False, "blend_mode": 0, "clipping_mask": False,
             "opacity": 1.0, "parent": -1, "effects": [], "new_cels_linked": False}
            for name, _ in layers
        ],
        "frames": [{"cels": [{"opacity": 1.0, "z_index": 0} for _ in layers], "duration": 1.0}],
        "current_frame": 0,
        "current_layer": 0,
        "fps": 6.0,
        "tags": [],
        "guides": [],
        "brushes": [],
    }
    def entry(name):  # a fixed timestamp, so the same art always gives the same file (no churn in git)
        return zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))

    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr(entry("mimetype"), "application/x-pixelorama", zipfile.ZIP_DEFLATED)
        z.writestr(entry("data.json"), json.dumps(data, indent=1), zipfile.ZIP_DEFLATED)
        for i, (_, rows) in enumerate(layers, start=1):
            z.writestr(entry(f"image_data/frames/1/layer_{i}"), rgba_bytes(rows), zipfile.ZIP_DEFLATED)


def read_pxo(path):
    """Returns (width, height, [(name, rows)]) for frame 1."""
    with zipfile.ZipFile(path) as z:
        data = json.loads(z.read("data.json"))
        w, h = data["size_x"], data["size_y"]
        out = []
        for i, layer in enumerate(data["layers"], start=1):
            raw = z.read(f"image_data/frames/1/layer_{i}")
            out.append((layer.get("name", ""), rows_from_bytes(raw, w, h)))
        return w, h, out


def rgba_bytes(rows):
    return b"".join(bytes(c for px in row for c in px) for row in rows)


def rows_from_bytes(raw, w, h):
    return [[tuple(raw[(y * w + x) * 4:(y * w + x) * 4 + 4]) for x in range(w)] for y in range(h)]


def write_png(path, rows):
    h, w = len(rows), len(rows[0])
    raw = b"".join(b"\x00" + bytes(c for px in row for c in px) for row in rows)

    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)

    with open(path, "wb") as f:
        f.write(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0))
                + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


def read_png(path):
    """8-bit RGBA or RGB PNGs (what Pixelorama exports): returns rows of (r, g, b, a)."""
    with open(path, "rb") as f:
        data = f.read()
    assert data[:8] == b"\x89PNG\r\n\x1a\n", "not a PNG"
    pos, idat, w = 8, b"", 0
    while pos < len(data):
        length = struct.unpack(">I", data[pos:pos + 4])[0]
        kind = data[pos + 4:pos + 8]
        body = data[pos + 8:pos + 8 + length]
        if kind == b"IHDR":
            w, h, depth, ctype = struct.unpack(">IIBB", body[:10])
            assert depth == 8 and ctype in (2, 6), f"unsupported PNG depth {depth} type {ctype}"
            bpp = 4 if ctype == 6 else 3
        elif kind == b"IDAT":
            idat += body
        pos += 12 + length
    raw = zlib.decompress(idat)
    stride = w * bpp
    rows, prev, i = [], bytearray(stride), 0
    for _ in range(h):
        f, line = raw[i], bytearray(raw[i + 1:i + 1 + stride])
        i += 1 + stride
        for x in range(stride):
            a = line[x - bpp] if x >= bpp else 0
            b = prev[x]
            c = prev[x - bpp] if x >= bpp else 0
            if f == 1: line[x] = (line[x] + a) & 255
            elif f == 2: line[x] = (line[x] + b) & 255
            elif f == 3: line[x] = (line[x] + (a + b) // 2) & 255
            elif f == 4:
                p = a + b - c
                pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                line[x] = (line[x] + (a if pa <= pb and pa <= pc else b if pb <= pc else c)) & 255
        rows.append([tuple(line[x * bpp:x * bpp + bpp]) + ((255,) if bpp == 3 else ()) for x in range(w)])
        prev = line
    return rows
