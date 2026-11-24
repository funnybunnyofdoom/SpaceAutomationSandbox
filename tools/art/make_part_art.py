"""Draws the ship-part sprites (#46) as Pixelorama sources: art_source/pixelorama/ship_parts/<part id>.pxo.

Run from the repository root:  python tools/art/make_part_art.py
Then export them with Pixelorama:  python tools/art/export_art.py

16x16 pixels per 2 m grid cell. Rotation 0 faces up (the part's front is the top edge; a thruster's nozzle is
at the bottom because it pushes towards the top). Each source has two layers, "base" (the shared panel) and
"detail" (what the part is), so a part can be redrawn by hand in Pixelorama without starting over.
Each part's accent is its placeholder colour from data/parts/parts.json, so players keep recognising it.
Original art, project-owned. Standard library only.
"""
import json
import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import pxo  # noqa: E402

N = 16
CLEAR = (0, 0, 0, 0)
OUT = (16, 18, 24, 255)
DARK = (40, 44, 54, 255)
PLATE = (78, 85, 99, 255)
PLATE_L = (104, 112, 128, 255)
HILITE = (150, 158, 174, 255)
SHADOW = (52, 57, 68, 255)
RIVET = (128, 136, 150, 255)


def hex_rgba(h):
    h = h.lstrip("#")
    return (int(h[0:2], 16), int(h[2:4], 16), int(h[4:6], 16), 255)


def mix(a, b, t):
    return tuple(round(a[i] + (b[i] - a[i]) * t) for i in range(3)) + (255,)


def shade(c, f):
    return mix(c, (0, 0, 0, 255), 1 - f) if f < 1 else mix(c, (255, 255, 255, 255), f - 1)


class Img:
    def __init__(self):
        self.px = [[CLEAR] * N for _ in range(N)]

    def set(self, x, y, c):
        if 0 <= x < N and 0 <= y < N:
            self.px[y][x] = c

    def rect(self, x0, y0, x1, y1, c):  # inclusive
        for y in range(y0, y1 + 1):
            for x in range(x0, x1 + 1):
                self.set(x, y, c)

    def outline(self, x0, y0, x1, y1, c):
        for x in range(x0, x1 + 1):
            self.set(x, y0, c); self.set(x, y1, c)
        for y in range(y0, y1 + 1):
            self.set(x0, y, c); self.set(x1, y, c)

    def hline(self, x0, x1, y, c):
        for x in range(x0, x1 + 1): self.set(x, y, c)

    def vline(self, x, y0, y1, c):
        for y in range(y0, y1 + 1): self.set(x, y, c)

    def disc(self, cx, cy, r, c):
        for y in range(N):
            for x in range(N):
                if (x + 0.5 - cx) ** 2 + (y + 0.5 - cy) ** 2 <= r * r: self.set(x, y, c)

    def ring(self, cx, cy, r0, r1, c):
        for y in range(N):
            for x in range(N):
                d = math.hypot(x + 0.5 - cx, y + 0.5 - cy)
                if r0 <= d <= r1: self.set(x, y, c)


def panel(full=True):
    """The shared base: a bevelled metal panel with an outline and corner rivets."""
    b = Img()
    b.rect(0, 0, 15, 15, PLATE)
    b.hline(1, 14, 1, PLATE_L); b.vline(1, 1, 14, PLATE_L)
    b.hline(1, 14, 14, SHADOW); b.vline(14, 1, 14, SHADOW)
    b.set(1, 1, HILITE)
    b.outline(0, 0, 15, 15, OUT)
    if full:
        for x, y in ((2, 2), (13, 2), (2, 13), (13, 13)): b.set(x, y, RIVET)
    return b


def pips(d, tier, accent):
    """Mk2 and Mk3 marks: small lit pips along the bottom-left."""
    for i in range(tier - 1):
        d.set(3 + 2 * i, 13, shade(accent, 1.5)); d.set(3 + 2 * i, 12, shade(accent, 1.2))


# --- One function per part: returns (base, detail) ---

def hull(a):
    b, d = panel(), Img()
    d.hline(2, 13, 7, SHADOW); d.hline(2, 13, 8, PLATE_L)
    d.vline(7, 2, 13, SHADOW); d.vline(8, 2, 13, PLATE_L)
    d.rect(7, 7, 8, 8, RIVET)
    return b, d


def cockpit(a):
    b, d = panel(), Img()
    glass, glass_d, glass_l = a, shade(a, 0.55), shade(a, 1.55)
    for y in range(2, 11):
        half = 5 if y > 4 else 3 + (y - 2)
        d.hline(8 - half, 7 + half, y, glass)
    d.hline(5, 10, 2, OUT)
    for x, y in ((4, 3), (11, 3), (3, 5), (12, 5)): d.set(x, y, OUT)
    d.vline(3, 5, 10, OUT); d.vline(12, 5, 10, OUT); d.hline(3, 12, 11, OUT)
    for y in range(7, 11): d.hline(4, 11, y, glass_d if y > 8 else glass)
    d.set(5, 4, glass_l); d.set(6, 4, glass_l); d.set(5, 5, glass_l); d.set(4, 6, glass_l)
    d.rect(4, 12, 11, 13, DARK); d.set(6, 12, (90, 220, 120, 255)); d.set(9, 12, (230, 80, 70, 255))
    return b, d


def thruster(a, tier=1):
    b = Img()
    b.rect(0, 0, 15, 8, PLATE); b.outline(0, 0, 15, 8, OUT)
    b.hline(1, 14, 1, PLATE_L); b.vline(1, 1, 7, PLATE_L)
    d = Img()
    d.hline(1, 14, 4, shade(a, 0.7)); d.hline(1, 14, 5, a); d.hline(1, 14, 6, shade(a, 0.7))
    # The bell flares from the housing towards the back (it pushes the craft up/forward).
    for i, y in enumerate(range(9, 16)):
        half = 3 + (i * 4 + 3) // 6
        d.hline(8 - half, 7 + half, y, PLATE_L if i < 2 else (PLATE if i < 5 else SHADOW))
        d.set(8 - half, y, OUT); d.set(7 + half, y, OUT)
        d.set(9 - half, y, HILITE)
    d.rect(6, 9, 9, 9, SHADOW)  # throat
    for i, y in enumerate(range(12, 16)):  # glowing exhaust opening
        half = 2 + i // 2
        d.hline(8 - half, 7 + half, y, shade(a, 1.45) if i >= 2 else shade(a, 1.1))
    for i in range(tier - 1):  # Mk pips on the housing
        d.set(3 + 2 * i, 2, shade(a, 1.5))
    return b, d


def battery(a, tier=1):
    b, d = panel(), Img()
    for x0 in (3, 9):
        d.rect(x0, 4, x0 + 3, 13, a)
        d.vline(x0, 4, 13, shade(a, 1.35)); d.vline(x0 + 3, 4, 13, shade(a, 0.6))
        d.rect(x0 + 1, 2, x0 + 2, 3, RIVET)
        d.hline(x0, x0 + 3, 9, shade(a, 0.5))
    d.hline(4, 6, 6, OUT); d.vline(5, 5, 7, OUT); d.hline(10, 12, 6, OUT)
    pips(d, tier, a)
    return b, d


def solar(a, tier=1):
    b = Img()
    b.rect(0, 0, 15, 15, (150, 156, 168, 255)); b.outline(0, 0, 15, 15, OUT)
    d = Img()
    cell = {1: 4, 2: 3, 3: 2}[tier]
    dark = shade(a, 0.55)
    d.rect(1, 1, 14, 14, dark)
    for y in range(1, 15):
        for x in range(1, 15):
            if (x - 1) % cell == 0 or (y - 1) % cell == 0: d.set(x, y, shade(a, 0.8))
    for k in range(2, 6): d.set(k, k - 1, shade(a, 1.5)); d.set(k + 1, k - 1, shade(a, 1.3))  # glint
    pips(d, tier, (200, 220, 255, 255))
    return b, d


def cargo(a):
    b, d = panel(full=False), Img()
    d.rect(2, 2, 13, 13, a); d.outline(2, 2, 13, 13, shade(a, 0.5))
    for i in range(10):
        d.set(3 + i, 3 + i, shade(a, 0.6)); d.set(12 - i, 3 + i, shade(a, 0.6))
    d.hline(3, 12, 3, shade(a, 1.3)); d.vline(3, 3, 12, shade(a, 1.3))
    for x, y in ((2, 2), (13, 2), (2, 13), (13, 13)): d.set(x, y, RIVET)
    return b, d


def drill(a, tier=1):
    b, d = panel(), Img()
    d.rect(3, 10, 12, 13, shade(a, 0.55)); d.hline(3, 12, 10, shade(a, 0.8))
    for i, y in enumerate(range(1, 10)):  # the bit points forward (up)
        half = 1 + i * 4 // 9
        d.hline(8 - half, 7 + half, y, a)
        d.set(8 - half, y, shade(a, 1.3)); d.set(7 + half, y, shade(a, 0.6))
    d.set(7, 0, shade(a, 1.4)); d.set(8, 0, a)
    for y in (3, 5, 7): d.set(7 + (y % 4 == 1), y, shade(a, 0.5)); d.set(8 - (y % 4 == 1), y + 1, shade(a, 0.5))
    pips(d, tier, a)
    return b, d


def fabricator(a, tier=1):
    b, d = panel(), Img()
    d.rect(3, 6, 12, 13, shade(a, 0.45)); d.outline(3, 6, 12, 13, a)
    d.hline(5, 10, 10, shade(a, 1.3)); d.vline(7, 8, 12, shade(a, 1.3))
    d.vline(4, 2, 6, RIVET); d.hline(4, 9, 2, RIVET); d.rect(9, 3, 10, 4, a); d.set(10, 5, shade(a, 1.5))
    pips(d, tier, a)
    return b, d


def anchor_clamp(a):
    b, d = panel(), Img()
    d.rect(4, 8, 11, 13, shade(a, 0.6))
    for x0, step in ((3, 1), (12, -1)):
        d.vline(x0, 3, 9, a)
        d.set(x0 + step, 2, a); d.set(x0 + 2 * step, 2, a); d.set(x0 + 2 * step, 3, shade(a, 1.3))
        d.vline(x0 + step, 4, 9, shade(a, 0.75))
    d.rect(6, 9, 9, 11, RIVET)
    return b, d


def foundation(a):
    b = Img(); b.rect(0, 0, 15, 15, a); b.outline(0, 0, 15, 15, OUT)
    d = Img()
    for y in (4, 8, 12): d.hline(1, 14, y, shade(a, 0.65)); d.hline(1, 14, y - 1, shade(a, 1.2))
    for y0, off in ((1, 0), (5, 4), (9, 0), (13, 4)):
        for x in range(1 + off, 15, 8): d.vline(x, y0, min(y0 + 2, 14), shade(a, 0.65))
    for x in range(1, 15, 2): d.set(x, 14, (210, 180, 60, 255) if (x // 2) % 2 else OUT)
    return b, d


def docking_port(a):
    b, d = panel(), Img()
    d.ring(8, 9, 3.2, 5.5, a); d.ring(8, 9, 4.6, 5.5, shade(a, 0.6))
    d.disc(8, 9, 3.2, DARK); d.disc(8, 9, 1.5, shade(a, 0.5))
    d.rect(6, 0, 9, 3, a); d.hline(6, 9, 0, shade(a, 1.4))  # the side it opens on: the front
    d.set(6, 6, shade(a, 1.5))
    return b, d


def storage_silo(a):
    b, d = panel(), Img()
    d.disc(8, 8, 6.3, shade(a, 0.6)); d.disc(8, 8, 5.4, a)
    d.ring(8, 8, 2.5, 3.2, shade(a, 0.7)); d.disc(8, 8, 1.4, RIVET)
    for x, y in ((5, 4), (4, 5), (6, 4)): d.set(x, y, shade(a, 1.45))
    return b, d


def refinery(a, tier=1):
    b, d = panel(), Img()
    d.rect(3, 3, 9, 12, shade(a, 0.5)); d.outline(3, 3, 9, 12, shade(a, 0.35))
    d.rect(4, 7, 8, 10, (255, 150, 40, 255)); d.hline(4, 8, 7, (255, 220, 120, 255))
    d.vline(11, 2, 13, RIVET); d.vline(12, 2, 13, SHADOW); d.hline(9, 11, 5, RIVET)
    d.rect(4, 3, 8, 4, a)
    pips(d, tier, a)
    return b, d


def slag_ejector(a):
    b, d = panel(), Img()
    d.rect(4, 4, 11, 13, a); d.outline(4, 4, 11, 13, shade(a, 0.6))
    d.rect(5, 0, 10, 4, DARK); d.vline(4, 0, 4, OUT); d.vline(11, 0, 4, OUT)
    for i in range(3): d.hline(7 - i, 8 + i, 7 + i, shade(a, 1.6))  # arrow: out the front
    d.vline(7, 10, 12, shade(a, 1.6)); d.vline(8, 10, 12, shade(a, 1.6))
    return b, d


def habitat(a):
    b, d = panel(full=False), Img()
    d.rect(2, 2, 13, 13, a)
    for x, y in ((2, 2), (13, 2), (2, 13), (13, 13)): d.set(x, y, PLATE)
    d.hline(3, 12, 2, shade(a, 1.3)); d.hline(3, 12, 13, shade(a, 0.6))
    lit, off = (255, 226, 140, 255), shade(a, 0.45)
    for i, (x, y) in enumerate(((4, 4), (7, 4), (10, 4), (4, 9), (7, 9), (10, 9))):
        d.rect(x, y, x + 1, y + 2, lit if i not in (2, 4) else off)
    return b, d


def farm(a):
    b, d = panel(), Img()
    soil = (92, 66, 44, 255)
    d.rect(2, 2, 13, 13, soil)
    for y in (3, 6, 9, 12):
        for x in range(3, 13, 2):
            d.set(x, y, a); d.set(x, y - 1, shade(a, 1.3) if (x + y) % 4 else a)
    return b, d


def robot_bay(a):
    b, d = panel(), Img()
    d.rect(4, 4, 11, 11, a); d.outline(4, 4, 11, 11, shade(a, 0.55))
    d.rect(6, 6, 7, 7, (90, 230, 255, 255)); d.rect(9, 6, 10, 7, (90, 230, 255, 255))
    d.hline(6, 10, 9, shade(a, 0.5))
    d.vline(8, 2, 3, RIVET); d.set(8, 1, (255, 90, 80, 255))
    d.rect(5, 12, 10, 13, shade(a, 0.7))
    return b, d


def warp_gate(a):
    b, d = panel(), Img()
    d.disc(8, 8, 6.5, (30, 16, 48, 255))
    d.ring(8, 8, 5.0, 6.6, a); d.ring(8, 8, 6.0, 6.6, shade(a, 0.55))
    for t in range(0, 24):
        ang = t * 0.5; r = 1 + t * 0.17
        d.set(int(8 + r * math.cos(ang)), int(8 + r * math.sin(ang)), shade(a, 1.2))
    d.disc(8, 8, 1.2, (240, 220, 255, 255))
    return b, d


def gate_defense(a):
    b, d = panel(), Img()
    d.disc(8, 9, 4.5, shade(a, 0.6)); d.disc(8, 9, 3.5, a)
    d.rect(7, 1, 8, 7, DARK); d.vline(7, 1, 7, SHADOW); d.hline(7, 8, 1, OUT)  # the barrel faces forward
    d.set(6, 8, shade(a, 1.5)); d.set(6, 9, shade(a, 1.3))
    return b, d


def research_lab(a):
    b, d = panel(), Img()
    for y in range(4, 10):  # a dish, open to the front
        half = 2 + (y - 4)
        d.hline(8 - half, 7 + half, y, a if y < 9 else shade(a, 0.6))
    d.hline(2, 13, 9, shade(a, 0.5))
    d.vline(8, 1, 6, RIVET); d.set(8, 1, (255, 255, 255, 255))
    d.rect(6, 10, 9, 13, shade(a, 0.55)); d.set(7, 11, (120, 255, 160, 255))
    return b, d


def fuel_generator(a):
    b, d = panel(), Img()
    d.rect(3, 6, 12, 13, shade(a, 0.55)); d.outline(3, 6, 12, 13, OUT)  # engine block
    d.rect(4, 8, 7, 12, a); d.vline(4, 8, 12, shade(a, 1.4))  # fuel tank
    d.hline(4, 7, 10, shade(a, 0.6))
    for y in (8, 10, 12): d.hline(9, 11, y, RIVET)  # cooling fins
    d.rect(9, 2, 10, 6, DARK); d.vline(9, 2, 6, SHADOW); d.hline(9, 10, 1, OUT)  # exhaust stack, out the front
    d.set(10, 2, (255, 170, 60, 255))
    return b, d


def electrolyser(a):
    b, d = panel(), Img()
    water, gas = (63, 143, 217, 255), (232, 244, 255, 255)
    d.rect(3, 6, 12, 13, shade(a, 0.5)); d.outline(3, 6, 12, 13, OUT)  # tank
    d.rect(4, 9, 11, 12, water); d.hline(4, 11, 9, shade(water, 1.3))
    d.vline(6, 4, 9, RIVET); d.vline(9, 4, 9, RIVET)  # electrodes
    d.set(6, 3, (255, 90, 80, 255)); d.set(9, 3, (90, 160, 255, 255))
    for x, y in ((5, 8), (7, 7), (8, 8), (10, 7)): d.set(x, y, gas)  # bubbles
    return b, d


def fuel_cell(a):
    b, d = panel(), Img()
    for i, x0 in enumerate((3, 6, 9)):  # stacked cell plates
        d.rect(x0, 3, x0 + 2, 12, shade(a, 0.6 if i % 2 else 0.8)); d.vline(x0, 3, 12, shade(a, 1.3))
    d.hline(3, 11, 2, OUT); d.hline(3, 11, 13, OUT)
    d.vline(12, 4, 11, RIVET); d.set(13, 5, (232, 244, 255, 255)); d.set(13, 9, (63, 143, 217, 255))  # gas in, water out
    return b, d


DRAW = {
    "hull": hull, "cockpit": cockpit, "thruster": thruster, "battery": battery, "solar_panel": solar,
    "cargo": cargo, "drill": drill, "fabricator": fabricator, "anchor_clamp": anchor_clamp,
    "foundation": foundation, "docking_port": docking_port, "storage_silo": storage_silo, "refinery": refinery,
    "slag_ejector": slag_ejector, "habitat": habitat, "farm": farm, "robot_bay": robot_bay,
    "thruster_mk2": lambda a: thruster(a, 2), "thruster_mk3": lambda a: thruster(a, 3),
    "drill_mk2": lambda a: drill(a, 2), "drill_mk3": lambda a: drill(a, 3),
    "solar_panel_mk2": lambda a: solar(a, 2), "solar_panel_mk3": lambda a: solar(a, 3),
    "battery_mk2": lambda a: battery(a, 2), "refinery_mk2": lambda a: refinery(a, 2),
    "fabricator_mk2": lambda a: fabricator(a, 2), "warp_gate": warp_gate, "gate_defense": gate_defense,
    "research_lab": research_lab,
    "fuel_generator": fuel_generator,
    "electrolyser": electrolyser, "fuel_cell": fuel_cell,
}


def flatten(base, detail):
    return [[detail.px[y][x] if detail.px[y][x][3] else base.px[y][x] for x in range(N)] for y in range(N)]


def main():
    parts = json.load(open(os.path.join("data", "parts", "parts.json"), encoding="utf-8"))["parts"]
    out = os.path.join("art_source", "pixelorama", "ship_parts")
    os.makedirs(out, exist_ok=True)
    missing = [p["id"] for p in parts if p["id"] not in DRAW]
    if missing:
        sys.exit(f"no drawing for: {', '.join(missing)}")
    sheet = []
    for p in parts:
        base, detail = DRAW[p["id"]](hex_rgba(p.get("placeholderColor", "#ff00ff")))
        target = os.path.join(out, p["id"] + ".pxo")
        layers = [("base", base.px), ("detail", detail.px)]
        unchanged = False
        if os.path.isfile(target):  # leave a source alone if its pixels haven't changed (it may have been edited by hand)
            try:
                unchanged = [(n, [list(r) for r in rows]) for n, rows in pxo.read_pxo(target)[2]] == [(n, [list(r) for r in rows]) for n, rows in layers]
            except Exception:
                unchanged = False
        if not unchanged:
            pxo.write_pxo(target, N, N, layers)
        sheet.append((p["id"], flatten(base, detail)))
    print(f"wrote {len(sheet)} sources to {out}")
    if len(sys.argv) > 1:  # a preview contact sheet (not part of the game): python make_part_art.py preview.png
        scale, cols, gap = 6, 8, 4
        rows_n = (len(sheet) + cols - 1) // cols
        W, H = cols * (N * scale + gap) + gap, rows_n * (N * scale + gap) + gap
        img = [[(24, 26, 34, 255)] * W for _ in range(H)]
        for i, (_, px) in enumerate(sheet):
            ox, oy = gap + (i % cols) * (N * scale + gap), gap + (i // cols) * (N * scale + gap)
            for y in range(N * scale):
                for x in range(N * scale):
                    c = px[y // scale][x // scale]
                    if c[3]: img[oy + y][ox + x] = c
        pxo.write_png(sys.argv[1], img)
        print("preview:", sys.argv[1], [s[0] for s in sheet])


if __name__ == "__main__":
    main()
