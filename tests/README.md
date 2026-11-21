# tests

`Simulation.Tests/` is a plain .NET 8 xUnit project with no Godot runtime. It links the sources in `scripts/simulation/` directly, so if that code ever references Godot, this build fails.

```powershell
dotnet test tests/Simulation.Tests
```

Current coverage: deterministic sector generation, stable body IDs, tick counts that don't depend on frame rate, the per-frame tick cap, on-rails orbits and radius derived from mass.

Planned, from `docs/02_TECHNICAL_ARCHITECTURE.md`: mass-ledger conservation, save/load round trips, depleted deposits, and offscreen/foreground equivalence.

`tests/.gdignore` keeps the Godot editor from scanning this folder. The main `SpaceAutomationSandbox.csproj` excludes `tests/**` from compilation.
