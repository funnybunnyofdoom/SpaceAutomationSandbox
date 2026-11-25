"""Draws the interface icons (#99) as Pixelorama sources: art_source/pixelorama/icons/<name>.pxo, 16x16.

    python tools/art/make_ui_art.py     then   python tools/art/export_art.py

Menu, status and instrument icons are pale with a cyan accent; materials (mat_<id>) use their colour from
data/materials/materials.json, with a shape for the kind of material. Original art, project-owned.
"""
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import pxo  # noqa: E402
from make_part_art import CLEAR, N, OUT, Img, hex_rgba, shade  # noqa: E402

W = (222, 234, 246, 255)
G = (150, 170, 192, 255)
C = (92, 214, 255, 255)
Y = (255, 206, 92, 255)
R = (255, 108, 92, 255)
L = (122, 226, 140, 255)
P = (190, 150, 255, 255)
COLORS = {"W": W, "G": G, "C": C, "Y": Y, "R": R, "L": L, "P": P}


def poly(d, rows, x0, y0, colors=None):
    """Pixels from text: each letter is a colour."""
    for y, row in enumerate(rows):
        for x, ch in enumerate(row):
            c = {**COLORS, **(colors or {})}.get(ch)
            if c: d.set(x0 + x, y0 + y, c)


def line(d, x0, y0, x1, y1, c):
    n = max(abs(x1 - x0), abs(y1 - y0), 1)
    for i in range(n + 1): d.set(round(x0 + (x1 - x0) * i / n), round(y0 + (y1 - y0) * i / n), c)


def up(d, x, y, h, c):  # a solid arrowhead pointing up
    for i in range(h): d.hline(x - i, x + i, y + i, c)


def pause(d): d.rect(4, 3, 6, 12, W); d.rect(9, 3, 11, 12, W)
def editor(d):
    d.outline(2, 2, 13, 13, G)
    for i in (6, 10): d.hline(2, 13, i, G); d.vline(i, 2, 13, G)
    d.rect(7, 7, 9, 9, C); d.rect(7, 3, 9, 5, W); d.rect(3, 7, 5, 9, W)
def shipyard(d):
    d.vline(3, 3, 13, G); d.hline(3, 12, 3, G); d.vline(12, 3, 5, G); line(d, 3, 6, 6, 3, G); d.hline(2, 13, 13, G)
    up(d, 11, 6, 3, C); d.rect(10, 9, 12, 11, W)
def fleet(d):
    for x, y, c in ((7, 2, C), (3, 8, W), (11, 8, W)): up(d, x, y, 3, c); d.rect(x - 1, y + 3, x + 1, y + 4, c)
def swarm(d):
    for x, y in ((7, 2), (3, 4), (11, 4), (5, 8), (9, 8), (2, 11), (7, 12), (12, 11)): d.rect(x, y, x + 1, y + 1, C if (x + y) % 2 else W)
def cargo(d): d.rect(3, 4, 12, 12, shade(Y, .75)); d.outline(3, 4, 12, 12, Y); d.hline(3, 12, 7, Y); d.rect(7, 4, 8, 7, W)
def trade(d):
    d.hline(3, 11, 5, C); line(d, 11, 5, 9, 3, C); line(d, 11, 5, 9, 7, C)
    d.hline(4, 12, 10, W); line(d, 4, 10, 6, 8, W); line(d, 4, 10, 6, 12, W)
def job(d): d.rect(2, 10, 5, 13, W); d.rect(10, 2, 13, 5, C); line(d, 5, 10, 10, 5, G); d.rect(7, 7, 8, 8, Y)
def map_icon(d): d.ring(8, 8, 5.6, 6.5, G); d.ring(8, 8, 3.2, 4, G); d.disc(8, 8, 1.8, Y); d.rect(12, 5, 13, 6, C); d.rect(4, 9, 5, 10, W)
def research(d):
    d.hline(6, 9, 2, W); d.vline(6, 2, 6, W); d.vline(9, 2, 6, W)
    for i in range(6):
        if i > 1: d.hline(6 - min(i, 4), 9 + min(i, 4), 7 + i, C)
        d.set(6 - min(i, 4), 7 + i, W); d.set(9 + min(i, 4), 7 + i, W)
    d.hline(2, 13, 13, W)
def power(d): poly(d, ["....YYY.", "...YYY..", "..YYY...", ".YYYYYY.", "...YYY..", "..YYY...", "..YY....", ".YY.....", ".Y......"], 4, 3)
def laws(d):
    d.vline(7, 2, 12, W); d.vline(8, 2, 12, W); d.hline(3, 12, 4, W); d.hline(5, 10, 13, W)
    for x in (3, 12): d.vline(x, 5, 7, G); d.hline(x - 1, x + 1, 8, C); d.hline(x - 1, x + 1, 9, C)
def life(d):
    for y in range(3, 11): d.hline(10 - min(y - 1, 7), 12 - max(0, y - 6), y, L)
    line(d, 3, 13, 10, 5, shade(L, .5))
def milestones(d):
    poly(d, ["AYYYYYYA", "AYYYYYYA", "AYYYYYYA", ".YYYYYY.", "..YYYY..", "...YY...", "...YY...", "..YYYY..", ".YYYYYY."], 4, 3, {"A": shade(Y, .7)})
def settings(d):
    d.ring(8, 8, 2.5, 5, W)
    for x, y in ((7, 1), (7, 13), (1, 7), (13, 7), (3, 3), (11, 3), (3, 11), (11, 11)): d.rect(x, y, x + 1, y + 1, W)
def help_icon(d): poly(d, [".CCCC.", "CC..CC", "....CC", "...CC.", "..CC..", "..CC..", "......", "..CC..", "..CC.."], 5, 3)
def debug(d): poly(d, ["W..WW..W", ".W.WW.W.", "..WWWW..", "WWWRRWWW", "..WRRW..", "WWWRRWWW", "..WWWW..", ".W.WW.W.", "W......W"], 4, 3)
def diagnostics(d): d.hline(2, 13, 13, G); d.vline(2, 2, 13, G); line(d, 3, 11, 6, 7, C); line(d, 6, 7, 9, 9, C); line(d, 9, 9, 13, 3, C)
def battery(d): d.outline(3, 4, 12, 13, W); d.rect(6, 2, 9, 3, W); d.rect(5, 8, 10, 11, L)
def hull(d): poly(d, ["WWWWWWWW", "WGGGGGGW", "WGGGGGGW", "WGGGGGGW", "WGGGGGGW", ".WGGGGW.", "..WGGW..", "...WW..."], 4, 4)
def shield(d): d.ring(8, 8, 5, 6.5, C); d.rect(6, 6, 9, 9, W)
def speed(d): d.ring(8, 9, 5.2, 6.5, G); d.rect(0, 12, 15, 15, CLEAR); line(d, 8, 9, 11, 5, C); d.rect(7, 8, 8, 9, W)
def gravity(d):
    d.vline(7, 2, 9, C); d.vline(8, 2, 9, C); d.hline(2, 13, 13, G)
    for i in range(4): d.hline(7 - i, 8 + i, 11 - i, C)
def heading(d): d.ring(8, 8, 5.5, 6.5, G); up(d, 8, 4, 4, C); d.rect(7, 8, 9, 10, W)
def warn(d):
    for i in range(11): d.hline(8 - (i + 1) // 2, 8 + (i + 1) // 2, 2 + i, Y)
    d.vline(8, 6, 9, OUT); d.set(8, 11, OUT)
def danger(d):
    for i in range(11): d.hline(8 - (i + 1) // 2, 8 + (i + 1) // 2, 2 + i, R)
    d.vline(8, 6, 9, W); d.set(8, 11, W)
def clock(d): d.ring(8, 8, 5.5, 6.5, W); d.vline(8, 4, 8, C); d.hline(8, 10, 8, C)
def target(d): d.ring(8, 8, 4, 5, C); d.hline(1, 4, 8, W); d.hline(11, 14, 8, W); d.vline(8, 1, 4, W); d.vline(8, 11, 14, W)
def gate(d): d.ring(8, 8, 4.6, 6.6, P); d.disc(8, 8, 2, C)
def crew(d): d.disc(8, 5, 2.6, W); d.rect(4, 9, 11, 13, C); d.set(4, 9, CLEAR); d.set(11, 9, CLEAR)
def docked(d): d.rect(2, 6, 6, 10, W); d.rect(9, 6, 13, 10, C); d.hline(7, 8, 8, Y)
def landed(d): up(d, 8, 2, 4, W); d.rect(6, 6, 10, 9, W); line(d, 5, 10, 3, 12, G); line(d, 11, 10, 13, 12, G); d.hline(1, 14, 13, C)
def orbit(d): d.ring(8, 8, 5.5, 6.5, G); d.disc(8, 8, 2.6, C); d.rect(12, 3, 13, 4, W)
def weapon(d): d.rect(3, 7, 9, 9, W); d.rect(10, 7, 13, 8, G); d.rect(4, 10, 6, 12, W); d.set(14, 7, R); d.set(14, 8, R)
def kit(d): d.outline(3, 5, 12, 13, W); d.hline(3, 12, 8, W); d.rect(7, 2, 8, 5, C); d.rect(6, 3, 9, 4, C)
def message(d): d.outline(2, 3, 13, 10, W); d.hline(4, 11, 5, C); d.hline(4, 9, 7, C); d.set(4, 11, W); d.set(4, 12, W); d.set(5, 11, W)
def goal(d): d.vline(4, 2, 13, G); d.rect(5, 2, 12, 7, L); d.rect(5, 4, 12, 5, shade(L, 1.4))
def mining(d): line(d, 3, 13, 10, 6, shade(Y, .7)); line(d, 4, 13, 11, 6, shade(Y, .7)); line(d, 6, 3, 13, 10, W); line(d, 7, 3, 13, 9, W)
def freight(d): d.rect(2, 5, 8, 10, Y); d.rect(9, 7, 13, 10, W); d.disc(5, 11.5, 1.6, G); d.disc(11, 11.5, 1.6, G)
def survey(d): d.ring(7, 7, 3.5, 5, C); line(d, 10, 10, 13, 13, W); line(d, 11, 10, 13, 12, W)
def salvage(d): d.vline(8, 2, 6, G); d.hline(5, 11, 7, W); line(d, 5, 7, 3, 11, W); line(d, 11, 7, 13, 11, W); line(d, 3, 11, 6, 13, C); line(d, 13, 11, 10, 13, C)
def star(d):
    d.disc(8, 8, 4.2, Y)
    for x, y in ((8, 1), (8, 14), (1, 8), (14, 8), (3, 3), (12, 3), (3, 12), (12, 12)): d.set(x, y, Y)
def planet(d): d.disc(8, 8, 6, (70, 140, 220, 255)); d.rect(5, 5, 8, 7, L); d.rect(9, 9, 11, 10, L)
def moon(d): d.disc(8, 8, 5, G); d.rect(6, 6, 7, 7, shade(G, .7)); d.rect(9, 9, 9, 10, shade(G, .7))
def asteroid(d):
    poly(d, ["..AAAA..", ".ABAAAA.", "ABAAASAA", "AAASAAAA", "AAAAAASA", ".ASAAAA.", "..AAA..."], 4, 4, {"A": (138, 120, 104, 255), "B": (180, 162, 142, 255), "S": (90, 78, 68, 255)})
def gas(d): d.disc(8, 8, 6, (220, 180, 130, 255)); d.hline(3, 12, 6, (180, 130, 90, 255)); d.hline(2, 13, 9, (180, 130, 90, 255))
def ok(d): d.disc(8, 8, 4.5, L); line(d, 5, 8, 7, 10, OUT); line(d, 7, 10, 11, 6, OUT)
def idle(d): d.ring(8, 8, 3, 4.5, G)
def work(d): d.ring(8, 8, 3.5, 5, C); up(d, 8, 5, 3, C)
def wait(d): d.hline(5, 11, 3, Y); d.hline(5, 11, 13, Y); line(d, 5, 4, 11, 12, Y); line(d, 11, 4, 5, 12, Y); d.hline(7, 9, 11, Y)


def undo(d): d.hline(5, 11, 5, W); d.vline(12, 6, 9, W); d.hline(6, 11, 10, W); line(d, 4, 5, 7, 2, C); line(d, 4, 5, 7, 8, C); line(d, 5, 5, 8, 2, C); line(d, 5, 5, 8, 8, C)
def redo(d):
    u = Img(); undo(u)
    for y in range(N):
        for x in range(N): d.set(N - 1 - x, y, u.px[y][x])
def mirror_h(d): d.vline(8, 1, 14, Y); d.rect(3, 5, 5, 10, C); d.outline(11, 5, 13, 10, W)
def mirror_v(d): d.hline(1, 14, 8, Y); d.rect(5, 3, 10, 5, C); d.outline(5, 11, 10, 13, W)
def tool_draw(d): line(d, 4, 12, 11, 5, W); line(d, 5, 12, 12, 5, W); d.rect(11, 3, 13, 5, C); d.rect(2, 12, 4, 13, Y)
def tool_line(d): line(d, 3, 12, 12, 3, C); d.rect(2, 11, 4, 13, W); d.rect(11, 2, 13, 4, W)
def tool_rect(d): d.rect(3, 4, 12, 11, C); d.outline(3, 4, 12, 11, W)
def tool_outline(d): d.outline(3, 4, 12, 11, C); d.outline(2, 3, 13, 12, C)
def tool_fill(d): d.rect(4, 6, 10, 12, W); line(d, 4, 6, 7, 3, W); line(d, 10, 6, 7, 3, W); d.rect(12, 9, 13, 12, C); d.rect(5, 8, 9, 11, C)
def tool_select(d):
    for i in range(2, 14, 3):
        for k in (2, 13): d.hline(i, i + 1, k, W); d.vline(k, i, i + 1, W)
    d.rect(6, 6, 9, 9, C)
def copy(d): d.outline(5, 2, 12, 9, G); d.rect(3, 5, 9, 13, shade(C, .6)); d.outline(3, 5, 9, 13, W)
def paste(d): d.outline(3, 3, 12, 13, W); d.rect(6, 2, 9, 4, Y); d.rect(5, 6, 10, 11, C)
def cut(d): line(d, 3, 2, 10, 10, W); line(d, 12, 2, 5, 10, W); d.ring(4, 12, 1, 2.2, R); d.ring(11, 12, 1, 2.2, R)
def trash(d): d.rect(4, 5, 11, 13, G); d.hline(3, 12, 4, W); d.hline(6, 9, 2, W); d.vline(6, 7, 11, OUT); d.vline(9, 7, 11, OUT)
def turn(d): d.ring(8, 8, 4, 5.4, W); d.rect(8, 2, 13, 7, CLEAR); up(d, 12, 4, 3, C)
def pick(d): line(d, 3, 12, 9, 6, C); line(d, 4, 12, 10, 6, C); d.rect(9, 3, 12, 6, W); d.set(2, 13, Y)
def replace(d): d.rect(2, 2, 6, 6, C); d.rect(9, 9, 13, 13, W); d.hline(8, 12, 4, Y); d.vline(12, 4, 7, Y); d.hline(3, 7, 11, Y); d.vline(3, 8, 11, Y)
def edit_program(d):
    for y in (3, 7, 11): d.rect(2, y, 3, y + 1, C); d.hline(6, 13, y, W); d.hline(6, 10, y + 1, G)
def find(d): d.ring(7, 7, 3, 4.4, C); line(d, 10, 10, 13, 13, W); line(d, 10, 11, 12, 13, W)
def favourite(d):
    for y, (a, b) in enumerate([(7, 8), (7, 8), (6, 9), (2, 13), (3, 12), (4, 11), (4, 11), (4, 11), (3, 12), (3, 5), (2, 4)], 2): d.hline(a, b, y, Y)
    d.hline(10, 12, 11, Y); d.hline(11, 13, 12, Y)
def fit(d):
    for x, y, dx, dy in ((2, 2, 1, 1), (13, 2, -1, 1), (2, 13, 1, -1), (13, 13, -1, -1)):
        for i in range(4): d.set(x + dx * i, y, W); d.set(x, y + dy * i, W)
    d.rect(6, 6, 9, 9, C)


MENU = {"undo": undo, "redo": redo, "mirror_h": mirror_h, "mirror_v": mirror_v, "tool_draw": tool_draw, "tool_line": tool_line, "tool_rect": tool_rect,
        "tool_outline": tool_outline, "tool_fill": tool_fill, "tool_select": tool_select, "copy": copy, "paste": paste, "cut": cut, "trash": trash,
        "turn": turn, "pick": pick, "replace": replace, "fit": fit, "find": find, "edit_program": edit_program, "favourite": favourite,
        "pause": pause, "editor": editor, "shipyard": shipyard, "fleet": fleet, "swarm": swarm, "cargo": cargo, "trade": trade, "job": job,
        "map": map_icon, "research": research, "power": power, "laws": laws, "life": life, "milestones": milestones, "settings": settings,
        "help": help_icon, "debug": debug, "diagnostics": diagnostics, "battery": battery, "hull": hull, "shield": shield, "speed": speed,
        "gravity": gravity, "heading": heading, "warn": warn, "danger": danger, "clock": clock, "target": target, "gate": gate, "crew": crew,
        "docked": docked, "landed": landed, "orbit": orbit, "weapon": weapon, "kit": kit, "message": message, "goal": goal, "mining": mining,
        "freight": freight, "survey": survey, "salvage": salvage, "star": star, "planet": planet, "moon": moon, "asteroid": asteroid,
        "gas": gas, "ok": ok, "idle": idle, "work": work, "wait": wait}

ORES = ["....AA...", "..AABBA..", ".ABBBAAA.", "AABBAAADD", "ABBAAAADD", "AAAADDAAD", ".AADDDAA.", "...AAAA.."]
KIND = {"metal": "ingot", "superconductors": "ingot", "electronics": "chip", "composites": "sheet", "water": "drop", "fuel": "can",
        "hydrogen": "can", "oxygen": "can", "methane": "can", "deuterium": "can", "food": "food", "biomass": "food", "ice": "crystal",
        "methane_ice": "crystal", "exotic_ore": "crystal", "fuel_rods": "rod", "spent_fuel": "rod", "shells": "shell", "missiles": "shell",
        "antimatter": "core"}


def material(d, mid, a):
    hi, lo = shade(a, 1.4), shade(a, .6)
    k = KIND.get(mid, "ore")
    if k == "ore":
        poly(d, ORES, 3, 4, {"A": a, "B": hi, "D": lo})
    elif k == "ingot":
        for i in range(3): d.hline(5 - i, 10 + i, 6 + i, hi)
        d.rect(2, 9, 13, 12, a); d.hline(2, 13, 12, lo)
    elif k == "chip":
        d.rect(4, 4, 11, 11, a); d.rect(6, 6, 9, 9, lo)
        for i in (5, 7, 9):
            for j in (2, 3, 12, 13): d.set(i, j, hi); d.set(j, i, hi)
    elif k == "sheet":
        for i, c in enumerate((lo, a, hi)): d.rect(2 + i, 10 - 3 * i, 11 + i, 12 - 3 * i, c)
    elif k == "drop":
        for y in range(2, 8): d.hline(8 - (y - 1) // 2, 8 + (y - 1) // 2, y, a)
        d.disc(8.5, 9.5, 4, a); d.vline(6, 9, 11, hi)
    elif k == "can":
        d.rect(4, 4, 11, 13, a); d.vline(5, 5, 12, hi); d.vline(11, 4, 13, lo); d.rect(6, 2, 9, 3, G); d.hline(4, 11, 8, lo)
        if mid in ("hydrogen", "deuterium"): d.rect(7, 6, 8, 6, OUT)
        if mid in ("oxygen", "deuterium"): d.rect(7, 11, 8, 11, OUT)
    elif k == "food":
        for y in range(3, 11): d.hline(10 - min(y - 1, 7), 12 - max(0, y - 6), y, a)
        line(d, 3, 13, 10, 5, lo)
        if mid == "food": d.disc(11.5, 11.5, 2.2, R)
    elif k == "crystal":
        for i in range(5): d.hline(8 - i, 8 + i, 2 + i, hi if i < 2 else a); d.hline(8 - i, 8 + i, 13 - i, lo)
        d.rect(4, 7, 12, 8, a); d.vline(8, 3, 12, hi)
    elif k == "rod":
        for x in (4, 7, 10): d.rect(x, 3, x + 1, 13, a); d.vline(x, 3, 13, hi)
        d.hline(3, 12, 5, G); d.hline(3, 12, 11, G)
    elif k == "shell":
        for i in range(4): d.hline(8 - min(i, 2), 8 + min(i, 2), 2 + i, hi)
        d.rect(6, 6, 10, 12, a); d.vline(10, 6, 12, lo)
        if mid == "missiles":
            for x in (4, 5, 11, 12): d.set(x, 12, a); d.set(min(max(x, 5), 11), 11, a)
            d.hline(7, 9, 13, Y)
        else:
            d.hline(6, 10, 13, G)
    elif k == "core":
        d.ring(8, 8, 4.8, 6, G); d.disc(8, 8, 3.2, a); d.disc(8, 8, 1.5, (255, 255, 255, 255))


def outlined(px):
    """A dark edge round the icon, so it reads over a bright planet."""
    out = [row[:] for row in px]
    for y in range(N):
        for x in range(N):
            if px[y][x][3]: continue
            if any(0 <= x + i < N and 0 <= y + j < N and px[y + j][x + i][3] for i, j in ((1, 0), (-1, 0), (0, 1), (0, -1))): out[y][x] = OUT
    return out


def main():
    out = os.path.join("art_source", "pixelorama", "icons")
    os.makedirs(out, exist_ok=True)
    icons = {}
    for name, draw in MENU.items():
        d = Img(); draw(d); icons[name] = outlined(d.px)
    with open("data/materials/materials.json", encoding="utf-8-sig") as f:
        for m in json.load(f)["materials"]:
            d = Img(); material(d, m["id"], hex_rgba(m["placeholderColor"])); icons["mat_" + m["id"]] = outlined(d.px)
    for name, px in icons.items():
        target = os.path.join(out, name + ".pxo")
        if os.path.isfile(target) and [[tuple(c) for c in r] for r in pxo.read_pxo(target)[2][0][1]] == px: continue  # unchanged
        pxo.write_pxo(target, N, N, [("icon", px)])
    print(f"{len(icons)} icons in {out}")
    if len(sys.argv) > 1:  # a preview contact sheet
        s, cols = 4, 13
        rows = (len(icons) + cols - 1) // cols
        img = [[(24, 30, 44, 255)] * (cols * 18 * s) for _ in range(rows * 18 * s)]
        for i, px in enumerate(icons.values()):
            for y in range(N * s):
                for x in range(N * s):
                    if px[y // s][x // s][3]: img[(i // cols) * 18 * s + y][(i % cols) * 18 * s + x] = px[y // s][x // s]
        pxo.write_png(sys.argv[1], img)


if __name__ == "__main__":
    main()
