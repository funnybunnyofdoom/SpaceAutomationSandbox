# SpaceAutomationSandbox (working title)

A 2D top-down pixel-art space construction and automation sandbox: build ships and facilities from square-grid parts, fly to celestial bodies, mine them (they really lose mass), and automate fleets until your industry can dismantle planets. The name is a placeholder, not a final title.

This is a **separate project** from Agrigeddon. It shares the toolchain (Godot .NET/C#, Pixelorama, VS Code, Git) but no code, assets, settings or repository.

**Status:** Phase 1 in progress. A fixed-step simulation runs a seeded two-body sector and a player ship built from the grid design in `data/blueprints/starter_ship.json`. Controls: **W/Up** thrust, **X** reverse, **Q/E** strafe (with reverse- or side-facing thrusters), **V** cruise drive (1 km/s between planets, burns fuel), **M** system map, **A/D** turn, **S/Space** brake (matches the target's velocity), **G** anchor/release while landed, **K** dock/undock (Tab can target stations), **C** cargo (what you fly and your target's stock anywhere; moving cargo needs a dock), **J** job: mining, fixed-route freight or serve-requests freight (automates the ship you're flying), **U** deploy a construction kit, **]** / **[** time faster / slower (up to 100×, same results as 1×), **P** pause, **F2** settings (window mode and size, interface size), **F11** fullscreen, **F3** debug menu (rate multipliers, cheats), **F4** diagnostics (seed and tick, tick cost, waiting jobs by reason, mass ledger), **H** or **F1** key help, **F** fleet manager (search, filter, sort, Fly/Target), **Tab** cycle target, **T** target the nearest body, **mouse wheel** zoom, **B** blueprint editor, **Y** shipyard (order ships; refit, dismantle or pack craft docked there), **F** fleet list (pick which ship to fly), **F6** quicksave, **F9** quickload, **Esc** pause menu (save, load, settings, quit). Every key and controller input can be rebound in Settings → Controls. The game opens on a title screen (Continue, New Game, Load, Settings, Quit); New Game asks for a name and a seed (the same seed gives the same world); saves are kept in slots with timed autosaves. Sound: your ship's engines, docking, mining and warp, interface clicks and ambient music (volumes in Settings, F2). Closing the window autosaves, and Continue resumes the newest save (see `docs/08_SAVE_FORMAT.md`). Bodies are solid and have local gravity: near one, a ship left alone falls (hold brake to hover), touching it lands, and thrusting away lifts off. Craft over 15 parts need crew (1 per 10 parts) living in habitats, fed with food (from farms) and water (from ice), or robots (robot bays, power only); move crew between docked craft in the cargo panel. Mk2 parts double a basic part's output for more electronics and composites. Crewed stations automatically ask for a day's food and water, and crewed ships top up when docked; recruit crew for food and water in the cargo panel. Past their ore, bodies can be mined as rock (or ice) until they're gone. Build a station with a warp gate part: its first jump (N while docked) opens a new star system and builds the twin gate there; jobs route through gates on their own. **R** research (labs make points; spend them to unlock Mk2/Mk3 parts, robot bays and warp gates). In the map, **G** shows the galaxy. Ships docked to a station act as tugs: fly the station (fleet list) and they push it the way they face. Other systems have red, yellow or blue stars, and some hold exotic ore for Mk3 parts. Landing or bumping faster than 6 m/s damages parts (they work worse, and break off at 0); shipyards repair craft docked at them, and automated jobs go for repairs on their own. A survey job scouts unsurveyed bodies automatically. Home orbits a star with three other planets (rocky Cinder, the Giant with two moons, icy Rime) and an asteroid belt. Around Home, three asteroids hold iron ore, silicate and copper ore, the Moon holds iron ore and silicate, and Ice-1 holds ice (refined into water and fuel); fly a ship within 2 km of one to survey it. Mining jobs deliver ore, freight jobs move goods between stations, and station refineries turn ore into metal, composites and electronics (plus slag, which hull plates and foundations use and the yard's slag ejector throws out above a 20 t reserve), so every ship material can be produced. Station designs are built as construction kits: load one onto a ship in the cargo panel, carry it, and deploy it (U) on a surface or in orbit. Stations can post requests (cargo panel) that serve-requests freighters fill automatically. Solar panels power thrusters, drills, refineries and fabricators; batteries store spare power, and docked ships recharge from their station (the HUD shows NO POWER when something can't run). See `docs/04_PHASES_AND_ACCEPTANCE.md`.

## Toolchain (found on this PC during Phase 0)

| Tool | Version / location |
| --- | --- |
| Godot .NET editor | 4.7.2-stable mono, `C:\dev\Tools\Godot\Godot_v4.7.2-stable_mono_win64.exe` |
| C# SDK used by project | `Godot.NET.Sdk/4.7.2`, target `net8.0` |
| Pixelorama | `C:\dev\Tools\Pixelorama\Pixelorama.exe` |
| Renderer | Compatibility (`gl_compatibility`), nearest-neighbor texture filtering for pixel art |

## First-time setup

1. Optional but recommended: set a user environment variable so VS Code tasks can find Godot without a hard-coded path:
   ```powershell
   [Environment]::SetEnvironmentVariable('GODOT4', 'C:\dev\Tools\Godot\Godot_v4.7.2-stable_mono_win64.exe', 'User')
   ```
   Restart VS Code/terminals afterwards. Optionally do the same for `PIXELORAMA`.
2. From this folder, run the bootstrap check (build, headless smoke test, local `git init`):
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\tools\setup\Phase0-Setup.ps1
   ```
   Add `-Commit` to also make the local baseline commit. No remote is created or pushed.
3. Open the project in the Godot editor (Project Manager > Import > this folder's `project.godot`) and press **F5**. You should see a dark starfield with a "Phase 0 bootstrap OK" label.

Controls are Godot input actions (see `scripts/core/InputActions.cs`): rebind them by defining the same action names in Project Settings → Input Map. A gamepad works too (right trigger thrust, left stick turn, left trigger brake).

## Export

`export_presets.cfg` has a Windows Desktop preset that packs the `data/*.json` files. Export from the Godot editor (Project → Export) or with `godot --headless --path . --export-release "Windows Desktop" builds/windows/SpaceAutomationSandbox.exe`. The `builds/` folder is git-ignored. This needs Godot's export templates (4.7.2 mono).

## Benchmarks

`dotnet run -c Release --project tools/Benchmarks` times the simulation with 10 to 10,000 automated ships. Results and how to read them are in `docs/09_BENCHMARKS.md`.

## Daily loop

```powershell
dotnet build SpaceAutomationSandbox.csproj   # or Ctrl+Shift+B in VS Code (the root also holds a .sln, so name the project)
dotnet test tests/Simulation.Tests   # pure C# simulation tests (VS Code task: test)
# Godot: F5 to test
git status --short    # review, then commit/push only when you decide to
```

VS Code: open **this folder only**. Tasks: `build`, `test`, `godot: open editor`, `godot: run main scene`. Launch config "Play main scene (debug C#)" attaches the C# debugger. Recommended extensions are listed in `.vscode/extensions.json`.

## Layout

```text
scenes/        main, ui, visuals (views only; never the source of truth)
scripts/       core, simulation, construction, logistics, physics, save
data/          parts, materials, recipes, blueprints (data-driven definitions)
art_source/pixelorama/   editable .pxo sources
assets/art/              exported .png used by the game (+ assets/ASSET_MANIFEST.md)
tools/         setup, art and VS Code template (tools/vscode -> .vscode) scripts
tests/         pure C# xUnit tests for scripts/simulation (no Godot runtime)
docs/          vision, architecture, setup, phases, decision log
```

## Art pipeline

Draw in Pixelorama, save the `.pxo` under `art_source/pixelorama/<group>/`, export the PNG with the same name to `assets/art/<group>/`, and record it in `assets/ASSET_MANIFEST.md`. `tools/art/Check-ArtPairs.ps1` reports missing or stale exports.

## Rules for agents

See `AGENTS.md` and `docs/06_DECISION_LOG_AND_OPEN_QUESTIONS.md`. Proposals in the docs aren't requirements until they're logged as decisions.
