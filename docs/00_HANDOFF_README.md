# Space Automation Sandbox — Work Agent Handoff Packet

**Status:** Early concept / bootstrap planning. **Project name:** intentionally provisional. **Prepared:** 2026-11-20.

This packet translates a voice brainstorming session into a practical starting point for a **new, separate** 2D space construction/automation game. It is a proposal for implementation, not proof that a Godot project, GitHub repository, or any assets already exist.

## What to give the Work agent

Attach this ZIP (or all the Markdown files) and paste the entire text of `docs/05_WORK_AGENT_FIRST_TASK.md` into Work. Ask Work to **execute Phase 0 only**, then report what it actually found, created, and tested. Do not start implementing the full game in one pass.

## Documents in this packet

| File | Use |
| --- | --- |
| `README_START_HERE.md` | How to use the packet, scope, assumptions. |
| `AGENTS.md` | Standing instructions for any coding agent; place at the new repo root. |
| `docs/01_GAME_VISION.md` | Confirmed design decisions, player fantasy, game loops, and boundaries. |
| `docs/02_TECHNICAL_ARCHITECTURE.md` | Proposed scalable Godot/C# architecture and risks. |
| `docs/03_WINDOWS_ENVIRONMENT_SETUP.md` | Safe Windows setup alongside Agrigeddon, commands, tooling. |
| `docs/04_PHASES_AND_ACCEPTANCE.md` | Phase 0 and first vertical slice, future milestone outline, acceptance criteria. |
| `docs/05_WORK_AGENT_FIRST_TASK.md` | Copy/paste agent brief to initialize the project without touching Agrigeddon. |
| `docs/06_DECISION_LOG_AND_OPEN_QUESTIONS.md` | Distinguishes agreed facts from suggested defaults and undecided questions. |

## Critical project boundary

Existing Agrigeddon context: GitHub repo `funnybunnyofdoom/Agrigeddon2` (private, according to previous discussions), Windows local work, test with Godot **F5**, iterate, then commit/push. **Do not modify, move, migrate, merge into, or change the toolchain for Agrigeddon.** Its local path has varied across sessions. Agents must discover/confirm the existing Games root and actual path **read-only** instead of guessing.

Suggested sibling layout (after verifying the actual root):

```text
C:\Dev\Games\
├── Agrigeddon\                  # EXISTING: do not touch
│   └── agrigeddon\              # Example only; verify actual path
└── SpaceAutomationSandbox\      # NEW, separate Git repository
    ├── project.godot
    ├── SpaceAutomationSandbox.csproj
    ├── AGENTS.md
    ├── docs\
    ├── scenes\
    ├── scripts\
    ├── data\
    ├── assets\
    ├── art_source\pixelorama\
    ├── tools\art\
    └── tests\
```

The **folder and repo name are working placeholders**, not a finalized game title. The Work agent should not publish or create a remote repository without confirming a suitable name and account permission. If the project already exists, inspect rather than overwrite it.

## Implementation posture

- Target Windows and **Godot 4 .NET + C#**, using the already-working Godot version when compatible. Do **not** silently upgrade Agrigeddon or install a new system-wide version over the current one.
- Reuse the proven pattern: Pixelorama `.pxo` sources -> exported PNG/spritesheets -> Godot; use VS Code and Git/GitHub.
- Preserve player-made, block-grid blueprints and automation as the central game mechanic.
- Design for massive endgame scales, **but implement and benchmark a tiny end-to-end prototype first**. The core challenge is coherent simulation, not a giant art pass.
- Mark anything not explicitly settled as **recommended/proposed**, not canon.

## Deliverables requested of Work, Phase 0

1. Verify installed prerequisites and find an appropriate sibling workspace without changing Agrigeddon.
2. Create a clean **new** Godot .NET/C# project with a functioning F5 main scene.
3. Add local repository, README/AGENTS/docs, sane ignore rules, optional VS Code tasks, and asset source/export folders.
4. Build/test the empty project; capture the exact commands, version numbers, paths, and test outcomes.
5. Stop and report before Phase 1 gameplay work or publishing a new GitHub remote.
