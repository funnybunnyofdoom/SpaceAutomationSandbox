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
