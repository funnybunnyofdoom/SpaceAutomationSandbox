# 04 — Initial Milestones and Acceptance Gates

This is a **bootstrap and prototype plan**, not a complete long-term production roadmap. The long-term roadmap can be drafted after another design pass, as originally intended.

## Phase 0 — Isolated project bootstrap (the Work agent's FIRST task)

**Goal:** a clean Godot .NET/C# project beside Agrigeddon with the same convenient local workflow and no accidental cross-project changes.

Work:

1. Inspect installed Windows Godot .NET editor, .NET SDK, Pixelorama, VS Code, Git, and the actual local Games workspace.
2. Confirm new project destination is absent/empty; keep it outside all existing repos. Ask only when a conflicting destination or a destructive operation requires a choice.
3. Generate new Godot project, C# solution, main F5-testable scene, minimal README, `AGENTS.md`, docs, and folder conventions.
4. Initialize separate **local** Git repo, with generated file ignores; no unapproved remote creation or push.
5. Verify `dotnet build` and Godot F5. Test a sample editable Pixelorama asset import or document which part is blocked and why.
6. Capture versions, local location, files made, and test outcomes; stop before gameplay development.

**Phase 0 acceptance:** F5 opens the new blank/test scene, C# compiles, Git is isolated, no Agrigeddon changes, and all setup instructions reproduce the result.

## Phase 1 — Minimum physical world + player ship

**Goal:** the player can move a basic modular ship around a small space map and approach a celestial body.

- Seeded 2D sector: one central gravitational body and one mineable smaller object; basic positions/orbits as agreed in the chosen physics approach.
- Ship represented as a 2D grid of parts, player steering/input and camera follow.
- Minimal modules: hull, thruster, battery/power source, cockpit, cargo, drill.
- Persist world seed, player ship, body resources.
- Add simple HUD: speed/location, cargo, current part power, target body.

**Acceptance:** pilot, stop, return to a resource source, identify the object, quit/reload without losing ship/body identity. Movement must be independent of render FPS.

## Phase 2 — Blueprint construction and deployment

**Goal:** edit, validate, save and fabricate a usable modular craft.

- Grid-based blueprint editor: place/remove/rotate module (if directional), inspect component requirements, calculate mass, power balance, storage, flight/mining capabilities.
- Blueprint save/load with stable schema and material bill of materials.
- Fabrication: one simple shipyard or construction node turns correct materials into a finished craft; progress queue and insufficient-material errors.
- Landing/anchoring state proof: player or prototype extractor can attach to the mineable body using suitable anchor components.

**Acceptance:** a saved miner blueprint can be loaded and manufactured again; missing materials prevent fabrication; changing layout updates stats; deployment/undocking cannot duplicate modules or resources.

## Phase 3 — Complete autonomous miner loop (**first fun gate**)

**Goal:** one crafted NPC vessel mines and delivers resources without human input.

- Resource ledger: mineral mass on celestial body, inventories of miner and depot.
- Job sequence: select source -> navigate -> mine -> navigate home -> unload -> repeat.
- A behavior configuration UI with a target material and destination, plus state/errors.
- Source depletion reduces physical body deposit; body mass/radius/gravity derived with chosen model.
- Save/load while miner is traveling, mining, and unloading.
- Test failure states: empty source, full destination, insufficient fuel/power, target missing.

**Acceptance:** 10 complete autonomous cycles without duplication/loss, clear status messages for blocked tasks, and mass invariants pass tests. Background operation must continue when the miner's visual scene is not instantiated.

## Phase 4 — Production networks and remote sites

- More material types, refining recipes, deposits, buffers, factories and storage.
- Distinguish player-first-visit site founding from NPC expansion.
- Multiple miners, freight jobs and industrial chains, service/maintenance.
- Supply/demand management and retry/error diagnostics; small performance test with dozens of ships.

**Gate:** demonstrate a small autonomous network replacing the player manually hauling goods.

## Phase 5 — Workforce and scale

- Food/water/oxygen/habitat systems and recruitable organics; robotics with power/maintenance infrastructure.
- Mixed workforce trade-offs, supply routing, module upgrades, and more body types.
- Sector streaming + cheaper distant simulation, stress tests, time-acceleration correctness, diagnostic tools.

**Gate:** dozens to hundreds of managed ships and jobs run without runaway scene count or invalid resource balances. No arbitrary performance promise: benchmark and report.

## Phase 6 — Multi-system logistics / warp

- Discover distant areas; gates, accelerators and/or warp corridors with infrastructure costs and route rules.
- Cross-sector freight, logistics requests, relay stations, congestion/capacity, recovery after network outages.
- Procedural world expansion and save migrations.

**Gate:** resources can travel between two distant sectors via player-built infrastructure with preserved inventory accounting.

## Phase 7 — Megastructures, mobile planets and swarms

- Complete body dismantling, changing body appearance/gravitation, industrial-scale excavation.
- Movable enormous factories/structures and construction/transport challenges.
- Swarm grouping/aggregation, macro-scale build orders and massive resource flows.
- Late-game energy and stability constraints, balance and simulation benchmarks.

**Gate:** two distinctive viable strategies can be demonstrated: a massive mobile facility and an aggregated swarm achieving similar industrial objectives by different means.

## Phase 0 first-session completion report template

```text
NEW GAME: [working title / folder]
Existing Agrigeddon project path (read-only): [confirmed or not found]
New project path: [exact path]
Godot .NET edition/version: [actual]
.NET SDK: [actual]
Pixelorama executable/version: [actual or unresolved]
VS Code: [actual or unresolved]
Git version: [actual]
New repository initialized: [yes/no, local path]
GitHub remote created/pushed: [must be no until authorized]

Created files:
- ...

Checks:
- dotnet build: PASS / FAIL [details]
- Godot F5: PASS / FAIL [details]
- Pixelorama export/import: PASS / FAIL / NOT TESTED [details]
- Agrigeddon untouched: VERIFIED / NOT VERIFIED [method]

Known blockers and proposed next action:
- ...
```
