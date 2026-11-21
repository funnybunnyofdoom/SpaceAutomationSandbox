<#
.SYNOPSIS
    Checks that every Pixelorama source has an exported PNG and vice versa.

.DESCRIPTION
    Convention: art_source/pixelorama/<group>/<name>.pxo  ->  assets/art/<group>/<name>.png
    Reports missing exports, orphan PNGs, and exports older than their source.
    Read-only; it does not launch Pixelorama or change files.
    (Automated batch export is deferred until Pixelorama's command-line export is verified
    on this PC; export manually via File > Export for now.)
#>
$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$SourceRoot = Join-Path $ProjectRoot 'art_source\pixelorama'
$ExportRoot = Join-Path $ProjectRoot 'assets\art'
$problems = 0

Get-ChildItem $SourceRoot -Recurse -Filter *.pxo | ForEach-Object {
    $rel = $_.FullName.Substring($SourceRoot.Length).TrimStart('\') -replace '\.pxo$', '.png'
    $png = Join-Path $ExportRoot $rel
    if (-not (Test-Path $png)) { Write-Warning "Missing export: $rel"; $problems++ }
    elseif ((Get-Item $png).LastWriteTime -lt $_.LastWriteTime) { Write-Warning "Stale export (older than .pxo): $rel"; $problems++ }
    else { Write-Host "OK  $rel" }
}

Get-ChildItem $ExportRoot -Recurse -Filter *.png | ForEach-Object {
    $rel = $_.FullName.Substring($ExportRoot.Length).TrimStart('\') -replace '\.png$', '.pxo'
    if (-not (Test-Path (Join-Path $SourceRoot $rel))) {
        Write-Host "Note: PNG without .pxo source (fine for third-party art if listed in ASSET_MANIFEST.md): $rel"
    }
}

if ($problems -eq 0) { Write-Host 'Art pairs check: PASS' -ForegroundColor Green } else { Write-Host "Art pairs check: $problems problem(s)" -ForegroundColor Yellow }
