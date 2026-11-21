# 02 — Proposed Technical Architecture (Godot 4 .NET / C#)

**Classification:** Engineering recommendation, not a settled feature specification. The engine version, runtime paths and rendering backend must be inspected on the actual PC. Do not change Agrigeddon's installation.

## Primary goal

Make the **same game rules** function for a visible, manually piloted ship and for thousands of offscreen or automated constructs. A scene-tree of fully simulated `RigidBody2D` ships in every star system will not scale to the desired late game.

## Logical layers

```text
UI / Input / Player Controls / Blueprint Editor / Maps
                         |
        Application Commands + Queries
                         |
  Fixed-step Authoritative Simulation (pure C# where practical)
  + World state: sectors, bodies, constructs, inventories, jobs
  + Construction: blueprints, module connectivity, resources
  + Resources: mass transfers, deposits, recipes, process queues
  + Navigation: local motion and distant route planning
  + Logistics: reservations, haul contracts, scheduler, diagnostics
  + Workforces: organic life support and machine requirements
                         |
        Renderer / Scene Adapters (Godot Node2D, HUD)
                         |
           Save/Load + Deterministic Testing
```

**Rule:** The simulation cannot require a corresponding Godot scene node for an offscreen ship. Node2D is a view/controller, not the source of truth for cargo, jobs, ship location, or resource state.

## Core data types (proposed)

| Domain | Suggested model | Critical rule |
| --- | --- | --- |
| Universe | `UniverseState`, `SectorState`, `CelestialBodyState` | Stable IDs; parent relationships; repeatable generation seed. |
| Bodies | `BodyComposition`, `Deposit`, `BodyPhysicalState` | Track bulk/density, remaining resources, solid/gas/waste output and gross mass. |
| Materials | `MaterialDefinition`, `MaterialStack`, `RecipeDefinition` | Stable units, conversion accounting, explicit yield/loss. |
| Modules | `ModuleDefinition`, `ModuleInstance` | Mass, tile footprint, connection rules, ports, upkeep, functionality. |
| Construct | `ConstructState`, `ModuleGrid`, `ModuleNetworkState` | A ship, station, or mobile refinery is the same type plus state and modules. |
| Blueprint | `BlueprintDefinition`, `BuildOrder`, `ProductionQueue` | Versioned saved design, buildability and bill of materials. |
| Sites | `ConstructionSiteState`, `AnchoringState`, `StorageNodeState` | Deployment to a body with anchor/landing capability. |
| Navigation | `TravelPlan`, `OrbitState`, `TransitState` | Active local movement and approximate long-range transit are distinct. |
| Automation | `JobDefinition`, `JobInstance`, `LogisticsRequest`, `Reservation` | Explicit target, preconditions, state transitions, error reporting. |
| Labor | `CrewState`, `WorkforceRequirements`, `SupportNetwork` | Life support, power and upkeep have shared resource rules. |

IDs and schema versions should be independent of Godot node paths and resource filenames.

## Time model and simulation levels of detail

Use a **fixed simulation tick** (proposed initial rate 5–20 ticks/s for economy, independent of frame rendering), with explicit accumulated `delta` in seconds and interpolation for visible sprites. Physics may tick more frequently for active nearby vessels; this choice must be benchmarked.

- **Active vicinity:** detailed ship movement, collision if implemented, local maneuvering, visible module interactions.
- **Active sector but off-screen:** coarse movement and discrete job state updates; no unnecessary Godot scene nodes.
- **Distant sector / massive fleets:** aggregated scheduling, material and energy ledgers, route departure/arrival events, bounded batches of job progression.
- **Extremely large swarms:** group similar vessels by job/route/blueprint, using throughput abstractions while preserving inventories, losses and meaningful failure states. Keep the ability to 'materialize' representative ships when the player visits.

Use a **simulation scheduler** with fixed costs/work budgets, not a `Process()` callback on every miner. Progression must remain consistent between simulation resolutions; cap time acceleration if approximation is not reliable. Test offscreen versus foreground equivalence.

## Physics and orbital approach: recommendation, open for design approval

Realistic orbits are attractive but enormously complicate docking, planet-scale mining, time warp and huge fleet simulation. Proposed hybrid:

1. Star systems are 2D maps with orbiting bodies represented by stable orbital parameters ('on rails') during ordinary passage of time.
2. Nearby vessels use local navigation, gravitational acceleration where it improves gameplay, impulse/thrust and a simplified collision/docking model. A ship can enter a maintained orbit, land/anchor, or transfer to another body.
3. Far-away ships use planned trajectories, transit time and resource costs, not frame-by-frame rigid-body integrations.
4. Planet mass loss can update radius/gravity/orbital parameters at controlled intervals. Do not automatically solve the entire N-body system on every mining tick.
5. Use **coordinate frames** (sector, body-local, surface-local) and a camera-relative origin or floating origin to avoid precision problems at interplanetary distances.
6. A later warp corridor is a network edge between portals with defined route constraints, travel time and energy cost, not an unrestricted teleport that bypasses logistics.

**Acceptance risk:** If the intended promise becomes physically faithful orbit mechanics for every ship, refine the physics architecture before implementing advanced automation. The initial build should deliberately make this a documented design decision.

## Planetary surface representation

A round planet on the space map and a detailed 'landed' construction site are distinct scales. Recommended first version: landing opens a **body-local tile-grid surface area**, with build zones, deposits, gravity and anchors; the parent body keeps authoritative global mass/composition. Later, multiple sites, latitude bands, or wraparound surfaces can be added without changing the blueprint grid. Whether players can literally drive all the way around a 2D circular planet remains open.

## Modules, utility networks and deployment

The module editor stores a typed grid and footprint. At minimum, parts validate occupancy, connectivity and required services. Optional later systems include damage propagation, heat, directional thrust, drag, and structural stress.

- A construct can be `FreeFlight`, `Docked`, `Landed`, `Anchored`, `UnderConstruction`, or `InTransit` (illustrative states; revise as needed).
- Anchoring requires compatible leg/clamp/anchor modules, available target surface, sufficient stabilization, and a legal transition from flight to stationary operation.
- Anchored mining drills draw material from a body's deposit; storage and processing can remain active. Unanchoring must first stow/disable incompatible equipment.
- To lift off, combined construct thrust and energy must exceed relevant gravity/load limits under the chosen simplified model. Extremely massive structures may need an orbital shipyard or launch infrastructure.
- Reuse the *same construct data* for a small ship, a deployed factory and a future planet-sized mobile habitat. Renderer and simulation resolution can vary by scale.

## Mass and production conservation

This is a **fundamental invariant** and should be covered by tests.

- Each resource stack carries a material ID and mass in a chosen canonical unit (recommended **kilograms** internally; render tons in UI). Volume/density is separate.
- Extracting 100 kg of ore subtracts 100 kg from a body's remaining deposit and adds 100 kg to the mining system or its cargo ledger. Refineries transform input into output + slag/waste/gas, explicitly accounting for every kilogram.
- Industrial processes that emit or discard materials must record matter leaving the tracked inventory or captured in waste, not erase it silently.
- Machine/ship construction consumes resources and adds finished-part mass; deconstruction yields products/scrap governed by specified recovery efficiency.
- Body mass equals remaining geologic/atmospheric material plus any counted surface deposits, under a clearly documented convention. **Avoid double counting when mined material is stored on the same body**; define 'natural-body mass' versus 'total local gravitational mass'.
- Radius at approximately fixed bulk density follows `r_new = r_old * (M_new / M_old)^(1/3)`. Example: after 75% mass removed, radius is ~62.996% of original. Mining craters and scars should visibly appear before the gross radius noticeably changes.
- If volume compression, voids, gas loss or density stratification are modeled, treat radius as derived from composition/volume, not a simple universal formula.
- A body's gravity is based on current gravitational mass under the chosen simplified model; recalculate intelligently and show consequences without destabilizing the simulation every frame.

**Nuance:** Planet-wide mass accounting is practical, but fully deformable continuous planetary terrain, dynamic gravity on every object, and strict conservation across arbitrary megastructures are costly. Use explicit approximations and communicate them.

## Behavior editor and jobs

Avoid an opaque AI language model as the primary NPC engine. Use **data-driven actions, conditions, priorities and event-driven state machines** so players can reason about failures. Example:

```text
JOB: Iron Miner 01
Scope: nearest reachable iron deposit in Sector A
Prerequisites: drill, power/fuel, free cargo, accessible depot
1. Find matching source with > minimum viable ore
2. Reserve mining site + depot capacity (with expiration)
3. Travel to source
4. Mine until cargo >= 90% OR source exhausted
5. Travel to assigned depot
6. Unload reserved cargo
7. Service/refuel/recharge if needed
8. Repeat, otherwise WAIT with a visible reason
```

The scheduler should address conflicts: two ships selecting one depleted deposit, lack of storage space, reroutes, no available crew/oxygen, insufficient energy, broken components, route congestion, and tasks left unfinished by a save/load.

A minimal job implementation can expose `{state, job_id, target_id, progress, error_code, time_to_next_step}`. Later, advanced players can combine behaviors with branching conditions, priorities, quotas, supply/demand signals, and fleet-level policies. Store job scripts/graphs as portable, versioned data.

## Logistics and warp networks

Resources live in explicit inventories. Production sites post **offers** and **requests**; available vessels accept haul contracts using route costs, capacities, priorities, energy/life support needs, and queue limits. Prefer reservations or transaction-based transfers to prevent duplication under simultaneous jobs.

Warp gates/accelerators/corridors, when unlocked, expand a sector graph with build/operation costs, capacity, eligibility and transit behavior. Freight can enter/exit the network at multiple nodes if the eventual design allows. Make long-distance routing data-driven from the start, even if Phase 1 only supports direct local routes.

## Organics and robots

Both are forms of labor capacity supporting work orders, but they have distinct supply networks:

| Workforce | Possible requirements | Likely strengths and weaknesses |
| --- | --- | --- |
| Organic | Food, oxygen/atmosphere, water, habitat, some electricity | Flexible, recruitable diverse species; life support complexity. |
| Robotic | Significant power, charging, maintenance parts/cooling | Easy to run in vacuum; high electric infrastructure and replacement costs. |
| Mixed | Both stacks, connectors, sometimes shared logistics | Flexible but expensive to support two infrastructures. |

Exact balancing and whether any task strictly requires organic judgment is **not decided**. Do not encode arbitrary species traits without design approval.

## Data storage, testing and asset pipeline

- **Definitions:** author reusable materials/modules/recipes/blueprints in versioned files (JSON or Godot Resources, chosen consistently). **Runtime state:** separate from definitions, serialized to versioned save snapshots.
- **Save/load:** save only at safe simulation boundaries, with round-trip tests; preserve ship jobs, resource balances, travel events, construction sites, seeds and unlocked systems.
- **Automated tests:** mass ledger before/after transfer, production, mining, placement, save/load, deterministic tick progression, depleted deposit behavior, offscreen/foreground equivalence, job recovery from failure.
- **Performance:** establish baselines at 10, 100, 1,000, 10,000 simultaneous simulated task actors, distinguishing detailed ship simulation from aggregated workers. Do not promise any target without measurements.
- **Art:** authoritative `.pxo` source in `art_source/pixelorama/`, exported game-ready PNGs in `assets/art/`; script batch export when feasible; maintain a short asset manifest with licensing/attribution and exported spritesheet metadata.
- **Rendering:** each construct can render modules as tile chunks or consolidated visuals. Cache/batch sprite draw calls rather than one independent scene node per module at scale.

## Technical red flags to avoid

1. One physics object per pixel/tile/structural module for every distant craft.
2. One Godot `Node` per NPC in every distant star system.
3. Directly hard-coding NPC behavior into ship sprites/scenes.
4. Using displayed inventory values as the simulation's numeric truth.
5. Mining that simply spawns goods without decrementing deposits/body mass.
6. Losing ships or cargo because a sector is unloaded, or gaining extra goods after save/load.
7. Desynchronizing local physical movement and offscreen travel events.
8. Infinite-precision promises for physics over enormous coordinate scales.

## Decision gate before complex features

**After the first automated mining loop:** benchmark, run mass-accounting tests, confirm save/load, test changing simulation LOD, and demonstrate a player-authored blueprint produces a craft whose job can run without its sprite being present. Only then expand to many bodies and more powerful behaviors.
