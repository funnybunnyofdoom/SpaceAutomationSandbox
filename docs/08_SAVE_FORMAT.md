# 08 — Save format (schema 2)

The code is `scripts/save/SaveGameSerializer.cs` (pure C#), and the tests are in `tests/Simulation.Tests/SaveLoadTests.cs`.

## Where and when

- There's one quicksave slot: `user://savegame.json`. On Windows that's `%APPDATA%\Godot\app_userdata\SpaceAutomationSandbox\savegame.json`.
- **F6** saves, **F9** loads and **F7** starts a new game. A new game doesn't touch the file until you save or quit.
- Closing the game window autosaves. Stopping the game from the Godot editor kills the process without that notification, so press F6 first if you need that state.
- At startup the game loads the save if one exists. If the save can't be read, it's renamed to `savegame.json.bad` and a new game starts, so a broken save is never silently overwritten.
- Saves are written to `savegame.json.tmp` and then renamed over the old file, so a crash mid-write keeps the previous save.

## Principles

- **Full state, not "seed plus changes".** Bodies are stored completely, including mass and orbit, so retuning `SectorGenerator` never changes an existing save. The seed is kept for reference and future generation.
- **References use stable IDs**: body, craft and part IDs. Scene paths and list positions are never used.
- **Ship designs are embedded** in each craft, so a save survives edits to `data/blueprints`. Part *stats* come from the current `data/parts/parts.json`, so rebalancing a part applies to existing saves. If a saved design references a part that no longer exists, the load fails with a message naming it.
- **Deterministic:** the same state always produces byte-identical JSON. Doubles are written in round-trip form. Saving at any tick, loading and continuing gives exactly the state of a run that never stopped (tested).
- Not saved: the render interpolation fraction, camera and zoom. These are view state.

## Shape

```jsonc
{
  "schemaVersion": 2,
  "game": "SpaceAutomationSandbox",
  "playerCraftId": 1,                 // 0 = no player craft
  "sector": {
    "seed": 20261120,
    "tick": 1234,                     // fixed ticks since sector start (20/s)
    "bodies": [
      { "id": 1, "name": "Primary", "kind": "Planet", "massKg": 5.0e15, "densityKgPerM3": 5500,
        "isMineable": false, "orbit": null },
      { "id": 2, "name": "Rock-1", "kind": "Asteroid", "massKg": 3.1e12, "densityKgPerM3": 2500,
        "isMineable": true,
        "orbit": { "parentId": 1, "radiusM": 26000, "periodS": 1800, "phaseAtZeroRad": 1.2 } }
    ],
    "crafts": [
      { "id": 1, "name": "Player",
        "design": { "id": "starter_ship", "name": "Starter prospector",
                    "cells": [ { "x": 0, "y": -3, "part": "drill" } /* ... */ ] },
        "position": { "x": 2381.0, "y": -6188.0 },   // centre of mass, sector metres
        "velocity": { "x": 60.1, "y": 46.3 },        // m/s
        "headingRad": 2.63,
        "controls": { "throttle": 0, "turn": 0, "brake": false },
        "targetBodyId": 2,                           // 0 = no target
        "landedOnBodyId": 0,                         // v2: 0 = flying
        "landedOffset": null }                       // v2: craft centre minus body centre while landed
    ]
  }
}
```

The load checks for: the right `game`, a `schemaVersion` from 1 to 2, valid JSON, a non-negative tick, unique IDs, orbit parents that exist and aren't circular (bodies may appear in any order), valid designs, and target, landed-on and player IDs that exist. Any failure raises `FormatException` with a reason.

## Changing the format

Bump `SaveGameSerializer.SchemaVersion` and update this document. Then either add a migration from the old version, or decide explicitly, and record in docs/06, that old saves are dropped. Until then, loading a different version is refused with a message.

## Version history

| Version | Change | Reading older files |
| --- | --- | --- |
| 1 | First format | — |
| 2 | Added `landedOnBodyId` and `landedOffset` per craft (solid bodies) | A v1 file loads with every craft flying. The next save writes v2. |
