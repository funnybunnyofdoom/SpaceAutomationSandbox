"""Draws the effect sprites (#130) as Pixelorama sources: art_source/pixelorama/fx/<name>.pxo, 16x16, one layer.

Run from the repository root:  python tools/art/make_fx_art.py   then   python tools/art/export_art.py
Flames, flashes and sparks are drawn white-to-grey so the game tints them (an ion drive blue, a rocket orange); explosions,
debris, smoke and dust have their own colours. Pixels are fully opaque or clear; the game fades them. Original art, project-owned.
"""
import math
import os
import random
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import pxo  # noqa: E402

N = 16
CLEAR = (0, 0, 0, 0)


def blank():
    return [[CLEAR] * N for _ in range(N)]


def put(px, x, y, c):
    if 0 <= x < N and 0 <= y < N: px[y][x] = c


def grey(v):
    v = max(0, min(255, int(v)))
    return (v, v, v, 255)


def flame(frame):
    """A flame pointing down from the top edge (the nozzle), white at its root and greying out; three flickers."""
    px = blank()
    rng = random.Random(10 + frame)
    length = 13 + frame
    for y in range(N):
        if y >= length: break
        t = y / length
        half = (3.5 - 2.5 * t) * (1 + 0.15 * math.sin(frame * 2.1 + y * 0.9))
        for x in range(N):
            d = abs(x + 0.5 - 8)
            if d <= half and rng.random() > t * 0.35:
                put(px, x, y, grey(255 - 140 * t - 40 * d / max(half, 0.5)))
    return px


def flash(frame):
    """A muzzle flash: a bright cross, then a smaller one."""
    px = blank()
    size = 7 - frame * 3
    for i in range(-size, size + 1):
        put(px, 8 + i, 8, grey(255 - abs(i) * 18)); put(px, 8, 8 + i, grey(255 - abs(i) * 18))
    for d in range(-size // 2, size // 2 + 1):
        put(px, 8 + d, 8 + d, grey(220)); put(px, 8 + d, 8 - d, grey(220))
    return px


def spark(frame):
    """A few sparks flying apart."""
    px = blank()
    rng = random.Random(20 + frame)
    for _ in range(7):
        a = rng.uniform(0, 2 * math.pi)
        r = 2 + frame * 2.5 + rng.uniform(0, 2)
        put(px, int(8 + math.cos(a) * r), int(8 + math.sin(a) * r), grey(255 - frame * 30))
    return px


FIRE = [(255, 250, 220, 255), (255, 220, 120, 255), (250, 160, 60, 255), (220, 90, 40, 255), (140, 60, 50, 255), (90, 80, 80, 255)]


def explosion(frame):
    """Six frames: a white core, a fireball, then smoke breaking up."""
    px = blank()
    rng = random.Random(30 + frame)
    radius = 2.5 + frame * 1.2
    for y in range(N):
        for x in range(N):
            d = math.hypot(x + 0.5 - 8, y + 0.5 - 8) + rng.uniform(-0.8, 0.8)
            if d > radius: continue
            if frame >= 4 and rng.random() < 0.25 * (frame - 3): continue  # breaking up
            k = min(len(FIRE) - 1, int(d / radius * 3 + frame * 0.8))
            put(px, x, y, FIRE[k])
    return px


DEBRIS = [(120, 126, 140, 255), (88, 92, 104, 255), (60, 62, 72, 255), (150, 110, 80, 255)]


def debris(frame):
    """A chunk of hull: small, jagged."""
    px = blank()
    rng = random.Random(40 + frame)
    cx, cy = 8, 8
    for _ in range(9 + frame * 2):
        x, y = cx + rng.randint(-2, 2), cy + rng.randint(-2, 2)
        put(px, x, y, DEBRIS[rng.randint(0, 2)])
    put(px, cx - 1, cy - 1, DEBRIS[0]); put(px, cx + 1, cy + 1, DEBRIS[2]); put(px, cx, cy, DEBRIS[3] if frame == 3 else DEBRIS[1])
    return px


def smoke(frame):
    """A puff of grey smoke, larger each frame."""
    px = blank()
    rng = random.Random(50 + frame)
    radius = 2 + frame * 1.5
    for y in range(N):
        for x in range(N):
            d = math.hypot(x + 0.5 - 8, y + 0.5 - 8)
            if d <= radius and rng.random() < 0.85 - frame * 0.15:
                put(px, x, y, grey(120 - d * 6 + rng.randint(-10, 10)))
    return px


def dust(frame):
    """Rock dust kicked up by a drill."""
    px = blank()
    rng = random.Random(60 + frame)
    for _ in range(10 + frame * 4):
        a = rng.uniform(math.pi * 1.1, math.pi * 1.9)
        r = rng.uniform(1, 3 + frame * 2)
        v = rng.randint(110, 160)
        put(px, int(8 + math.cos(a) * r), int(12 + math.sin(a) * r), (v, v - 15, v - 35, 255))
    return px


def missile():
    """A missile pointing up."""
    px = blank()
    for y in range(4, 13): put(px, 7, y, (200, 205, 215, 255)); put(px, 8, y, (160, 165, 175, 255))
    put(px, 7, 3, (230, 80, 70, 255)); put(px, 8, 3, (200, 60, 50, 255))
    for x in (6, 9): put(px, x, 11, (120, 125, 135, 255)); put(px, x, 12, (120, 125, 135, 255))
    return px


def glint():
    """A four-pointed glint, for something done or jumping."""
    px = blank()
    for i in range(-6, 7):
        put(px, 8 + i, 8, grey(255 - abs(i) * 30)); put(px, 8, 8 + i, grey(255 - abs(i) * 30))
    for d in (-1, 1): put(px, 8 + d, 8 + d, grey(200)); put(px, 8 + d, 8 - d, grey(200))
    return px


def streak():
    """A speed streak for cruising: a thin line fading back."""
    px = blank()
    for y in range(N): put(px, 8, y, grey(255 - y * 12))
    return px


SPRITES = {}
for i in range(3): SPRITES[f"flame_{i}"] = flame(i)
for i in range(2): SPRITES[f"flash_{i}"] = flash(i)
for i in range(3): SPRITES[f"spark_{i}"] = spark(i)
for i in range(6): SPRITES[f"explosion_{i}"] = explosion(i)
for i in range(4): SPRITES[f"debris_{i}"] = debris(i)
for i in range(3): SPRITES[f"smoke_{i}"] = smoke(i)
for i in range(2): SPRITES[f"dust_{i}"] = dust(i)
SPRITES["missile"] = missile()
SPRITES["glint"] = glint()
SPRITES["streak"] = streak()


def main():
    out = os.path.join("art_source", "pixelorama", "fx")
    os.makedirs(out, exist_ok=True)
    for name, rows in SPRITES.items():
        target = os.path.join(out, name + ".pxo")
        if os.path.isfile(target):
            try:
                if [list(r) for r in pxo.read_pxo(target)[2][0][1]] == [list(r) for r in rows]: continue
            except Exception:
                pass
        pxo.write_pxo(target, N, N, [("fx", rows)])
    print(f"{len(SPRITES)} effect sprites in {out}")


if __name__ == "__main__":
    main()
