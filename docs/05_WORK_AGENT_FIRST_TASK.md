# 05 — COPY/PASTE PROMPT FOR THE WORK AGENT

> Attach this full ZIP or the included Markdown files when you send this prompt. Do **Phase 0 only**; gameplay development comes after environment verification.

---

I want you to set up a **new, isolated Godot game project** on my Windows PC, alongside my existing Agrigeddon project, using its established **Godot .NET/C#, Pixelorama, VS Code, Git/GitHub, local F5 test -> iterate -> push** toolchain.

**The new game** is a 2D top-down pixel-art modular space automation sandbox. I will pilot a custom-built ship, explore planets, moons and asteroids, place construction nodes and use square-grid blueprints to create ships, stations, mines, factories and farms. Everything should eventually be able to operate autonomously through configurable jobs and logistics fleets. Structures may be mobile, landed, or anchored to bodies for resource extraction. Celestial bodies genuinely lose material and mass as mined. In the long term, the game supports both giant planet-consuming or planet-scale mobile structures **and** tiny coordinated swarms. The workforce may include organic crews requiring food/oxygen and robots requiring more electric/maintenance infrastructure. Warp infrastructure eventually connects distant areas.

**Use the attached packet as requirements/context:** read `README_START_HERE.md`, `AGENTS.md`, and each file in `docs/`. Explicitly distinguish agreed design requirements from architecture recommendations and open questions.

## Important restriction

**Do not modify Agrigeddon in any way.** Its GitHub repo is `funnybunnyofdoom/Agrigeddon2` and it has a separate local working tree. Its local path has varied in prior sessions, so discover its current path read-only. Do not merge new files into its repo, upgrade its Godot version or SDK, alter its Git setup, overwrite art, or copy assets/code from it. Do not make changes to unrelated projects.

## Execute only these steps now

1. **Audit the workstation:** discover the already functioning Godot .NET executable/version, C#/.NET SDK, Pixelorama, VS Code and Git. Confirm which Games directory contains the current Agrigeddon project. Don't install/replace tools without explaining the actual dependency and getting permission.
2. **Create a sibling folder for the new game:** propose/use `C:\Dev\Games\SpaceAutomationSandbox` if that is compatible with the existing workspace; otherwise create a sibling under the verified Games root. Check for collisions before writing. This is a *working directory name*, not a final game title.
3. **Create a new Godot 4 .NET/C# project**, compatible with the installed editor. Add a minimal 2D `Main.tscn`, a C# entry script or tested equivalent, and set the main scene. Make sure pressing **F5** opens a functional window without errors.
4. **Initialize a separate local Git repo**, add an appropriate `.gitignore`, `README.md`, root `AGENTS.md`, `docs/`, `scenes/`, `scripts/`, `data/`, `assets/art/`, `art_source/pixelorama/`, `tools/art/`, and `tests/` as useful. Keep source `.pxo` and exported assets separate. Copy the handoff docs into the new project's documentation.
5. **Configure a basic VS Code workflow** for working on this new project and building C# with `dotnet build`. Avoid hard-coded personal executable locations unless discovered and documented. Ensure all steps are reproducible on Windows.
6. **Test the environment** with the actual C# build, editor/F5 if available, and a simple Pixelorama source/export/import check if feasible. If GUI access or an executable is missing, report the limitation clearly instead of claiming a test passed.
7. **Stop and report:** actual paths, software versions, all files created, exact test commands/results, whether Agrigeddon is unchanged, and any blockers.

## Explicitly out of scope for this first pass

- No gameplay systems, new materials, ship movement, mining logic or art production yet.
- No premature full roadmap, elaborate UI or placeholder megastructure implementation.
- No creation of GitHub remote repository, push, branch, or PR until I approve the new project/repository name and tell you to publish.
- No unexplained migrations, destructive commands, edits to existing project settings, or assumptions that you have physical access if you do not.

## Quality bar and future direction

The engine architecture will eventually separate simulation from rendering, support deterministic/resource-conserving jobs, preserve physical resource depletion, maintain blueprint-defined modules, and scale to large fleets by simulating offscreen work cheaply. See `docs/02_TECHNICAL_ARCHITECTURE.md`. Those are **design constraints for future implementation**, not tasks to implement during Phase 0.

When done, give me a concise but precise completion report and a recommended first gameplay implementation step, then **wait for my approval before Phase 1**.
