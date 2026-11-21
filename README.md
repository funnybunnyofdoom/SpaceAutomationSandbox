# SpaceAutomationSandbox (working title)

A 2D top-down pixel-art space construction and automation sandbox: build ships and facilities from square-grid parts, fly to celestial bodies, mine them (they really lose mass), and automate fleets until your industry can dismantle planets. The name is a placeholder, not a final title.

This is a **separate project** from Agrigeddon. It shares the toolchain (Godot .NET/C#, Pixelorama, VS Code, Git) but no code, assets, settings or repository.

**Status:** Phase 1 in progress. A fixed-step simulation runs a seeded two-body sector and a player ship built from the grid design in `data/blueprints/starter_ship.json`. Controls: **W/Up** thrust, **A/D** turn, **S/Space** brake (matches the target's velocity), **G** anchor/release while landed, **K** dock/undock (Tab can target stations), **C** cargo while docked, **J** mining job (automates the ship you're flying), **Tab** cycle target, **T** target the nearest body, **mouse wheel** zoom, **B** blueprint editor, **Y** shipyard (order ships), **F** fleet list (pick which ship to fly), **F6** save, **F9** load, **F7** new game. Closing the window autosaves, and the game resumes the save at startup (see `docs/08_SAVE_FORMAT.md`). Bodies are solid: touching one lands the ship, and thrusting away lifts it off. Ships anchored on Rock-1 with a drill mine its metal, and a mining job runs the whole mine → deliver loop by itself. See `docs/04_PHASES_AND_ACCEPTANCE.md`.

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

## Daily loop

```powershell
dotnet build          # or Ctrl+Shift+B in VS Code
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
