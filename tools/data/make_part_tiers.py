"""Adds Mk II to Mk IV of the workhorse parts (#133, #134) to data/parts/parts.json and their research to data/research/research.json.

Run from the repository root: python tools/data/make_part_tiers.py
Tiers already in the file are left as they are (their numbers were tuned by hand). A new tier's main numbers are Mk I's times 2, 3 or 5
(Mk IV at least 1.5 times Mk III), its power use 1.5, 1.9 or 2.6 times (Mk IV at least 1.3 times Mk III) and its mass 1.2, 1.35 or 1.6 times. Its bill keeps Mk I's mix, with
superconductors from Mk III and exotic ore in Mk IV. Mk II comes with the part's usual tech, Mk III with Superconductors, Mk IV with Exotic engineering.
New parts are inserted one a line after the tier before them; the rest of the files is left as it is.
"""
import json
import os
import re

PARTS = os.path.join("data", "parts", "parts.json")
RESEARCH = os.path.join("data", "research", "research.json")
NL = "\n"

BASES = ["thruster", "drill", "solar_panel", "battery", "cargo", "storage_silo", "refinery", "fabricator", "habitat", "robot_bay", "farm",
         "research_lab", "salvage_arm", "repair_drones", "armour_plate", "shield_generator", "autocannon", "laser"]
MK2_TECH = {"cargo": "advanced_fabrication", "storage_silo": "advanced_fabrication", "research_lab": "advanced_fabrication",
            "habitat": "automation", "farm": "automation", "robot_bay": "automation", "salvage_arm": "automation", "repair_drones": "automation",
            "armour_plate": "defenses", "autocannon": "defenses", "laser": "defenses", "shield_generator": "shields"}
SCALED = ["thrustN", "miningRateKgPerS", "powerGenerationKW", "energyCapacityKJ", "cargoCapacityKg", "refiningRateKgPerS", "fabricationRateKgPerS",
          "crewCapacity", "robotCount", "foodRateKgPerS", "researchRatePerS", "salvageRateKgPerS", "repairRateKgPerS", "shieldCapacity", "shieldRechargePerS"]
WHOLE = {"crewCapacity", "robotCount", "cargoCapacityKg", "energyCapacityKJ", "thrustN"}
FACTOR = {2: 2, 3: 3, 4: 5}
POWER = {2: 1.5, 3: 1.9, 4: 2.6}
MASS = {2: 1.2, 3: 1.35, 4: 1.6}
NAMES = {2: "Mk2", 3: "Mk3", 4: "Mk4"}
EXOTIC = {"id": "exotic_engineering", "name": "Exotic engineering", "cost": 600, "requires": ["superconductors", "heavy_fabrication"], "parts": [],
          "description": "Mk4 parts: five times Mk1, built with superconductors and exotic ore."}


def tier_id(base, tier):
    return base if tier == 1 else f"{base}_mk{tier}"


def lighter(hex_colour, tier):
    h = hex_colour.lstrip("#")
    rgb = [int(h[i:i + 2], 16) for i in (0, 2, 4)]
    t = 0.08 * (tier - 1)
    return "#" + "".join(f"{round(c + (255 - c) * t):02x}" for c in rgb)


def scale(value, key, factor):
    v = value * factor
    return int(round(v)) if key in WHOLE else round(v, 4)


def make(base_part, tier, mk3):
    p = {"id": tier_id(base_part["id"], tier), "name": f"{base_part['name']} {NAMES[tier]}"}
    mass = round(base_part["massKg"] * MASS[tier] / 100) * 100
    p["massKg"] = mass
    p["placeholderColor"] = lighter(base_part.get("placeholderColor", "#888888"), tier)
    for key, value in base_part.items():
        if key in ("id", "name", "massKg", "placeholderColor", "materialsKg"): continue
        if key in SCALED:
            v = scale(value, key, FACTOR[tier])
            if tier == 4 and mk3 is not None and key in mk3: v = max(v, scale(mk3[key], key, 1.5))
            p[key] = v
        elif key == "powerUseKW":
            p[key] = round(value * POWER[tier])
            # Never less than the tier below (#143: a hand-tuned Mk3 can use more than Mk1's scaling gives Mk4).
            if tier == 4 and mk3 is not None and mk3.get(key): p[key] = max(p[key], round(mk3[key] * 1.3))
        elif key == "toughness" and base_part["id"] == "armour_plate":
            p[key] = round(value * FACTOR[tier])
        elif key == "weapon":
            w = dict(value)
            w["damagePerShot"] = round(w["damagePerShot"] * FACTOR[tier], 4)
            w["energyKJPerShot"] = round(w["energyKJPerShot"] * POWER[tier], 2)
            p[key] = w
        else:
            p[key] = value
    extra = {}
    if tier >= 3: extra["superconductors"] = round(mass * (0.05 if tier == 3 else 0.08))
    if tier == 4: extra["exotic_ore"] = round(mass * 0.04)
    rest = mass - sum(extra.values())
    base_bill = base_part["materialsKg"]
    total = sum(base_bill.values())
    bill = {m: round(kg / total * rest) for m, kg in base_bill.items()}
    first = next(iter(bill))
    bill[first] += rest - sum(bill.values())  # exact
    for m, kg in extra.items(): bill[m] = bill.get(m, 0) + kg
    p["materialsKg"] = bill
    return p


def as_line(part):
    return "    " + json.dumps(part, ensure_ascii=False)


def write_parts(path, parts, added):
    """Inserts each new part as one line after the part before it, leaving the rest of the file as it is."""
    text = open(path, encoding="utf-8").read()
    lines = text.split(NL)
    order = [p["id"] for p in parts]
    for pid in added:
        before = order[order.index(pid) - 1]
        start = next(i for i, l in enumerate(lines) if f'"id": "{before}"' in l)
        end = start
        depth = lines[start].count("{") - lines[start].count("}")
        while depth > 0:
            end += 1
            depth += lines[end].count("{") - lines[end].count("}")
        last = not lines[end].rstrip().endswith(",")
        if last: lines[end] = lines[end].rstrip() + ","
        part = next(p for p in parts if p["id"] == pid)
        lines.insert(end + 1, as_line(part) + ("" if last else ","))
    open(path, "w", encoding="utf-8", newline="").write(NL.join(lines))


def write_research(path, research):
    text = open(path, encoding="utf-8").read()
    if '"id": "exotic_engineering"' not in text:
        anchor = '    { "id": "fusion_power"'
        entry = ('    { "id": "exotic_engineering", "name": "Exotic engineering", "cost": 600, "requires": ["superconductors", "heavy_fabrication"], "parts": [],' + NL
                 + '      "description": "' + EXOTIC["description"] + '" },' + NL)
        text = text.replace(anchor, entry + anchor, 1)
    for tech in research["techs"]:
        m = re.search('("id": "' + re.escape(tech["id"]) + '".*?"parts": )\\[[^\\]]*\\]', text, re.S)
        if m: text = text[:m.start()] + m.group(1) + json.dumps(tech["parts"]) + text[m.end():]
    open(path, "w", encoding="utf-8", newline="").write(text)


def main():
    parts_file = json.load(open(PARTS, encoding="utf-8"))
    parts = parts_file["parts"]
    by_id = {p["id"]: p for p in parts}
    research = json.load(open(RESEARCH, encoding="utf-8"))
    techs = {t["id"]: t for t in research["techs"]}
    if "exotic_engineering" not in techs:
        research["techs"].append(dict(EXOTIC))
        techs["exotic_engineering"] = research["techs"][-1]
    added = []
    for base in BASES:
        for tier in (2, 3, 4):
            pid = tier_id(base, tier)
            if pid in by_id: continue
            part = make(by_id[base], tier, by_id.get(tier_id(base, 3)))
            after = max(parts.index(by_id[t]) for t in [tier_id(base, k) for k in range(1, tier)] if t in by_id)
            parts.insert(after + 1, part)
            by_id[pid] = part
            tech = MK2_TECH.get(base, "advanced_fabrication") if tier == 2 else "superconductors" if tier == 3 else "exotic_engineering"
            if pid not in techs[tech]["parts"]: techs[tech]["parts"].append(pid)
            added.append(pid)
    write_parts(PARTS, parts, added)
    write_research(RESEARCH, research)
    print(f"added {len(added)} parts")


if __name__ == "__main__":
    main()
