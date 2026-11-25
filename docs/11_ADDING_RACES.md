# 11 — Adding a race

A race is a folder in `data/races/`. Add one and it's in the New Game screen; no code changes. A race that can't be used is left out, and the New Game screen says why.

| File | Required | What it is |
| --- | --- | --- |
| `race.json` | yes | The race (below) |
| `portrait.webp` or `portrait.png` | no | 4:5, about 1120 × 1400. Without one it gets a plain picture in its colour. After adding it, open the project in Godot once so it's imported |
| `ship.json` | no | Its own starting ship: a design file, as the blueprint editor saves them |
| `shipyard.json` | no | Its own starting shipyard |

## `race.json` (schema 2)

| Field | Required | Meaning |
| --- | --- | --- |
| `schemaVersion` | yes | 2 |
| `id` | yes | The folder's name. Saves refer to it: don't rename it |
| `name`, `description` | `name` | Shown on the New Game screen |
| `order` | no | Place in the list (Human is always first) |
| `accent` | no | Interface colour, `#rrggbb` |
| `crew`, `supplies` | no | People at the starting shipyard; whether it has food and water |
| `traits` | no | Percent, from -50 to 50, by trait (below) |
| `tech` | no | Tech it starts with (ids from `data/research`) |
| `stockKg` | no | More, or with a minus less, of a material at the starting shipyard |
| `shipyardParts` | no | Parts of the starting shipyard to swap, old id to new |
| `blockedLaws` | no | Laws it can't pass, with the reason |
| `leanings` | no | By law: points on the attitude of civilizations that want it (`wants`) or are against it (`opposes`) |
| `shipName`, `shipyardName` | no | What the first ship and the shipyard are called |
| `goalHints` | no | Goal list hints that differ, by goal id |
| `notes` | no | Extra lines for the New Game screen. What the data means is listed by itself |

| Trait | Plus means |
| --- | --- |
| `mining`, `building`, `refining`, `research` | faster (`building` is also refits, dismantling and shipyard repairs) |
| `power` | machines use more: drills, refineries, fabricators, labs, habitats, farms, robots, ejectors, salvage arms and repair drones; not engines, weapons, shields or gates |
| `crewSupplies` | crew eat and drink more |
| `crewGrowth` | crew grow faster in habitats |
| `recruitCost` | recruiting costs more |

Traits apply to the player's own craft only. They're read from these files each time, so changing a race changes games already played with it.
