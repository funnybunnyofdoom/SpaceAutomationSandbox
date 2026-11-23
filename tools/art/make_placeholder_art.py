"""Placeholder app icon, Windows .ico and boot splash (#67), drawn from code so the source is this file.

Run from the repository root:  python tools/art/make_placeholder_art.py
Writes assets/art/ui/app_icon.png (256 px), assets/art/ui/app_icon.ico (16/32/48/64/128/256) and
assets/art/ui/boot_splash.png (1280x720). Replace them with Pixelorama art later (#46); keep the names.
Only the standard library is used (no Pillow).
"""
import os
import random
import struct
import zlib

OUT = os.path.join("assets", "art", "ui")

# 16x16 ship, pointing up, in the game's square-module style. "." is transparent.
SHIP = [
    "................",
    ".......cc.......",
    "......cCCc......",
    "......hCCh......",
    ".....hhhhhh.....",
    ".....hHHHHh.....",
    "...s.hHggHh.s...",
    "..ss.hHggHh.ss..",
    "..sssshhhhssss..",
    "..ss.hHHHHh.ss..",
    "...s.hhhhhh.s...",
    ".....tt..tt.....",
    ".....ff..ff.....",
    ".....FF..FF.....",
    "......F...F.....",
    "................",
]
PALETTE = {
    "c": (70, 150, 230), "C": (140, 210, 255),   # cockpit
    "h": (120, 128, 140), "H": (165, 172, 185),  # hull
    "g": (90, 200, 120),                         # cargo/greenhouse
    "s": (60, 90, 150),                          # solar wings
    "t": (80, 80, 90),                           # thrusters
    "f": (255, 190, 70), "F": (255, 120, 40),    # flame
}
SPACE = (5, 5, 15)


def ship_pixels(size):
    """The 16x16 ship scaled by a whole number to size x size (RGBA rows), on a dark rounded tile."""
    scale = size // 16
    rows = []
    for y in range(size):
        row = []
        for x in range(size):
            ch = SHIP[y // scale][x // scale]
            if ch in PALETTE:
                row.append(PALETTE[ch] + (255,))
            else:
                # Dark tile with a few stars; corners cut for a rounded look.
                cx, cy = min(x, size - 1 - x), min(y, size - 1 - y)
                corner = cx + cy < size // 8
                row.append((0, 0, 0, 0) if corner else SPACE + (255,))
        rows.append(row)
    for sx, sy in ((2, 3), (13, 2), (12, 12), (3, 13)):
        for y in range(sy * scale, (sy + 1) * scale):
            for x in range(sx * scale, (sx + 1) * scale):
                if rows[y][x][3] and SHIP[sy][sx] == ".":
                    rows[y][x] = (200, 200, 220, 255)
    return rows


def png_bytes(rows):
    h, w = len(rows), len(rows[0])
    raw = b"".join(b"\x00" + bytes(c for px in row for c in px) for row in rows)

    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)

    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


def ico_bytes(images):
    """An .ico holding PNG images (Windows Vista and later read these)."""
    header = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    entries, blobs = b"", b""
    for size, data in images:
        entries += struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(data), offset)
        blobs += data
        offset += len(data)
    return header + entries + blobs


def splash_pixels():
    """320x180 starfield with the ship and a planet edge, scaled 4x to 1280x720."""
    w, h, scale = 320, 180, 4
    rng = random.Random(62)
    img = [[SPACE for _ in range(w)] for _ in range(h)]
    for _ in range(220):
        x, y = rng.randrange(w), rng.randrange(h)
        b = rng.choice((90, 130, 170, 220))
        img[y][x] = (b, b, min(255, b + 20))
    # Planet curve along the bottom.
    for y in range(h):
        for x in range(w):
            dx, dy = x - 160, y - 520
            if dx * dx + dy * dy < 390 * 390:
                edge = dx * dx + dy * dy > 386 * 386
                img[y][x] = (110, 170, 235) if edge else (40, 85, 150)
    # The ship, 3x, in the middle.
    for sy, line in enumerate(SHIP):
        for sx, ch in enumerate(line):
            if ch in PALETTE:
                for yy in range(3):
                    for xx in range(3):
                        img[54 + sy * 3 + yy][136 + sx * 3 + xx] = PALETTE[ch]
    rows = []
    for y in range(h * scale):
        src = img[y // scale]
        rows.append([src[x // scale] + (255,) for x in range(w * scale)])
    return rows


def main():
    os.makedirs(OUT, exist_ok=True)
    with open(os.path.join(OUT, "app_icon.png"), "wb") as f:
        f.write(png_bytes(ship_pixels(256)))
    with open(os.path.join(OUT, "app_icon.ico"), "wb") as f:
        f.write(ico_bytes([(s, png_bytes(ship_pixels(s if s % 16 == 0 else 48))) for s in (16, 32, 48, 64, 128, 256)]))
    with open(os.path.join(OUT, "boot_splash.png"), "wb") as f:
        f.write(png_bytes(splash_pixels()))
    print("wrote", OUT)


if __name__ == "__main__":
    main()
