# data

Versioned, data-driven definitions. Code loads these through pure C# parsers in `scripts/construction/`, and tests load the same files. Every file has a `schemaVersion`, and loaders reject versions they don't know.

Units are kilograms, metres, seconds, newtons and kilojoules.

## `materials/materials.json` (schema 1)

| Field | Required | Meaning |
| --- | --- | --- |
| `id` | yes | Stable string ID used in part costs, and later in inventories. Don't rename it. |
| `name` | yes | Display name. |
| `placeholderColor` | no | `#rrggbb` for UI until icons exist. |

File order is display order. The three current materials (metal, electronics, composites) are **placeholders** until the resource taxonomy is designed (docs/06, question 5).

## `parts/parts.json` (schema 2)

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
| `isAnchorClamp` | no | Lets a landed craft anchor to the body it's on (G) and release again. |
| `isFoundation` | no | Makes the design a fixed structure: it anchors permanently when it lands. Not allowed in ship designs. |
| `isDockingPort` | no | One berth where a ship can dock. It opens in the direction the part faces (its rotation). |
| `fabricationRateKgPerS` | no | Shipyard build rate: kg of finished craft assembled per second. A design with any of these is a valid station. |
| `materialsKg` | yes (v2) | What one part is built from, as `{ "materialId": kg }`. Every material must exist and be > 0, and the amounts **must add up to `massKg`** (checked on load), so building never creates or destroys mass. |

## `blueprints/*.json` designs (schema 2)

The files here are **built-in** designs and are read-only from the game. `starter_ship.json` is the player's first ship. `starter_shipyard.json` is the first station (`DesignRole.Station`: needs a fabricator). Costs must be whole kg, because inventories are whole kg. Designs made in the in-game editor (key B) use the same format but are saved to `user://blueprints/<id>.json` (`%APPDATA%\Godot\app_userdata\SpaceAutomationSandbox\blueprints`). `ShipDesign.ToJson()` writes the format, and the output is byte-stable.

`cells` is a list of `{ "x", "y", "part" }` on a square grid. Each cell is `ShipDesign.CellSizeM` (4 m) across. **Forward is −y**: the nose sits at the smallest y. Each cell holds one part. Optional `"rot"`: 0–3 quarter turns clockwise (schema 2; omitted when 0). At 0 a part faces forward: a thruster pushes forward and a docking port opens forward. Only rear-facing (rot 0) thrusters give forward thrust for now. `surface_depot.json` is a built-in fixed structure (foundations).

Validation (`ShipDesign.Validate`) reports, rather than throws: unknown part IDs, a count of control sources other than one, and zero total thrust. Cells don't yet need to be connected, because the connectivity rules are still an open question (docs/06, question 4).
