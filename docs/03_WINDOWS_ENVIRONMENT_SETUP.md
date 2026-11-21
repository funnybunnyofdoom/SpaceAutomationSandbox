# 03 — Windows Development Environment Setup

**Objective:** Reuse the existing Godot + Pixelorama + VS Code + GitHub pipeline, but create an **isolated sibling game**. Do not change Agrigeddon's editor setup, code, project files, repo, or assets.

## Previously established context (verify on this machine)

- Agrigeddon's repo: `funnybunnyofdoom/Agrigeddon2`; private. Local game testing is performed by opening the project in Godot and pressing **F5**. User prefers local edit -> F5 test -> iterate -> commit/push. Branches/PRs only when requested.
- Previous path reports included `C:\dev\Games\Agrigeddon\agrigeddon` and `C:\dev\Games\PLAYTEST-01-Agrigeddon`. These are not proof of today's active path. **Inspect and avoid both.**
- Agrigeddon uses Godot .NET + C# and an art workflow involving Pixelorama `.pxo` source -> PNG or spritesheet -> Godot. Do not assume its exact Godot version or the tool executable locations.

## Phase 0 workstation audit (PowerShell, read-only)

Run commands in a new PowerShell terminal. Failure of any `Get-Command` means only that the executable may not be on PATH. It is not proof the program is uninstalled.

```powershell
Get-Command godot, godot-mono, dotnet, git, code, pixelorama, pwsh -ErrorAction SilentlyContinue |
  Select-Object Name, Source

where.exe godot 2>$null
where.exe dotnet 2>$null
where.exe git 2>$null
where.exe code 2>$null

dotnet --list-sdks
git --version
code --version
```

Locate the actual Agrigeddon project **read-only** using a reasonable path or the Godot Project Manager. Record the working `project.godot`, Godot editor executable, .NET SDK and whether `dotnet build` works on that project **only if the user approves a read-only inspection/build**. Never run migrations or open/save Agrigeddon's project in a new version just to collect information.

## Separate workspace and tools

Suggested destination (change only if existing Games root differs):

```text
C:\Dev\Games\SpaceAutomationSandbox\
```

The new project root should contain its own `.git`, `project.godot`, `.csproj`, `docs`, game assets, user-facing README and VS Code settings. Reuse existing executables; do not duplicate Pixelorama, VS Code or SDK installations unless a missing dependency is verified.

For the first bootstrap, use the **installed compatible Godot 4 .NET editor** already used for Agrigeddon. If the current editor is non-.NET, obtain a .NET edition **alongside** the existing installation only with user approval. If a newer editor is wanted, install side-by-side, never replace the known working executable.

## Initial Godot project creation

These are instructions for the Work agent, not commands that have already been executed:

1. In Godot Project Manager, select **Create New Project** with a new empty directory under the chosen Games root; select a 2D-compatible renderer (recommend **Compatibility** for initial 2D portability unless current setup warrants otherwise).
2. Ensure the editor is the .NET-enabled build; add a C# script to `Main.tscn` and let Godot generate/create the C# solution/project if necessary. Verify the `.csproj` target framework against the installed SDK and Godot version; do not blindly copy Agrigeddon's framework target.
3. Create `scenes/main/Main.tscn` with a simple camera and persistent background, and set it as the main scene in `project.godot`.
4. Create baseline `README.md`, `.gitignore`, `AGENTS.md`, architecture/design docs, `art_source/pixelorama`, `assets/art`, `tools/art`, `scripts`, `data`, and `tests`.
5. Start a new **local-only** Git repository. Set the new project's own remote only after the user confirms its repository name/privacy and authorizes creation/push.
6. Press **F5** and confirm a nonblank window opens, no script errors, and closing/reopening the project succeeds.

Godot can manage its own C# project generation. Do not fabricate `.csproj` settings from memory; use the installed editor's generated output when possible.

## Recommended `.gitignore` for the new repository

```gitignore
# Godot-generated data
.godot/
# .NET build outputs
**/bin/
**/obj/
# Local editor and tooling caches
.vscode/
.idea/
*.user
*.suo
# OS files
Thumbs.db
.DS_Store
# Logs and local outputs
*.log
_temp/
```

Track production `.png` and editable `.pxo` art sources; don't ignore them. `.vscode/` can be selectively committed later if team-shared launch tasks are useful.

## Pixelorama asset workflow

```text
art_source/pixelorama/ship_parts/*.pxo  ->  assets/art/ship_parts/*.png
art_source/pixelorama/bodies/*.pxo      ->  assets/art/bodies/*.png
art_source/pixelorama/ui/*.pxo          ->  assets/art/ui/*.png
```

Reuse or adapt the pattern of an export PowerShell script under `tools/art/`, and use a tool override/environment variable rather than hard-coding a personal Pixelorama executable path. Export deterministically and document each sprite's cell size, pivot/anchor, and licensing. For a grid-building game, decide a common module art cell size (a **proposed** starting value is 16×16 or 32×32 pixels) before making production artwork. Avoid committing generated thumbnail caches.

## IDE, build and test

Suggested regular loop:

```powershell
# Run in the NEW project's root, not Agrigeddon
Set-Location 'C:\Dev\Games\SpaceAutomationSandbox'

dotnet build
# Open project in Godot and press F5

git status --short
# Review changes, then commit/push only when requested
```

VS Code: install/enable a compatible C# extension if missing; open **only** the new game's folder as the workspace. Configure editor path and scene debug launch based on the actual editor install. Do not blindly reuse the Agrigeddon solution file.

## Bootstrap test checklist

- [ ] New folder is a sibling, not nested in the Agrigeddon repo.
- [ ] Agrigeddon tracked files, `git status`, editor version and Git remote remain unchanged.
- [ ] New project launches with F5.
- [ ] `dotnet build` works in new project (or the exact environment blocker is reported).
- [ ] Pixelorama opens and test sprite `.pxo` can be exported/imported without modifying Agrigeddon assets.
- [ ] Git ignores generated `.godot/`, `bin/`, and `obj/` folders.
- [ ] Initial project docs and agent guidance are in the **new** repo.
- [ ] A clean closing report records actual paths, versions, commands and test results.
- [ ] No GitHub remote was published without explicit permission.
