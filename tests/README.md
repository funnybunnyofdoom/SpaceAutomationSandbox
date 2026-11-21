# tests

`Simulation.Tests/` is a plain .NET 8 xUnit project with no Godot runtime. It compiles the sources directly, so if that code ever references Godot, this build fails.

```powershell
dotnet test tests/Simulation.Tests
```

It links `scripts/simulation/` and `scripts/construction/`, and loads the real files in `data/`. Current coverage: deterministic sector generation, stable body and craft IDs, tick counts that don't depend on frame rate, the per-frame tick cap, on-rails orbits, radius derived from mass, part catalog and design loading and validation, design stats, and the flight model (thrust/mass, inertia, brake without overshoot, turning, input clamping, exact replay of a scripted input sequence), and rendezvous: orbit velocity, a target-relative brake that holds position on a curving orbit, closing-speed sign, and an asteroid slow enough to catch for several seeds. Save/load: full-state round trip, byte-identical save→load→save, saving and loading at several ticks then continuing exactly like a run that never stopped, saved bodies taking priority over the generator, stable IDs, and clear rejection of foreign, future-version or inconsistent files. Collision: the bounding radius, landing, riding along on the moving asteroid, lift-off rules, fast impacts, never ending a tick inside a body over 40,000 random ticks, saving while landed, and loading v1 saves. Blueprint editor: live stats, errors and warnings, bounds, undo per action and per drag, dirty tracking, design JSON round trip, ids from names, and designs with unknown parts.

Planned, from `docs/02_TECHNICAL_ARCHITECTURE.md`: mass-ledger conservation, save/load round trips, depleted deposits, and offscreen/foreground equivalence.

`tests/.gdignore` keeps the Godot editor from scanning this folder. The main `SpaceAutomationSandbox.csproj` excludes `tests/**` from compilation.
