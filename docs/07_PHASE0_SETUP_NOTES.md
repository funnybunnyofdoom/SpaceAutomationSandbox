# 07 — Phase 0 setup notes (2026-11-20)

What the agent actually found and decided while bootstrapping. PC-side checks were run on 2026-11-20 (see "Verification results").

## Workstation findings (read-only)

| Item | Finding | How it was found |
| --- | --- | --- |
| Machine | Windows x64, device `neue-ziel` | device info |
| Games root | `C:\dev\Games\` containing `Agrigeddon`, `Gachahalla`, `PLAYTEST-01-Agrigeddon` | directory names only, contents not opened |
| Collision check | `C:\dev\Games\SpaceAutomationSandbox` did not exist | directory listing |
| Godot | `C:\dev\Tools\Godot\Godot_v4.7.2-stable_mono_win64.exe` (+ `_console.exe`), .NET build (`GodotSharp\` present) | file listing |
| Godot C# SDK | `Godot.NET.Sdk.4.7.2.nupkg`; runtime config `tfm: net8.0` | `GodotSharp\Tools\nupkgs`, `GodotSharp\Api\Release\GodotPlugins.runtimeconfig.json` |
| Godot version | `4.7.2.stable.mono.official.ed1daf0bf` | `Godot_v4.7.2-stable_mono_win64_console.exe --version` |
| Pixelorama | `C:\dev\Tools\Pixelorama\Pixelorama.exe`, file version `1.2.3.0` | exe `VersionInfo.ProductVersion` |
| .NET SDKs | `C:\Program Files\dotnet\dotnet.exe`; SDKs 5.0.301, 6.0.402, 7.0.407, 8.0.203, 9.0.305, 10.0.401. No `global.json` at the time, so `dotnet build` used **10.0.401** to build the `net8.0` target. *(2026-11-21: `global.json` now pins 10.0.401 with `rollForward: latestFeature`, #48.)* | `dotnet --list-sdks`, `dotnet --version` |
| Git | `git version 2.56.0.windows.4` | `git --version` |
| VS Code | `1.142.0` (`code` on PATH) | `code --version` |
| Shell used by the script | Windows PowerShell 5.1.22621.6133 | transcript header |
| Agrigeddon | Not opened, listed only by folder name; exact project path not confirmed | — |

## Decisions taken in Phase 0 (reversible)

- Project folder `C:\dev\Games\SpaceAutomationSandbox`, sibling of Agrigeddon, own git repo, branch `master`, no remote. *(2026-11-21: the creator approved publishing; `origin` is now `https://github.com/funnybunnyofdoom/SpaceAutomationSandbox.git`, with `master` pushed and tracking `origin/master`.)*
- `Godot.NET.Sdk/4.7.2`, `net8.0`, Compatibility renderer, nearest texture filter, 1280×720 viewport with `canvas_items` stretch.
- The `.csproj` was written by hand from the installed SDK version and runtime config, because the editor couldn't be run remotely. If Godot regenerates it or creates a `.sln`, keep the editor's version.
- Selected `.vscode/` files are tracked (tasks, launch, settings, extensions), and everything else in `.vscode/` is ignored. Tool paths come from `GODOT4` / `PIXELORAMA` environment variables.
- Pixelorama batch export is deferred until its command-line export is verified on this PC. Manual export plus `tools/art/Check-ArtPairs.ps1` for now.

## Verification results (2026-11-20)

`Phase0-Setup.ps1` was run twice: first at 21:46 (it ran `git init -b master` and copied `tools\vscode\*.json` into `.vscode\`), then again at the end of Phase 0 with no flags. The second run's results:

| Check | Result |
| --- | --- |
| Nesting guard | PASS: no parent folder is a git repo |
| `dotnet build SpaceAutomationSandbox.csproj` | PASS: 0 warnings, 0 errors, output `.godot\mono\temp\bin\Debug\SpaceAutomationSandbox.dll` |
| Godot headless `--import` | PASS |
| Headless smoke run (`--quit-after 120`) | PASS: printed `[Bootstrap] Main scene ready. C# assembly loaded. Godot 4.7.2-stable (official)`, no `ERROR` lines |
| Godot editor F5 | PASS (the creator checked it by hand): the starfield window with the "Phase 0 bootstrap OK" label opens. The editor did not create a `.sln` or rewrite the `.csproj` |
| Git repository | `C:\dev\Games\SpaceAutomationSandbox`, branch `master`, **no remotes**, `user.name`/`user.email` configured globally |
| `.gitignore` | `.godot/` and `_temp/` are ignored (`git check-ignore -v`). The four shared `.vscode/*.json` files are tracked |
| Baseline commit | Not made yet. It waits for the creator's approval |
| Agrigeddon / PLAYTEST-01-Agrigeddon | Not touched. No command was run in or against those folders, and the optional `-AgrigeddonPath` check was deliberately **not** used |

## Still open

- [ ] Pixelorama round trip: create `art_source/pixelorama/test/test_tile.pxo` (16×16), export `assets/art/test/test_tile.png`, confirm that Godot imports it. Not tested yet, because it needs the GUI. It doesn't block Phase 1, since no art is needed before the first sprites.
- [ ] Optional: add a `global.json` to pin the .NET SDK, if builds should not drift when newer SDKs are installed. Not done, because nothing currently needs it.
