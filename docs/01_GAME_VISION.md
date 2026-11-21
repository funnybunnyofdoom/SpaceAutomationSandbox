# 01 — Game Vision & Design Notes

**Working description:** 2D top-down, pixel-art, modular spacecraft and civilization automation sandbox. No final title yet.

## One-sentence pitch

Design your own ships and infrastructure from square parts, personally explore and establish footholds on celestial bodies, then automate fleets and settlements until your industry can consume planets, build moving worlds, or spread as vast swarms.

## Touchstones (inspiration, not clone specification)

- **Factorio:** the fun comes from building and debugging systems, scaling and optimizing autonomous production chains. Freight vessels are effectively *moving conveyor belts* between geographically separate sites.
- **Kerbal Space Program:** the fun of designing functional craft with components that matter mechanically.
- **A modular sandbox:** every major construct comes from player-authored designs, not a fixed prefabricated building menu.

## Confirmed design decisions from the discussion

1. **Top-down 2D space.** Celestial bodies appear as circles in space; ships can orbit them, land, gather resources, and establish operations. Pixel art and square-grid component construction are preferred.
2. **Personal vessel and exploration.** The player designs/pilots a personal ship, travels to previously unvisited bodies, and establishes the first construction/logistics node on a new body before expanding its automated industry.
3. **Common modular construction.** Players design ships, orbital stations, mines, smelters, farms, warehouses, and factories using square components. A large factory can become mobile if equipped appropriately.
4. **Blueprints.** Save, edit, reuse, and place designs; shipyards manufacture blueprint-defined ships. Blueprints are core to scaling.
5. **Deployable/anchorable constructs.** A giant mining factory ship may land, anchor/mount itself to a planet, extract materials, detach, and move elsewhere (subject to sufficient capabilities).
6. **Autonomous programmable systems.** Players configure behaviors, e.g., seek specified ores, mine, transport goods to defined storage, refuel/recharge, return, repeat. The system must support increasingly elaborate job networks.
7. **Production and logistics.** Materials vary by body; materials can be mined, stored, processed, and used to build parts. Ships move resources among planetary or orbital depots, factories, and shipyards.
8. **Living and mechanical workforce choices.** The player can employ/recruit multiple organic species, robots, or any mix. Organics impose food/oxygen/life-support demands; robots draw relatively heavier power and may have other upkeep requirements. Specialization versus hybrid infrastructure should offer meaningful trade-offs, not hard exclusivity.
9. **Growing spatial scale.** Local travel first; later construct warp gates, accelerators, or corridors to connect distant systems. NPC freight craft use the network autonomously.
10. **Physical resource depletion.** Mining actually removes a celestial body's material and mass. Over time it changes volume, shape/appearance, and potentially gravitational effects.
11. **Extreme endgame.** Eventually completely dismantle planets, build planet-sized ships or superstructures that consume celestial bodies, or instead build massive networks of small specialized swarm units. These are different scaling strategies, not forced mutually exclusive factions.
12. **Long-play simulation.** The intended experience supports long sessions building and optimizing complex logistics rather than quick arcade rounds.

## Player role and fantasy

You are both a hands-on pioneer and a systems architect. In the early game you gather, craft, fly, and build directly. In the midgame you engineer repeatable industrial loops. In the late game you increasingly solve constraints at the network/civilization level: throughput, energy, material flows, workforce support, transport bottlenecks, hazards, and massive construction programs.

## Representative loops

**Early:** Build a tiny exploration/mining ship -> survey an asteroid or moon -> mine essential parts -> craft a starter construction node -> establish a storage depot -> design and fabricate first automated miner -> configure its round-trip job.

**Mid:** Locate deposits across planets -> blueprint specialized collectors, refineries and freighters -> construct farms and life support and/or power plants and robot services -> create independent production chains -> automate ship replacement, fuel and repairs -> expand into other sectors via constructed travel links.

**Late:** Consume small moons, then planetary crusts and eventually entire bodies -> allocate immense amounts of matter/energy -> build macrostructures, mobile manufacturing planets, intersector infrastructure or distributive fleets of thousands/millions of small units. Use aggregate simulation for distant swarms.

## Construction and component model

A **construct** is a connected or explicitly linked grid of modules. Its behavior emerges from actual installed modules and available supplies. Examples:

- Structure: frame/hull, armor, bulkhead, bracing, landing gear, anchors/clamps.
- Motion: thrusters, maneuvering modules, fuel tanks, attitude control, navigation systems.
- Power: generator, battery, solar collector, connectors, distribution and control.
- Industry: drill, collector, refinery, assembly machine, farm plot, processor, storage, transporter.
- Operations: cockpit/bridge, drone controller, sensors, cargo handling, fabrication controls.
- Habitability: pressurized habitat, oxygen and water systems, food production, crew quarters.
- Automation: controllers/schedulers/jobs; later fleet coordination and warp access.

Each module may have a mass, dimensions, material cost, durability, energy usage/production, inventory ports and specialized requirements. A blueprint's material bill is the sum of constituent modules and assembly costs.

**Design principle:** Never artificially label a creation a 'ship' or 'factory' if its hardware can do both. Deployed state and available modules determine what it can do. Gameplay balance can still penalize trying to launch a mountain-sized factory from a high-gravity surface.

## A first completely playable loop

The **first proof of fun** should be tiny:

1. Pilot an editable player ship in a small 2D space scene with a body and a rock/ore source.
2. Enter a grid editor, attach/select modules, verify material cost and functional effects.
3. Save a miner blueprint, manufacture one craft.
4. Give the miner a simple repeatable job: mine iron -> return to depot -> unload -> repeat.
5. Watch depot inventory increase as asteroid inventory/mass decreases. Pausing, save/load, and task diagnostics should preserve the loop.

Do not wait for dozens of planets, organic life, warp travel, or planetary megastructures before testing whether this is enjoyable.

## Proposed game-design guardrails (not yet approved)

- **No hard class restrictions:** a facility with thrusters is mobile; a ship with anchors and drills can become an extractor.
- **No magic cargo:** discrete material transfer and recipe accounting to make automation meaningful.
- **No mandatory script coding:** graphical state/rule editor with optional advanced logic later.
- **Player engagement:** player piloting and exploration remain valuable even when logistics become self-running. New bodies require an initial manual arrival/claim/construction action, unless a future researched automation permits it.
- **Two perspectives:** space map and accessible local planetary build area. Exact surface representation is still undecided.

## Important unresolved design areas

Exact orbital physics (real Kepler versus game-friendly), time acceleration, surface representation for a 2D world, combat and hazards, multiplayer, research tree, crew autonomy, procedural universe topology, economy/alien diplomacy, atmosphere/gravity simulation, and what qualifies as 'completion' are not decided. See `06_DECISION_LOG_AND_OPEN_QUESTIONS.md`.
