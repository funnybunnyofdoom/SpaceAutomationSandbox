<#
.SYNOPSIS
    Phase 0 bootstrap check for SpaceAutomationSandbox (working title).

.DESCRIPTION
    Run from anywhere; it always operates on the project folder that contains this script.
    Steps (all local, nothing is published):
      1. Audit tool versions (Godot .NET, dotnet SDK, git, VS Code, Pixelorama).
      2. dotnet build of this project only.
      3. Headless Godot import, then a short headless run of the main scene (smoke test).
      4. 'git init' a separate local repository on branch 'master' (skipped if .git exists).
      5. Optional: baseline commit (-Commit), optional read-only Agrigeddon status check.
    Writes a log to _temp\phase0-setup.log (ignored by git).

    It never touches any other project. The Agrigeddon check only runs
    'git --no-optional-locks status' / 'git log -1', which are read-only.

.PARAMETER GodotExe
    Path to the Godot .NET editor. Defaults to $env:GODOT4, then the path discovered on
    this PC during Phase 0 (C:\dev\Tools\Godot\Godot_v4.7.2-stable_mono_win64.exe).

.PARAMETER Commit
    Also create the local baseline commit. Without it, files are left unstaged for your review.

.PARAMETER AgrigeddonPath
    Optional. Folder of the Agrigeddon working tree, to print its git status read-only.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\tools\setup\Phase0-Setup.ps1
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\tools\setup\Phase0-Setup.ps1 -Commit -AgrigeddonPath C:\dev\Games\Agrigeddon\agrigeddon
#>
[CmdletBinding()]
param(
    [string]$GodotExe,
    [switch]$Commit,
    [string]$AgrigeddonPath
)

$ErrorActionPreference = 'Continue'
$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$TempDir = Join-Path $ProjectRoot '_temp'
New-Item -ItemType Directory -Force -Path $TempDir | Out-Null
$LogPath = Join-Path $TempDir 'phase0-setup.log'
Start-Transcript -Path $LogPath -Force | Out-Null

$results = [ordered]@{}
function Section($name) { Write-Host ""; Write-Host "==== $name ====" -ForegroundColor Cyan }

# --- Safety: refuse to run inside another repo -------------------------------------------
Section 'Project root'
Write-Host "Project root: $ProjectRoot"
if (-not (Test-Path (Join-Path $ProjectRoot 'project.godot'))) {
    Write-Error 'project.godot not found next to tools\setup. Aborting.'; Stop-Transcript | Out-Null; exit 1
}
if ($ProjectRoot -match 'Agrigeddon') {
    Write-Error 'Project root path contains "Agrigeddon". Refusing to run. Aborting.'; Stop-Transcript | Out-Null; exit 1
}
$parentRepo = $null
$probe = Split-Path $ProjectRoot -Parent
while ($probe) {
    if (Test-Path (Join-Path $probe '.git')) { $parentRepo = $probe; break }
    $next = Split-Path $probe -Parent
    if ($next -eq $probe) { break }
    $probe = $next
}
if ($parentRepo) {
    Write-Error "A parent folder is already a git repo ($parentRepo). The new game must not be nested. Aborting."
    Stop-Transcript | Out-Null; exit 1
}
Write-Host 'OK: not nested inside another git repository.'

# --- 0. VS Code workspace files ----------------------------------------------------------
# Templates live in tools\vscode\ and are copied into .vscode\ only if missing (never overwrites).
$VsTemplates = Join-Path $ProjectRoot 'tools\vscode'
$VsDir = Join-Path $ProjectRoot '.vscode'
if (Test-Path $VsTemplates) {
    New-Item -ItemType Directory -Force -Path $VsDir | Out-Null
    Get-ChildItem $VsTemplates -Filter *.json | ForEach-Object {
        $dest = Join-Path $VsDir $_.Name
        if (Test-Path $dest) { Write-Host "VS Code: kept existing .vscode\$($_.Name)" }
        else { Copy-Item $_.FullName $dest; Write-Host "VS Code: installed .vscode\$($_.Name)" }
    }
}

# --- 1. Audit ----------------------------------------------------------------------------
Section '1. Tool audit'
if (-not $GodotExe) { $GodotExe = $env:GODOT4 }
if (-not $GodotExe) { $GodotExe = 'C:\dev\Tools\Godot\Godot_v4.7.2-stable_mono_win64.exe' }
$GodotConsole = $GodotExe -replace '\.exe$', '_console.exe'
if (-not (Test-Path $GodotConsole)) { $GodotConsole = $GodotExe }

if (Test-Path $GodotExe) {
    $gv = (& $GodotConsole --version 2>&1 | Select-Object -Last 1)
    Write-Host "Godot:      $GodotExe  ->  $gv"
    $results['Godot'] = "$gv ($GodotExe)"
} else {
    Write-Warning "Godot editor not found at $GodotExe (set -GodotExe or `$env:GODOT4)."
    $results['Godot'] = 'NOT FOUND'
}

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($dotnet) {
    $sdks = (& dotnet --list-sdks) -join '; '
    Write-Host "dotnet:     $($dotnet.Source)"
    Write-Host "SDKs:       $sdks"
    $results['.NET SDKs'] = $sdks
    if ($sdks -notmatch '(^|; )(8|9|10)\.') { Write-Warning 'No .NET 8+ SDK listed. Godot 4.7 C# needs one (project targets net8.0).' }
} else {
    Write-Warning 'dotnet not on PATH.'; $results['.NET SDKs'] = 'dotnet NOT ON PATH'
}

$git = Get-Command git -ErrorAction SilentlyContinue
$results['Git'] = if ($git) { (& git --version) } else { 'NOT ON PATH' }
Write-Host "Git:        $($results['Git'])"

$code = Get-Command code -ErrorAction SilentlyContinue
$results['VS Code'] = if ($code) { ((& code --version 2>$null) | Select-Object -First 1) } else { 'code NOT ON PATH' }
Write-Host "VS Code:    $($results['VS Code'])"

$pixelExe = if ($env:PIXELORAMA) { $env:PIXELORAMA } else { 'C:\dev\Tools\Pixelorama\Pixelorama.exe' }
if (Test-Path $pixelExe) {
    $pv = (Get-Item $pixelExe).VersionInfo.ProductVersion
    if (-not $pv) { $pv = 'version not embedded in exe; check Help > About' }
    $results['Pixelorama'] = "$pv ($pixelExe)"
} else { $results['Pixelorama'] = "NOT FOUND at $pixelExe" }
Write-Host "Pixelorama: $($results['Pixelorama'])"

# --- 2. dotnet build ---------------------------------------------------------------------
Section '2. dotnet build'
Push-Location $ProjectRoot
if ($dotnet) {
    & dotnet build (Join-Path $ProjectRoot 'SpaceAutomationSandbox.csproj') -nologo
    $results['dotnet build'] = if ($LASTEXITCODE -eq 0) { 'PASS' } else { "FAIL (exit $LASTEXITCODE)" }
} else { $results['dotnet build'] = 'SKIPPED (no dotnet)' }
Write-Host "dotnet build: $($results['dotnet build'])"

# --- 3. Godot headless import + smoke run ------------------------------------------------
Section '3. Godot headless import and smoke run'
if (Test-Path $GodotConsole) {
    & $GodotConsole --headless --path $ProjectRoot --import 2>&1 | Out-Host
    $results['Godot import'] = if ($LASTEXITCODE -eq 0) { 'PASS' } else { "FAIL (exit $LASTEXITCODE)" }

    $smoke = & $GodotConsole --headless --path $ProjectRoot --quit-after 120 2>&1
    $smoke | Out-Host
    $smokeText = $smoke -join "`n"
    if ($smokeText -match '\[Bootstrap\] Main scene ready' -and $smokeText -notmatch 'SCRIPT ERROR|ERROR:') {
        $results['Headless smoke run'] = 'PASS (bootstrap line printed, no errors)'
    } elseif ($smokeText -match '\[Bootstrap\] Main scene ready') {
        $results['Headless smoke run'] = 'PASS WITH WARNINGS (bootstrap line printed; review ERROR lines above)'
    } else {
        $results['Headless smoke run'] = 'FAIL (bootstrap line not printed)'
    }
} else { $results['Godot import'] = 'SKIPPED'; $results['Headless smoke run'] = 'SKIPPED' }
Write-Host "Import: $($results['Godot import'])   Smoke: $($results['Headless smoke run'])"

# --- 4. Local git repository -------------------------------------------------------------
Section '4. Local git repository'
if ($git) {
    if (Test-Path (Join-Path $ProjectRoot '.git')) {
        Write-Host '.git already exists; leaving it as is.'
        $results['Git repo'] = 'already existed'
    } else {
        & git -C $ProjectRoot init -b master
        $results['Git repo'] = if ($LASTEXITCODE -eq 0) { 'initialized (branch master, no remote)' } else { 'git init FAILED' }
    }
    $remotes = (& git -C $ProjectRoot remote) -join ', '
    $results['Git remotes'] = if ($remotes) { $remotes } else { 'none (as intended)' }
    Write-Host 'Files git would track (ignored files excluded):'
    & git -C $ProjectRoot status --short --untracked-files=all

    if ($Commit) {
        & git -C $ProjectRoot add -A
        & git -C $ProjectRoot commit -m 'Phase 0: bootstrap isolated Godot 4.7 .NET project'
        $results['Baseline commit'] = if ($LASTEXITCODE -eq 0) { (& git -C $ProjectRoot log -1 --oneline) } else { 'commit FAILED (check git user.name/user.email)' }
    } else { $results['Baseline commit'] = 'not made (run again with -Commit after review)' }
} else { $results['Git repo'] = 'SKIPPED (no git)' }

# --- 5. Optional read-only Agrigeddon check ----------------------------------------------
if ($AgrigeddonPath) {
    Section '5. Agrigeddon read-only status'
    if (Test-Path (Join-Path $AgrigeddonPath '.git')) {
        Write-Host "Last commit: $(& git -C $AgrigeddonPath --no-optional-locks log -1 --oneline)"
        Write-Host 'Working tree changes (should match what you had before):'
        & git -C $AgrigeddonPath --no-optional-locks status --short
        $results['Agrigeddon'] = 'status printed read-only; compare with your expectation'
    } else { $results['Agrigeddon'] = "no .git at $AgrigeddonPath" }
}
Pop-Location

# --- Summary -----------------------------------------------------------------------------
Section 'SUMMARY'
$results.GetEnumerator() | ForEach-Object { '{0,-20} {1}' -f $_.Key, $_.Value } | Out-Host
Write-Host ""
Write-Host "Next: open the project in Godot ($GodotExe), press F5, confirm the starfield window opens."
Write-Host "Log saved to $LogPath"
Stop-Transcript | Out-Null
