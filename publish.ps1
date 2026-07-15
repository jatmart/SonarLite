#requires -Version 5.1
<#
.SYNOPSIS
    The one correct way to refresh SonarLite so the exe you run, the logon-autostart target, and
    (optionally) the GitHub repo are always the same version.

.DESCRIPTION
    Publishes a ReadyToRun (R2R) build straight into the canonical run folder
    (bin\Release\net8.0-windows), repoints the HKCU autostart entry at it, and relaunches.

    Why this and not `dotnet build`: R2R (ahead-of-time native precompilation, ~20% faster cold
    start) ONLY happens on `dotnet publish -r win-x64`. A plain `dotnet build` writes an IL-only exe
    into the same folder -- the app still runs, just slower to start -- so always refresh with this
    script, never a bare build.

.PARAMETER Push
    Also commit any pending changes and push to origin, so GitHub lands on the same version too.

.PARAMETER Message
    Commit message used by -Push when there are pending changes. Default: "chore: sync build".

.PARAMETER Tray
    Relaunch minimized to the tray (exactly like the logon launch) instead of showing the window.

.EXAMPLE
    .\publish.ps1
    Local refresh: R2R build + repoint autostart + relaunch windowed.

.EXAMPLE
    .\publish.ps1 -Push -Message "feat: add loudness meter"
    Same, then commit everything with that message and push -- running exe == autostart == GitHub.
#>
[CmdletBinding()]
param(
    [switch]$Push,
    [string]$Message = "chore: sync build",
    [switch]$Tray
)

$ErrorActionPreference = 'Stop'
$root   = $PSScriptRoot
$proj   = Join-Path $root 'SonarLite.csproj'
$outDir = Join-Path $root 'bin\Release\net8.0-windows'
$exe    = Join-Path $outDir 'SonarLite.exe'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

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

Write-Host "==> Relaunching..." -ForegroundColor Cyan
if ($Tray) { Start-Process $exe -ArgumentList '--tray' } else { Start-Process $exe }

Push-Location $root
$sha = git rev-parse --short HEAD
Pop-Location
Write-Host ""
Write-Host "Done. Running R2R build from:" -ForegroundColor Green
Write-Host "  $exe"
Write-Host "  git HEAD: $sha"
