#requires -Version 5.1
<#
.SYNOPSIS
    The one correct way to refresh SonarLite so the exe you run, the logon-autostart target, and
    (optionally) the GitHub repo + a full GitHub Release are always the same version.

.DESCRIPTION
    Publishes a ReadyToRun (R2R) build straight into the canonical run folder
    (bin\Release\net8.0-windows), repoints the HKCU autostart entry at it, and relaunches.

    Why this and not `dotnet build`: R2R (ahead-of-time native precompilation, ~20% faster cold
    start) ONLY happens on `dotnet publish -r win-x64`. A plain `dotnet build` writes an IL-only exe
    into the same folder -- the app still runs, just slower to start -- so always refresh with this
    script, never a bare build.

.PARAMETER Push
    Also commit any pending changes and push to origin, so GitHub is on the same version too.

.PARAMETER Message
    Commit message used by -Push/-Release when there are pending changes.
    Default: "chore: sync build" (or "release: vX.Y.Z" when -Release is given).

.PARAMETER Tray
    Relaunch minimized to the tray (exactly like the logon launch) instead of showing the window.

.PARAMETER Release
    Cut a full release: stamp <Version> in the csproj to this number, publish, commit, push, create
    and push tag vX.Y.Z, zip the build, and publish a GitHub Release with the zip attached. Accepts
    "0.3.1" or "v0.3.1". Implies -Push.

.PARAMETER Notes
    Release notes body for -Release. Default: a changelog auto-generated from the commit subjects
    since the previous tag, plus an install blurb.

.EXAMPLE
    .\publish.ps1
    Local refresh: R2R build + repoint autostart + relaunch windowed.

.EXAMPLE
    .\publish.ps1 -Push -Message "feat: add loudness meter"
    Same, then commit everything with that message and push.

.EXAMPLE
    .\publish.ps1 -Release 0.3.1
    Full release: bump to 0.3.1, build, commit, push, tag v0.3.1, and publish the GitHub Release.
#>
[CmdletBinding()]
param(
    [switch]$Push,
    [string]$Message = "chore: sync build",
    [switch]$Tray,
    [string]$Release,
    [string]$Notes,
    [switch]$IfChanged
)

$ErrorActionPreference = 'Stop'
$root   = $PSScriptRoot
$proj   = Join-Path $root 'SonarLite.csproj'
$outDir = Join-Path $root 'bin\Release\net8.0-windows'
$exe    = Join-Path $outDir 'SonarLite.exe'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

$doRelease = -not [string]::IsNullOrWhiteSpace($Release)
$tag = $null

# --- Release preamble: validate version, stamp csproj, prepare notes -------------------------
if ($doRelease) {
    $ver = $Release.TrimStart('v', 'V')
    if ($ver -notmatch '^\d+\.\d+\.\d+$') { throw "Release version must look like X.Y.Z (got '$Release')." }
    $tag = "v$ver"

    Push-Location $root
    try {
        git rev-parse -q --verify "refs/tags/$tag" *> $null
        if ($LASTEXITCODE -eq 0) { throw "Tag $tag already exists -- pick a new version." }
        $prevTag = (git tag --sort=-v:refname | Select-Object -First 1)
    } finally { Pop-Location }

    $Push = $true
    if (-not $PSBoundParameters.ContainsKey('Message')) { $Message = "release: $tag" }

    # Stamp <Version> in the csproj (UTF-8, no BOM) so the exe self-reports this version.
    $csprojText = [System.IO.File]::ReadAllText($proj)
    if ($csprojText -notmatch '<Version>.*?</Version>') { throw "No <Version> element in $proj to update." }
    $csprojText = [regex]::Replace($csprojText, '<Version>.*?</Version>', "<Version>$ver</Version>")
    [System.IO.File]::WriteAllText($proj, $csprojText)
    Write-Host "==> Stamped <Version> = $ver" -ForegroundColor Cyan
}

# --- Auto-refresh gate (for the Stop hook) ---------------------------------------------------
# The editor hook calls `-IfChanged` at the end of every turn, but a rebuild+relaunch is only
# worth its ~dropout when a source file actually changed. Skip fast otherwise so plain Q&A never
# disturbs the running app. Never gates a real release.
if ($IfChanged -and -not $doRelease) {
    $srcMax = (Get-ChildItem $root -Recurse -File -Include *.cs, *.xaml |
        Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' } |
        Measure-Object -Property LastWriteTimeUtc -Maximum).Maximum
    $exeTime = if (Test-Path $exe) { (Get-Item $exe).LastWriteTimeUtc } else { [datetime]::MinValue }
    if ($null -ne $srcMax -and $exeTime -ge $srcMax) {
        Write-Host "No source changes since last build; nothing to refresh." -ForegroundColor DarkGray
        exit 0
    }
}

Write-Host "==> Stopping any running SonarLite..." -ForegroundColor Cyan
Get-Process SonarLite -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 700

Write-Host "==> Publishing ReadyToRun build -> $outDir" -ForegroundColor Cyan
dotnet publish $proj -c Release -r win-x64 --self-contained false -o $outDir --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }

# `dotnet publish -o` leaves the RID build under a nested win-x64\ with its own SonarLite.exe.
# Remove it so there's exactly one exe in the run folder and no "which one do I run?" ambiguity.
$nested = Join-Path $outDir 'win-x64'
if (Test-Path $nested) { Remove-Item $nested -Recurse -Force }

# R2R roughly doubles our assembly (IL ~261KB -> IL+native ~428KB); use that as a sanity check.
$dllKb = [math]::Round((Get-Item (Join-Path $outDir 'SonarLite.dll')).Length / 1KB)
if ($dllKb -lt 350) {
    Write-Warning "SonarLite.dll is ${dllKb}KB -- expected ~428KB for R2R. ReadyToRun may not have applied."
} else {
    Write-Host "    R2R confirmed (SonarLite.dll ${dllKb}KB)." -ForegroundColor Green
}

Write-Host "==> Pointing autostart at the canonical exe (with --tray)..." -ForegroundColor Cyan
Set-ItemProperty -Path $runKey -Name SonarLite -Value ('"{0}" --tray' -f $exe)

if ($Push) {
    Write-Host "==> Syncing git..." -ForegroundColor Cyan
    Push-Location $root
    try {
        $dirty = git status --porcelain
        if ($dirty) {
            git add -A
            git commit -m $Message
            if ($LASTEXITCODE -ne 0) { throw "git commit failed." }
        } else {
            Write-Host "    Working tree clean; nothing to commit." -ForegroundColor DarkGray
        }
        git push
        if ($LASTEXITCODE -ne 0) { throw "git push failed." }
    } finally { Pop-Location }
}

# --- Release finale: tag, zip, publish GitHub Release (app is still stopped so files aren't locked) ---
if ($doRelease) {
    Push-Location $root
    try {
        Write-Host "==> Tagging $tag..." -ForegroundColor Cyan
        git tag -a $tag -m "SonarLite $tag"
        if ($LASTEXITCODE -ne 0) { throw "git tag failed." }
        git push origin $tag
        if ($LASTEXITCODE -ne 0) { throw "git push tag failed." }

        Write-Host "==> Zipping build..." -ForegroundColor Cyan
        $zip = Join-Path $root ("bin\SonarLite-$tag-win-x64.zip")   # under bin\ (gitignored)
        if (Test-Path $zip) { Remove-Item $zip -Force }
        $files = Get-ChildItem $outDir -File | Where-Object { $_.Extension -ne '.pdb' }  # ship no debug symbols
        Compress-Archive -Path $files.FullName -DestinationPath $zip
        Write-Host ("    {0:N0} KB, {1} files" -f ((Get-Item $zip).Length / 1KB), $files.Count) -ForegroundColor Green

        if (-not $PSBoundParameters.ContainsKey('Notes')) {
            if ([string]::IsNullOrEmpty($prevTag)) { $range = 'HEAD' } else { $range = "$prevTag..HEAD" }
            $changelog = (git log $range --no-merges --pretty=format:"- %s") -join "`n"
            if ([string]::IsNullOrWhiteSpace($changelog)) { $changelog = "- Maintenance release." }
            $Notes = @"
## What's new
$changelog

## Install
Requires the **.NET 8 Desktop Runtime** (win-x64). Download ``SonarLite-$tag-win-x64.zip``, extract, and run ``SonarLite.exe``. Use the in-app **Start with Windows** checkbox to enable autostart.
"@
        }

        Write-Host "==> Creating GitHub Release $tag..." -ForegroundColor Cyan
        $notesFile = Join-Path $env:TEMP "sonarlite-release-notes-$tag.md"
        [System.IO.File]::WriteAllText($notesFile, $Notes)
        try {
            gh release create $tag "$zip#SonarLite $tag (win-x64)" --title "SonarLite $tag" --notes-file $notesFile
            if ($LASTEXITCODE -ne 0) { throw "gh release create failed (is gh installed and authenticated?)." }
        } finally { Remove-Item $notesFile -Force -ErrorAction SilentlyContinue }
    } finally { Pop-Location }
}

Write-Host "==> Relaunching..." -ForegroundColor Cyan
if ($Tray) { Start-Process $exe -ArgumentList '--tray' } else { Start-Process $exe }

Push-Location $root
$sha = git rev-parse --short HEAD
Pop-Location
Write-Host ""
Write-Host "Done. Running R2R build from:" -ForegroundColor Green
Write-Host "  $exe"
Write-Host "  git HEAD: $sha"
if ($doRelease) { Write-Host "  released: $tag -> $(git -C $root remote get-url origin)" -ForegroundColor Green }
