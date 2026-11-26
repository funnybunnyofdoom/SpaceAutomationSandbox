# 11 — Adding a race

A race is a folder in `data/races/`. Add one and it's in the New Game screen; no code changes. A race that can't be used is left out, and the New Game screen says why.

| File | Required | What it is |
| --- | --- | --- |
| `race.json` | yes | The race (below) |
| `portrait.webp` or `portrait.png` | no | 4:5, about 1120 × 1400. Without one it gets a plain picture in its colour. After adding it, open the project in Godot once so it's imported |
| `ship.json` | no | Its own starting ship: a design file, as the blueprint editor saves them |
| `shipyard.json` | no | Its own starting shipyard |
| `patrol.json`, `raider.json`, `trade_station.json` | no | Its civilizations' own patrol ship, raider and trade station (#125). Ships need 1 to 4 weapons; a station a docking port; at most 30 parts. Larger raids and every third patrol ship carry missiles: its guns become missile launchers |

## `race.json` (schema 2)

| Field | Required | Meaning |
| --- | --- | --- |
| `schemaVersion` | yes | 2 |
| `id` | yes | The folder's name. Saves refer to it: don't rename it |
| `name`, `description` | `name` | Shown on the New Game screen |
| `order` | no | Place in the list (Human is always first) |
| `accent` | no | Interface colour, `#rrggbb` |
| `hullTint` | no | A light tint for its ships' parts, `#rrggbb` (#130) |
| `crew`, `supplies` | no | People at the starting shipyard; whether it has food and water |
| `traits` | no | Percent, from -50 to 50, by trait (below) |
| `tech` | no | Tech it starts with (ids from `data/research`) |
| `stockKg` | no | More, or with a minus less, of a material at the starting shipyard |
| `shipyardParts` | no | Parts of the starting shipyard to swap, old id to new |
| `blockedLaws` | no | Laws it can't pass, with the reason |
| `leanings` | no | By law: points on the attitude of civilizations that want it (`wants`) or are against it (`opposes`) |
| `civilization` | no | `true`: civilizations you meet can be of this race (looks, words and attitude only) |
| `cultures` | no | The cultures such a civilization may have (`territorial`, `conservationist`, `purist`, `pacifist`, `militarist`); left out: any |
| `views` | no | By race id: points (-50 to 50) on how such a civilization sees a player of that race. Its own race it sees 10 warmer unless this says otherwise |
| `civilizationNames` | no | What such civilizations may be called |
| `civTraits` | no | How its civilizations behave (#125): percent, from -50 to 50, by civilization trait (below) |
| `favours` | no | Up to 4 goods its civilizations pay 50% more for (and don't sell) |
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

| Civilization trait | Plus means |
| --- | --- |
| `patrolShips` | more patrol ships (3 usually; 1 to 5) |
| `patrolRebuild` | lost patrol ships replaced sooner |
| `raidSize`, `raidRate` | bigger raids; raids more often when hostile |
| `tradeMargin` | dearer in trade |
| `tradeStock` | more stock of what it sells |

Civilization traits, favoured goods and designs apply to civilizations met since #125; those in older games play as before.

Traits apply to the player's own craft only. They're read from these files each time, so changing a race changes games already played with it.

## The player's own races (#117)

The race creator, on the New Game screen, makes these without files. They're kept in `user://races/<id>/` in the same format, with ids that start with `custom_`, and `picture` (a built-in race's id, `plain`, or empty for `portrait.webp` beside it). Beyond the rules above, the shipyard must work from the start: 2 to 6 crew with food and water, or a robot bay and no crew. It can start with a ship and a shipyard of the player's own designs (`shipDesign`, `shipyardDesign`: design files as text); the shipyard needs a fabricator, a docking port, room for its crew and enough crew or robots to work. They cost points by what their materials are worth beyond the usual ones', about 4 points per 10 t of metal's worth; a smaller design gives some back. Civilization traits and favours cost nothing: they're about others. A race can be exported to one file with its picture (`kind: race`, version 1) and imported again. A game keeps its own copy of such a race, so changing or deleting it changes no game.

Points: a race has 8. 5% of mining, building, refining, research or machines' power costs 1 for the better; crew supplies and growth 0.5; recruiting 0.25. Also: a crew member over 4, 1; robots instead of the habitat, 6; tech, its cost / 40; 10 points of civilizations' views, 1; stock by the tonne (electronics 0.75, composites 0.6, fuel 0.3, metal 0.2, slag 0.05). For the worse gives them back; no food and water gives 2, a law it can't pass 1. Over 8 it's only marked unbalanced.
