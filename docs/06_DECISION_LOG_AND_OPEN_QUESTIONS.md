# 06 — Decision Log and Open Design Questions

This document keeps an implementation agent from accidentally transforming brainstorming suggestions into settled requirements.

## Confirmed by the creator in conversation

| Topic | Status / decision |
| --- | --- |
| Perspective and presentation | **Confirmed:** 2D top-down pixel art; planets and other celestial bodies appear as circles in space. |
| Player embodiment | **Confirmed:** pilot/build a personal ship and physically explore/arrive to establish the first node on new worlds. |
| Construction | **Confirmed:** grid/square modular creation of ships, orbital stations, factories, farms, mining units and settlements. |
| Blueprints | **Confirmed:** reuse designs for placing structures and producing ships. |
| Mobility/deployment | **Confirmed:** a ship and a factory are not rigidly separated; mobile factory ships can land, anchor and extract, then move again. |
| Automation | **Confirmed:** user-configured autonomous ship behavior; miners search for ore and deliver to storage/refinery. |
| Logistics | **Confirmed:** transport vessels carry goods between automated sites, the functional equivalent of belts in Factorio. |
| Scale | **Confirmed:** long-session progression from hands-on construction to enormous self-running infrastructure. |
| Workforce | **Confirmed:** organic people/aliens and robots can be used alone or together; they have different food/oxygen versus electric infrastructure burdens. |
| Warp | **Confirmed in principle:** late-game inter-region travel corridors/gates/accelerators with automated freight. Exact mechanics open. |
| Resource depletion | **Confirmed:** celestial bodies lose mass as resources are removed and can ultimately be dismantled. |
| Megastructures | **Confirmed:** planet-sized mobile structures, giant planet-consumers, and swarms of tiny ships are desired endgame paths. |
| Development | **Confirmed:** wants separate new project using established Agrigeddon-related Godot/Pixelorama/VS Code workflow. |

## Approved placeholders (may be replaced later)

| Date | Topic | Decision |
| --- | --- | --- |
| 2026-11-20 | Body motion (Phase 1) | **Approved as a placeholder:** celestial bodies follow fixed circular orbits ("on rails"), and position is a pure function of simulation time. The period is set directly and not derived from gravity. Ship physics is still undecided (open question 1). |
| 2026-11-20 | Simulation tick | Provisional: fixed 20 ticks/s (`FixedStepClock.TicksPerSecond`), the top of the 5–20 range proposed in docs/02, with render interpolation. Revisit when ship movement needs it. |

| 2026-11-20 | Ship flight (Phase 1) | **Approved as a placeholder** (the creator flew it: "it works", though meeting up with the asteroid is hard). Newtonian inertia with no gravity, drag, collisions, fuel or power use. Acceleration is total thrust divided by total mass along the nose, with a fixed turn rate (1.5 rad/s). "Brake" is a flight assist that uses the same thrust to match the selected target body's velocity, or to stop relative to the sector when there's no target (rendezvous assist, added 2026-11-20 after a playtest). Ships fly through bodies (open question 3). |
| 2026-11-20 | Starter orbit speed | Placeholder: the asteroid's orbit period is 20–40 min, about 60–150 m/s with under 1 m/s² of curvature, so the starter ship can catch it and hold position. It was 3–5 min, at about 550–1,000 m/s, which made rendezvous nearly impossible. |
| 2026-11-20 | Body collision (open question 3) | **Chosen by the creator as a placeholder:** bodies are solid. A ship is a circle (the bounding circle of its grid) that can't end a tick inside a body. On contact it is put on the surface and *lands*: it moves with the body, with no damage, bounce or friction model. Turning is allowed while landed. Only throttle with an outward component lifts it off (brake never does). Docking, damage, and anchoring with anchor parts stay open for Phase 2. |
| 2026-11-20 | Blueprint editor (Phase 2, first step) | Placeholder scope: a 15×15 editable area. Validation errors use the same rules as flying. Disconnected parts are only a *warning* (open question 4). There's no part rotation yet, and all thrust still acts along the nose. The editor makes designs only and never changes the ship you are flying; building ships is Phase 2 fabrication. Player designs are saved to `user://blueprints/<id>.json`, where the id comes from the name. Built-in `data/blueprints` files are read-only and can't be overwritten from the game. |
| 2026-11-20 | Starter materials (open question 5) | **Chosen by the creator as placeholders:** Metal, Electronics and Composites, defined in `data/materials`. Ore, refining and material properties are still open (Phase 3/4). |
| 2026-11-20 | Part cost rule | A part's `materialsKg` must add up exactly to its `massKg`: building converts materials into parts with no gain or loss. Waste and yield, if wanted, would be added later as explicit outputs. Amounts are whole-kg values in double kg; the authoritative ledger unit is decided with mining (Phase 3). |
| 2026-11-21 | Shipyards (Phase 2) | **Chosen by the creator:** a shipyard is a grid-built station (`DesignRole.Station`: needs a fabricator part; ships need a cockpit and thrust). Shipyards may be landed *or* in orbit. A finished ship is parked next to its yard: beside a landed yard on the surface, or a little behind an orbiting yard on the same orbit. Docks and parking against the yard come later. The starter shipyard is on a fixed orbit 600 m above the Primary. Placeholder build rate: 500 kg/s per fabricator. |
| 2026-11-21 | Fixed orbits for craft | Stations and parked ships are held on a fixed circular orbit ("on rails") because there is no gravity yet. A ship leaves its parking orbit only by its own throttle (brake never releases it), the same rule as landed ships. |
| 2026-11-21 | Ordering builds | **Chosen by the creator:** from anywhere for now (shipyard panel, key Y). A design's full material cost is taken from the yard's stock when the order is accepted. A refused order takes nothing and reports the exact shortfall. Cancelling refunds everything. Only the first order in the queue is built. The ordered design is a snapshot. |
| 2026-11-21 | Inventory unit | **Chosen by the creator:** inventories hold whole kilograms (64-bit integers), so conservation checks are exact. Part costs must be whole kg. Mining will accumulate fractions in the drill and move whole kg. No storage capacity limit yet. Starting stock is 30 t metal, 6 t electronics and 5 t composites: enough for two starter ships, not three. |
| 2026-11-21 | Choosing a ship to fly | **Chosen by the creator:** a simple scrolling fleet list (key F) where clicking a craft pilots it. With many ships in the late game this needs a fuller fleet system (filters, groups, search); a single cycle key was rejected for that reason. |
| 2026-11-21 | Anchoring (Phase 2) | **Chosen by the creator:** an *anchor clamp* part (0.8 t) lets a landed craft lock to the body it's on (G) and release again. While anchored, thrust can't lift it off; this is the basis for mining with drills later. A *foundation* part (0.5 t, no electronics, so cheaper) marks a **fixed structure**: it anchors itself as soon as it lands and can never release. So a mobile factory (clamps, thrusters, cockpit) costs more than a fixed one, which makes mobility a progression goal. A ship design with a foundation is an error. The starter prospector gets one clamp (17.8 t, 13.2 t metal, 2.7 t electronics, 1.9 t composites; the starting stock still builds two). Placeholder: anchoring doesn't check which part touches the ground. |
| 2026-11-21 | Part rotation | **Chosen by the creator:** parts rotate in 90° steps (R in the editor). A thruster pushes the way it faces. Only rear-facing thrusters (rotation 0) give forward thrust; others are shown as "other" thrust and are unused until a later flight upgrade (strafe or torque turning was deferred). The editor warns about them. Turn rate stays fixed. Design files are now schema 2 (optional `rot`); schema 1 files still load. |
| 2026-11-21 | Docks | **Chosen by the creator:** a *docking port* part is one berth, just outside the port in the direction it opens. A ship within 100 m of a free berth and within 1 m/s of the station's speed docks with K. It is held nose-in and moves with the station until K or its own throttle undocks it. Brake never does. Tab can now target craft: the brake matches the target craft's velocity, and the HUD shows gap, relative speed and free ports. New ships from a yard take a free port first, then park. The starter shipyard has two ports. |
| 2026-11-21 | Cargo (Phase 1 leftover) | **Chosen by the creator:** ships carry cargo up to their cargo bays' capacity (whole kg), and cargo adds to the mass thrust has to push. Materials move only between a ship and the station it's docked at (cargo panel, C), all-or-nothing, with nothing created or lost. Stations have no storage limit yet. **Power is deferred** until a power design talk (generation, what drains it, organics vs robots). |
| 2026-11-21 | Building stations | **Chosen by the creator:** the shipyard builds valid station designs too. An orbital station is placed on its own fixed orbit next to the yard. A fixed structure (foundation) needs a body chosen in the order panel; when finished it is placed on that body's surface on the side facing the yard, nose out (foundations down), and anchored. This is a placeholder for later construction ships or sites. A design counts as a ship if it has a cockpit or thrusters, otherwise a station; a station needs a fabricator, docking port or cargo bay. Built-in `surface_depot` is a fixed structure. |
| 2026-11-21 | Mining yield (Phase 3) | **Chosen by the creator:** placeholder metal deposits. Rock-1 holds a finite 500 t metal deposit that is part of its mass, so mined metal feeds the shipyard directly. Ore and refining come with Phase 4. |
| 2026-11-21 | Drilling and depletion | A craft with drills, anchored on a body with a deposit, mines automatically at its drills' rate (25 kg/s per drill) until its hold is full or the deposit is empty. Whole kg move from the deposit and the body's mass into cargo; the unfinished fraction is kept on the craft. Body masses are whole kg in new games, so the ledger is exact. Radius follows mass (it shrinks very slightly). An empty deposit stops miners with a reason. Stations with cargo bays are now limited to that capacity (needed for "destination full"); stations without cargo bays stay unlimited. |
| 2026-11-21 | Autopilot | **Chosen by the creator:** automated ships fly by the same flight rules as the player. The autopilot only sets turn, throttle and brake each tick, using the same anchor, release, dock, undock and transfer actions. It brakes against its target and detours around bodies by circling them about 1.5 km up, in steps of up to 45°. After mining it climbs 1 km before heading off, because the asteroid keeps moving. Placeholder tuning: cruise 150 m/s, land at 3 m/s. Off-screen shortcuts are for Phase 5. |
| 2026-11-21 | Mining jobs | **Chosen by the creator:** a job panel per ship (J): source body, material, destination station with a docking port, and "return when the hold is 25/50/75/100% full"; Start/Stop. The cycle is: fly to the source, land, anchor and mine, lift off, fly home, dock, unload, repeat. Every state is shown (HUD, panel, fleet list), and blocked states give a plain reason (source empty, destination full, no free port, can't anchor). A ship running a job ignores the flight keys until the job is stopped. Jobs are stored as data (save v6). |
| 2026-11-21 | Ore and refining (Phase 4) | **Chosen by the creator:** asteroids yield raw *iron ore* and *silicate* (this replaces the Phase 3 metal placeholder). A *refinery* part (50 kg of input per second) on any station runs data-driven recipes in file order (`data/recipes`): 1 t iron ore → 0.7 t metal + 0.3 t slag; 1 t silicate → 0.5 t composites + 0.5 t slag. Recipes must weigh the same in and out (checked on load), so slag is explicit waste. A batch takes its inputs from stock when it starts and adds its outputs when done (it waits if there's no room). The starter shipyard gets a refinery. Electronics come only from starting stock for now. Slag piles up; nothing uses it yet. |
| 2026-11-21 | More asteroids and survey | **Chosen by the creator:** the seed adds Rock-2 (400 t silicate + 100 t iron ore, ~34–40 km) and Rock-3 (300 t iron ore, ~44–50 km); Rock-1 has 500 t iron ore. Mineable bodies start *unsurveyed*. The craft you are piloting surveys any body whose surface it comes within 2 km of (the first visit). Mining jobs and fixed structures need a surveyed body, so the player founds new sites. NPC expansion comes later. Saves from before v7 count every body as surveyed. |
| 2026-11-21 | Freight jobs | **Chosen by the creator:** a second job type, *freight*: dock at a pickup station, load one material once at least N t is there, fly to the destination, dock, unload, repeat. Optional *keep stocked*: deliver only until the destination holds X t, then wait there with the rest. Same autopilot and docking as mining, with plain wait reasons. A request/offer marketplace with reservations is a later step. |
| 2026-11-21 | Publishing | **Chosen by the creator:** the repo is published to `github.com/funnybunnyofdoom/SpaceAutomationSandbox` (`origin`). Branch `master` is pushed and tracks `origin/master`. Pushes still happen only when the creator asks. |
| 2026-11-20 | Ship grid scale | Placeholder: one design cell is 4 m. Forward is −y. Parts have no rotation, and all thrust acts along the nose. Cells don't need to be connected yet (open question 4). |

## Recommended defaults, not yet approved

| Topic | Proposal | Why |
| --- | --- | --- |
| Programming language | Godot 4 .NET/C# | Align with current Agrigeddon pipeline; C# is suitable for data-heavy simulation. |
| Repository/folder | Local `SpaceAutomationSandbox`, separate Git repo | Neutral placeholder; prevents project coupling. |
| Physics | Hybrid on-rails body orbits + nearby ship motion + abstract distant transit | Balance orbit fantasy with huge fleet count. |
| Planet surfaces | Switch from circular space representation to a body-local tile construction view on landing | Preserves square-grid editing without requiring arbitrary deformations of the whole globe. |
| Jobs | Graphical state/condition/action editor | Debuggable and accessible; supports programmable NPCs without scripting language. |
| Resource units | Mass in kg, with volume/density separately | Conservation and numerical consistency. |
| Simulation | Fixed-step authoritative state, separate rendering, sector/simulation LOD | Essential for saves and performance at late-game scale. |
| Sprite module grid | 16×16 or 32×32 art pixels | Pixelorama-friendly; choose after a visual test. |
| Starter gameplay | One body + one depot + one miner blueprint and repeatable cargo job | Fastest proof that construction and automation are fun. |
| Publishing | Local repo first, remote after permission | Avoid unapproved GitHub changes. |

## Open questions for the next creative conversation

1. **Orbit realism:** Should orbital transfers and thrust be physically modeled (a major mechanic), a lightweight arcade approximation, or a selectable mode? Do players need stable orbits everywhere?
2. **Planet surface scale:** A wraparound continuous 2D circumference, tiled landed-map zones, or zooming directly into the planet's disk? Can players dig tunnels inside it?
3. **Piloting and collision:** Do player ships physically collide with all bodies? Is docking manual, assisted, or automatic? What about damage from landing?
4. **Design rules:** Are module grids entirely freeform or must hull, piping, power connections and structural constraints form a connected network? Can pieces detach?
5. **Resource taxonomy:** How many starter materials, what special properties (mass, heat resistance, conductivity, radioactivity), and how refined goods are produced?
6. **Mining visualization:** Do players carve visibly irregular craters, alter terrain by tile, or only visually shrink the celestial disk? Can the entire planet become hollow?
7. **Life support:** How granular should food, water, oxygen, crew comfort, population and species compatibility be?
8. **Automation authoring:** Fixed high-level jobs initially, node-based conditions, or an advanced programmable rules system eventually? Prioritization, fallbacks, and job ownership?
9. **Travel scope:** One seamless star system, region-separated systems, or a galaxy map with warp nodes? How is time acceleration handled?
10. **Threats:** Environmental hazards only, hostile alien fleets, diplomacy, defense, resource conflicts, or a purely peaceful sandbox?
11. **Research and progression:** Technology tree, exploration unlocks, manufacturing sophistication, or player knowledge/discovered resource-driven progression?
12. **Endgame megastructures:** What resources/energy/time constraints stop an early infinite-growth exploit? What distinguishes one giant ship from swarm specialization?
13. **Victory and sandbox:** An open-ended civilization toy, optional megaproject goals, quests, or campaign narrative?
14. **Performance objective:** Is there a target maximum active ships, automated jobs, and number of star systems on the user's actual PC?
15. **Build tooling:** Which exact Godot version, C# SDK, Pixelorama binary and VS Code extensions are already installed on this Windows system?

## Avoid hidden feature commitments

- No combat, multiplayer, economy, story, alien politics, atmosphere chemistry, hyperspecific structural physics, or complete accurate N-body simulation is confirmed yet.
- When implementing a vertical slice, prefer extensible placeholders and explicit settings instead of permanent hard-coded assumptions.
- Discuss major new mechanics before coding them; preserve already accepted details when revising an individual feature.
