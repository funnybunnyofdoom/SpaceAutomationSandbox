# tests

`Simulation.Tests/` is a plain .NET 8 xUnit project with no Godot runtime. It compiles the sources directly, so if that code ever references Godot, this build fails.

```powershell
dotnet test tests/Simulation.Tests
```

It links `scripts/simulation/` and `scripts/construction/`, and loads the real files in `data/`. Current coverage: deterministic sector generation, stable body and craft IDs, tick counts that don't depend on frame rate, the per-frame tick cap, on-rails orbits, radius derived from mass, part catalog and design loading and validation, design stats, and the flight model (thrust/mass, inertia, brake without overshoot, turning, input clamping, exact replay of a scripted input sequence).

Planned, from `docs/02_TECHNICAL_ARCHITECTURE.md`: mass-ledger conservation, save/load round trips, depleted deposits, and offscreen/foreground equivalence.

`tests/.gdignore` keeps the Godot editor from scanning this folder. The main `SpaceAutomationSandbox.csproj` excludes `tests/**` from compilation.
