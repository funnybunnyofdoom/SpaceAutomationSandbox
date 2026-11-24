"""Draws the celestial-body art (#71-#74) as Pixelorama sources: art_source/pixelorama/bodies/<name>.pxo.

Run from the repository root:  python tools/art/make_body_art.py [preview.png]
Then export them with Pixelorama:  python tools/art/export_art.py

Surfaces are 128x128 tiles that repeat without a seam. The game (assets/shaders/body.gdshader) draws them inside
each body's true disc at 1, 8 and 64 m per pixel, tinted around the body's own colour, so only a tile's
contrast and pattern show in the game: its average colour is divided out. Two layers: "base" (ground) and
"detail" (cracks, pits, stones), so a tile can be redrawn by hand.
Original art, project-owned. Deterministic. Standard library only.
"""
import math
import os
import random
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import pxo  # noqa: E402

N = 128
CLEAR = (0, 0, 0, 0)


def rgb(h):
    return (int(h[0:2], 16), int(h[2:4], 16), int(h[4:6], 16), 255)


def blank(w=N, h=N):
    return [[CLEAR] * w for _ in range(h)]


def noise(seed, cells_x, cells_y=None):
    """Smooth value noise over the tile, repeating at its edges; about 0..1."""
    cells_y = cells_y or cells_x
    rng = random.Random(seed)
    grid = [[rng.random() for _ in range(cells_x)] for _ in range(cells_y)]
    out = []
    for y in range(N):
        fy = y * cells_y / N
        y0 = int(fy)
        ty = fy - y0
        ty = ty * ty * (3 - 2 * ty)
        row = []
        for x in range(N):
            fx = x * cells_x / N
            x0 = int(fx)
            tx = fx - x0
            tx = tx * tx * (3 - 2 * tx)
            a, b = grid[y0 % cells_y][x0 % cells_x], grid[y0 % cells_y][(x0 + 1) % cells_x]
            c, d = grid[(y0 + 1) % cells_y][x0 % cells_x], grid[(y0 + 1) % cells_y][(x0 + 1) % cells_x]
            row.append((a + (b - a) * tx) * (1 - ty) + (c + (d - c) * tx) * ty)
        out.append(row)
    return out


def blend(*parts):
    total = sum(w for w, _ in parts)
    return [[sum(w * n[y][x] for w, n in parts) / total for x in range(N)] for y in range(N)]


def ranked(field):
    """The field as ranks 0..1: every palette colour then gets a known share of the tile."""
    order = sorted((field[y][x], y, x) for y in range(N) for x in range(N))
    out = [[0.0] * N for _ in range(N)]
    for i, (_, y, x) in enumerate(order):
        out[y][x] = i / (N * N)
    return out


BAYER = [[0, 8, 2, 10], [12, 4, 14, 6], [3, 11, 1, 9], [15, 7, 13, 5]]


def paint(field, palette, stops, dither=0.05):
    """Palette colours by rank, with a thin dithered edge between neighbours."""
    f = ranked(field)
    out = blank()
    for y in range(N):
        for x in range(N):
            v = f[y][x] + ((BAYER[y % 4][x % 4] + 0.5) / 16 - 0.5) * dither
            i = 0
            while i < len(stops) and v > stops[i]:
                i += 1
            out[y][x] = palette[i]
    return out


def put(img, x, y, c):
    img[math.floor(y) % N][math.floor(x) % N] = c


def pit(d, cx, cy, r, dark, light, mid):
    """A small round pit lit from the top left (wraps over the tile's edge)."""
    for y in range(int(cy - r - 1), int(cy + r + 2)):
        for x in range(int(cx - r - 1), int(cx + r + 2)):
            dx, dy = x + 0.5 - cx, y + 0.5 - cy
            q = math.hypot(dx, dy)
            if q > r:
                continue
            side = (dx + dy) / (q + 0.01)
            if q > r - 1.2:
                put(d, x, y, light if side > 0.2 else dark)
            else:
                put(d, x, y, dark if side < -0.6 and q > r * 0.4 else mid)


def crack(d, rng, colour, steps, turn=0.45, edge=None):
    x, y, a = rng.random() * N, rng.random() * N, rng.random() * math.tau
    for _ in range(steps):
        put(d, x, y, colour)
        if edge:
            put(d, x + 1, y + 1, edge)
        a += rng.uniform(-turn, turn)
        x += math.cos(a)
        y += math.sin(a)


def rock():
    p = [rgb(c) for c in ("5f4f40", "7a6652", "917a62", "a88f74", "bfa587")]
    base = paint(blend((3, noise(1, 8)), (2, noise(2, 16)), (1.4, noise(3, 32))), p, (0.08, 0.34, 0.72, 0.94))
    d, rng = blank(), random.Random(4)
    for _ in range(26):  # stones with a lit top-left
        x, y, w, h = rng.randrange(N), rng.randrange(N), rng.randint(2, 4), rng.randint(2, 3)
        for j in range(h):
            for i in range(w):
                put(d, x + i, y + j, p[4] if j == 0 or i == 0 else p[3])
        for i in range(w):
            put(d, x + i + 1, y + h, p[0])
    for _ in range(9):
        pit(d, rng.randrange(N), rng.randrange(N), rng.uniform(2.5, 5), p[0], p[4], p[1])
    for _ in range(5):
        crack(d, rng, p[0], rng.randint(14, 30))
    return base, d


def ice():
    p = [rgb(c) for c in ("8fb4cc", "aecde0", "c2dcea", "d6eaf4", "f2fbff")]
    base = paint(blend((3, noise(11, 4, 6)), (1.5, noise(12, 12)), (0.6, noise(13, 32))), p, (0.06, 0.3, 0.74, 0.95), 0.1)
    d, rng = blank(), random.Random(14)
    for _ in range(14):
        crack(d, rng, p[0], rng.randint(30, 80), 0.28, p[4])
    for _ in range(40):
        put(d, rng.randrange(N), rng.randrange(N), p[4])
    return base, d


def planet():
    p = [rgb(c) for c in ("4a4038", "665a4e", "807264", "988a7a", "b4a693")]
    base = paint(blend((4, noise(21, 4)), (2, noise(22, 9)), (1, noise(23, 24))), p, (0.16, 0.44, 0.74, 0.93))
    d, rng = blank(), random.Random(24)
    for _ in range(7):  # ridges: a lit edge with its shadow
        x, y, a = rng.random() * N, rng.random() * N, rng.random() * math.tau
        for _ in range(rng.randint(20, 44)):
            put(d, x, y, p[4])
            put(d, x + 1, y + 1, p[0])
            put(d, x + 1, y, p[1])
            a += rng.uniform(-0.35, 0.35)
            x += math.cos(a)
            y += math.sin(a)
    for _ in range(6):
        crack(d, rng, p[0], rng.randint(20, 40), 0.25)
    return base, d


def regolith():
    p = [rgb(c) for c in ("74747c", "8c8c94", "9c9ca4", "adadb4", "c8c8ce")]
    base = paint(blend((3, noise(31, 5)), (1.2, noise(32, 20)), (1, noise(33, 64))), p, (0.07, 0.36, 0.74, 0.96), 0.12)
    d, rng = blank(), random.Random(34)
    for r in (11, 8, 6.5, 5, 5, 4, 4, 3.5, 3, 3, 3, 2.5, 2.5, 2.5, 2, 2, 2, 2, 2, 2):
        pit(d, rng.randrange(N), rng.randrange(N), r, p[0], p[4], p[1])
    return base, d


def gas_bands():
    p = [rgb(c) for c in ("8a5c38", "a87848", "c69858", "dcb67c", "f0d8a8")]
    rng = random.Random(41)
    waves = [(k, rng.random() * math.tau, 1 / math.sqrt(k)) for k in (2, 3, 5, 8, 13)]
    level = [sum(w * math.sin(math.tau * k * y / N + ph) for k, ph, w in waves) for y in range(N)]  # each row's band level
    wobble = noise(42, 4, 8)
    streak = noise(43, 8, 64)
    field = [[level[math.floor(y + 10 * (wobble[y][x] - 0.5)) % N] + (streak[y][x] - 0.5) for x in range(N)] for y in range(N)]
    base = paint(field, p, (0.12, 0.36, 0.68, 0.9), 0.08)
    d = blank()
    for cx, cy, rx, ry in ((36, 44, 9, 4), (96, 92, 6, 3), (80, 20, 4, 2)):  # storms
        for y in range(cy - ry, cy + ry + 1):
            for x in range(cx - rx, cx + rx + 1):
                q = ((x - cx) / rx) ** 2 + ((y - cy) / ry) ** 2
                if q <= 1:
                    put(d, x, y, p[4] if q > 0.6 else p[0] if q > 0.3 else p[1])
    return base, d


def star_surface():
    p = [rgb(c) for c in ("c8c8c8", "dcdcdc", "ececec", "ffffff")]
    rng = random.Random(51)
    seeds = [(rng.random() * N, rng.random() * N) for _ in range(110)]
    field = [[0.0] * N for _ in range(N)]
    for y in range(N):
        for x in range(N):  # granules: bright cells, dark lanes between
            ds = sorted(min(abs(x - sx), N - abs(x - sx)) ** 2 + min(abs(y - sy), N - abs(y - sy)) ** 2 for sx, sy in seeds)
            field[y][x] = math.sqrt(ds[1]) - math.sqrt(ds[0])
    field = ranked(field)
    base = paint(blend((1, field), (0.7, noise(52, 8))), p, (0.16, 0.42, 0.8), 0.06)
    d = blank()
    for _ in range(3):
        pit(d, rng.randrange(N), rng.randrange(N), rng.uniform(2, 4), rgb("a0a0a0"), p[0], rgb("888888"))
    return base, d


def ore_veins():
    """A mask: white where a vein of ore shows. Kept to thin branching lines and specks."""
    base = [[(0, 0, 0, 255)] * N for _ in range(N)]
    d, rng = blank(), random.Random(61)
    white = (255, 255, 255, 255)
    for _ in range(22):
        x, y, a = rng.random() * N, rng.random() * N, rng.random() * math.tau
        for _ in range(rng.randint(18, 46)):
            put(d, x, y, white)
            if rng.random() < 0.55:
                put(d, x + 1, y, white)
            if rng.random() < 0.3:
                put(d, x, y + 1, white)
            a += rng.uniform(-0.5, 0.5)
            x += math.cos(a)
            y += math.sin(a)
    for _ in range(90):
        x, y = rng.randrange(N), rng.randrange(N)
        put(d, x, y, white)
        put(d, x + 1, y, white)
    return base, d


def from_text(rows, colours):
    return [[colours[c] for c in row] for row in rows]


def site_marker():
    """A survey stake at the start of a site; up is away from the body."""
    c = {".": CLEAR, "o": rgb("101218"), "w": rgb("ffffff"), "g": rgb("9aa4b4"), "d": rgb("5c6472")}
    rows = ["..oooo..", ".owwwgo.", ".owggdo.", "..oggo..", "..owdo..", "..ogdo..", "..ogdo..", "...oo..."]
    return blank(8, 8), from_text(rows, c)


def claim_flag():
    """A pennant for a body in a civilization's system; white so the game can tint it."""
    c = {".": CLEAR, "o": rgb("101218"), "w": rgb("ffffff"), "l": rgb("d4d4d4"), "g": rgb("8c8c8c")}
    rows = [".oo.....", ".owooo..", ".owwwwoo", ".owlwwwo", ".owllooo", ".owooo..", ".og.....", ".og....."]
    return blank(8, 8), from_text(rows, c)


def star_glow():
    """The galaxy view's star: a disc with a dithered halo and four rays; white so the game can tint it."""
    n = 48
    base, d = blank(n, n), blank(n, n)
    white, soft, faint = rgb("ffffff"), rgb("d0d0d0"), rgb("808080")
    for y in range(n):
        for x in range(n):
            dx, dy = x + 0.5 - n / 2, y + 0.5 - n / 2
            q = math.hypot(dx, dy)
            if q <= 8:
                d[y][x] = white
            elif q <= 10:
                d[y][x] = soft
            else:
                glow = (1 - (q - 10) / 13) * 0.6
                ray = max(0.0, 1 - min(abs(dx), abs(dy)) / 1.5) * max(0.0, 1 - q / 24)
                if max(glow, ray) > (BAYER[y % 4][x % 4] + 0.5) / 16:
                    base[y][x] = soft if q < 14 else faint
    return base, d


ART = {"rock": rock, "ice": ice, "planet": planet, "regolith": regolith, "gas_bands": gas_bands, "star_surface": star_surface,
       "ore_veins": ore_veins, "site_marker": site_marker, "claim_flag": claim_flag, "star_glow": star_glow}


def main():
    out = os.path.join("art_source", "pixelorama", "bodies")
    os.makedirs(out, exist_ok=True)
    tiles = []
    for name, draw in ART.items():
        base, detail = draw()
        h, w = len(base), len(base[0])
        target = os.path.join(out, name + ".pxo")
        layers = [("base", base), ("detail", detail)]
        unchanged = False
        if os.path.isfile(target):  # leave a source alone if its pixels haven't changed
            try:
                unchanged = [(n, [list(r) for r in rows]) for n, rows in pxo.read_pxo(target)[2]] == layers
            except Exception:
                unchanged = False
        if not unchanged:
            pxo.write_pxo(target, w, h, layers)
        tiles.append([[detail[y][x] if detail[y][x][3] else base[y][x] for x in range(w)] for y in range(h)])
    print(f"wrote {len(tiles)} sources to {out}")
    if len(sys.argv) > 1:  # preview sheet: each surface tiled 2x2, so seams would show
        cols, cell = 4, 2 * N + 8
        sheet = [[(24, 26, 34, 255)] * (cols * cell) for _ in range(((len(tiles) + cols - 1) // cols) * cell)]
        for i, t in enumerate(tiles):
            for y in range(2 * N):
                for x in range(2 * N):
                    c = t[y % len(t)][x % len(t[0])]
                    if c[3]:
                        sheet[(i // cols) * cell + y][(i % cols) * cell + x] = c
        pxo.write_png(sys.argv[1], sheet)


if __name__ == "__main__":
    main()
