# AGENTS.md — Space Automation Sandbox

Instructions for a coding Work agent working in this **new space game repository only**.

## Scope and preservation

- This is a **separate game** from Agrigeddon. Never modify files in `funnybunnyofdoom/Agrigeddon2`, its Windows working tree, its settings, dependencies, Git configuration, or its production art. Do not use Agrigeddon assets or code unless explicitly authorized and licensing/ownership permit reuse.
- Never assume a local path exists. Check before creating or writing. If the proposed destination exists, inspect it and avoid overwriting.
- Use the existing Windows toolchain as a model: Godot 4 .NET/C#, VS Code, Pixelorama, Git, GitHub. Verify versions/paths before acting. Maintain a per-project configuration.
- Default to a local, F5-testable change, followed by user review and explicit commit/push. Do not create PRs or branches, publish to GitHub, or change the existing `master` workflow unless asked. For an entirely new repo, make a local baseline commit only with permission or as part of explicit initialization instructions.
- Keep code and assets modular, deterministic where possible, and testable without running an enormous scene.

## Vision guardrails

- Top-down 2D pixel art; physically visit celestial bodies with a custom-built player ship.
- Build ships, stations, factories, farms, mines, and other constructs from square-grid parts using a common blueprint system.
- Designs can be mobile, docked/anchored, or deployed; resource extractors can land and attach to celestial bodies.
- Construct autonomous ships, facilities, task scripts, and logistics networks. Movement of goods by vessels is the functional equivalent of Factorio belts.
- Mass and composition of celestial bodies decrease as material is removed; endgame includes planet dismantling, huge mobile structures, and tiny autonomous swarms.
- Crew and machine infrastructure can coexist: organics require food, oxygen, etc.; machines tend to be power/maintenance-intensive.
- Later-game warp links allow interstellar / intersector transport and automated freight.

## Coding practices

1. Make the smallest working vertical slice. Keep public APIs/data formats intentional and documented.
2. Separate world simulation state from rendering, input, UI, and Godot scene lifetimes. Never make an off-screen ship depend on a visible `Node2D` to exist.
3. Define a single authoritative resource ledger in consistent units for each inventory and body. Avoid double counting when converting, transporting, producing, or removing materials.
4. Use stable IDs for bodies, crafts, modules, blueprints, jobs, depots, inventories, and regions. Save references by IDs, not scene-tree paths.
5. Cap work per simulation tick, batch updates, and profile before optimizing. A thousand ships must not require a thousand rendered scenes while off-screen.
6. Start with a **data-driven**, player-accessible behavior builder, not a natural-language agent or arbitrary user code interpreter. Jobs must show why they are waiting or failing.
7. Make the game deterministic enough for repeatable test seeds and replayable failure cases. Avoid frame-rate-dependent changes to inventory.
8. Keep pixel art source `.pxo` separate from exported `.png`. Track licenses/attributions for third-party placeholders. Don't import art from Agrigeddon automatically.
9. Provide F5 reproducibility and explicit verification for each milestone. Preserve user-edited files and working mechanics.
10. Report: files changed, assumptions made, commands run, test results, known limitations, and next step.

## Recommended folder structure

```text
project.godot
AGENTS.md
README.md
docs/
scenes/main/
scenes/ui/
scenes/visuals/
scripts/core/
scripts/simulation/
scripts/construction/
scripts/logistics/
scripts/physics/
scripts/save/
assets/art/
art_source/pixelorama/
data/parts/
data/materials/
data/recipes/
data/blueprints/
tools/art/
tests/
```

**Working title is not final.** An agent must not interpret unapproved suggestions in design docs as requirements. Follow the decision log.
