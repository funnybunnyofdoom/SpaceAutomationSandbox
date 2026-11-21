# tests

Empty in Phase 0 by design.

Planned (from `docs/02_TECHNICAL_ARCHITECTURE.md`): a plain C# test project (no Godot runtime needed) covering mass-ledger conservation, deterministic tick progression, save/load round trips, depleted deposits, and offscreen/foreground equivalence. It should be added together with the first simulation code in `scripts/simulation/`, so that the core rules stay testable without running a scene.
