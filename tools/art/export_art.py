"""Exports Pixelorama sources to PNG with Pixelorama's own command line, and checks the result (#46).

    python tools/art/export_art.py            # sources newer than their PNG
    python tools/art/export_art.py --all      # everything

Convention: art_source/pixelorama/<group>/<name>.pxo  ->  assets/art/<group>/<name>.png
Pixelorama is found from the PIXELORAMA environment variable, else C:/dev/Tools/Pixelorama/Pixelorama.exe.
After each export the PNG is compared pixel by pixel with the source's layers blended (normal blend, fully
opaque or clear pixels), so a broken round trip is caught here rather than in the game.
"""
import os
import subprocess
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import pxo  # noqa: E402

SOURCE = os.path.join("art_source", "pixelorama")
EXPORT = os.path.join("assets", "art")


def pixelorama():
    exe = os.environ.get("PIXELORAMA", r"C:\dev\Tools\Pixelorama\Pixelorama.exe")
    if not os.path.isfile(exe):
        sys.exit(f"Pixelorama not found at {exe}: set PIXELORAMA to its executable.")
    return exe


def blended(path):
    w, h, layers = pxo.read_pxo(path)
    rows = [[(0, 0, 0, 0)] * w for _ in range(h)]
    for _, layer in layers:  # bottom to top
        for y in range(h):
            for x in range(w):
                if layer[y][x][3]: rows[y][x] = layer[y][x]
    return rows


def same(a, b):
    return all((p[3] == 0 and q[3] == 0) or p == q for ra, rb in zip(a, b) for p, q in zip(ra, rb)) and len(a) == len(b)


def main():
    everything = "--all" in sys.argv
    exe = pixelorama()
    done = failed = 0
    for group in sorted(os.listdir(SOURCE)):
        folder = os.path.join(SOURCE, group)
        if not os.path.isdir(folder):
            continue
        for name in sorted(os.listdir(folder)):
            if not name.endswith(".pxo"):
                continue
            src = os.path.abspath(os.path.join(folder, name))
            dst = os.path.abspath(os.path.join(EXPORT, group, name[:-4] + ".png"))
            if not everything and os.path.isfile(dst) and os.path.getmtime(dst) >= os.path.getmtime(src):
                continue
            os.makedirs(os.path.dirname(dst), exist_ok=True)
            subprocess.run([exe, "--headless", "--quit", "--", src, "--export", "--output", dst],
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=120)
            if not os.path.isfile(dst) or not same(pxo.read_png(dst), blended(src)):
                print(f"FAILED  {group}/{name}")
                failed += 1
            else:
                print(f"ok      {group}/{name[:-4]}.png")
                done += 1
    print(f"{done} exported, {failed} failed")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
