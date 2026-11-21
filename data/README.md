# data

Versioned, data-driven definitions. Code loads these through pure C# parsers in `scripts/construction/`, and tests load the same files. Every file has a `schemaVersion`, and loaders reject versions they don't know.

Units are kilograms, metres, seconds, newtons and kilojoules.

## `parts/parts.json` (schema 1)

| Field | Required | Meaning |
| --- | --- | --- |
| `id` | yes | Stable string ID. Designs and saves refer to it, so don't rename it. |
| `name` | yes | Display name. |
| `massKg` | yes | Dry mass of one cell. Must be > 0. |
| `placeholderColor` | no | `#rrggbb` used to draw the part until sprites exist. |
| `isControlSource` | no | Can pilot the craft (cockpit). A design needs exactly one. |
| `thrustN` | no | Thrust in newtons. Phase 1: all thrust pushes along the ship's forward axis. |
| `energyCapacityKJ` | no | Battery capacity. Shown in stats only; nothing uses power yet. |
| `cargoCapacityKg` | no | Cargo capacity by mass. Shown in stats only. |
| `miningRateKgPerS` | no | Drill rate. Not used until mining (Phase 3). |

## `blueprints/*.json` ship designs (schema 1)

The files here are **built-in** designs and are read-only from the game. Designs made in the in-game editor (key B) use the same format but are saved to `user://blueprints/<id>.json` (`%APPDATA%\Godot\app_userdata\SpaceAutomationSandbox\blueprints`). `ShipDesign.ToJson()` writes the format, and the output is byte-stable.

`cells` is a list of `{ "x", "y", "part" }` on a square grid. Each cell is `ShipDesign.CellSizeM` (4 m) across. **Forward is −y**: the nose sits at the smallest y. Each cell holds one part, and parts have no rotation yet.

Validation (`ShipDesign.Validate`) reports, rather than throws: unknown part IDs, a count of control sources other than one, and zero total thrust. Cells don't yet need to be connected, because the connectivity rules are still an open question (docs/06, question 4).
