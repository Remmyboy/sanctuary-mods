<#
.SYNOPSIS
  Drives the running game through the dev-only probe plugin (tools/Probe).

.DESCRIPTION
  probe.ps1 install            build tools/Probe and deploy it to SanctuaryMods\SanctuaryProbe
  probe.ps1 uninstall          remove it from the game folder
  probe.ps1 launch             start the game through Steam and wait for the probe
  probe.ps1 alive              is the probe loaded, and what is the game doing
  probe.ps1 <line> [<line>...] run command lines as one batch and print the output
  probe.ps1 -File cmds.txt     the same, from a file

  Each argument is one probe command line, e.g.
    probe.ps1 state "eval Engine.GetSimulationTick()" "shot hud"
  `probe.ps1 help` lists the verbs. Batches run in order; `wait`, `waitlua`
  and `waitmatch` hold a batch without blocking the game.

  install and launch refuse while the game is already running unless -Force:
  the user may be in a real match, and a deploy hot-loads into it.
#>
param(
    [Parameter(Position = 0, ValueFromRemainingArguments = $true)][string[]]$Lines,
    [string]$File,
    [int]$Timeout = 300,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$Dir = Join-Path $env:LOCALAPPDATA 'SanctuaryProbe'
$Repo = Split-Path $PSScriptRoot -Parent
$GameExe = 'C:\Program Files (x86)\Steam\steamapps\common\Sanctuary Shattered Sun Playtest\engine\Sanctuary.exe'
$ModDir = Join-Path (Split-Path $GameExe) 'SanctuaryMods\SanctuaryProbe'

function Test-GameRunning { [bool](Get-Process -Name Sanctuary -ErrorAction SilentlyContinue) }

function Get-Alive {
    $f = Join-Path $Dir 'alive.txt'
    if (-not (Test-Path $f)) { return $null }
    $age = ((Get-Date) - (Get-Item $f).LastWriteTime).TotalSeconds
    if ($age -gt 10 -or -not (Test-GameRunning)) { return $null }
    (Get-Content $f -Raw).Trim()
}

function Wait-Alive([int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        $a = Get-Alive
        if ($a) { return $a }
        Start-Sleep -Milliseconds 500
    }
    $null
}

$verb = if ($Lines) { $Lines[0].ToLowerInvariant() } else { '' }

switch ($verb) {
    'install' {
        if ((Test-GameRunning) -and -not $Force) {
            throw 'Sanctuary is running: installing hot-loads the probe into it. Ask the user, then pass -Force.'
        }
        & dotnet build (Join-Path $Repo 'tools\Probe\Probe.csproj') -c Release -p:LiveDeploy=true -v:minimal -nologo
        if ($LASTEXITCODE -ne 0) { throw 'probe build failed' }
        if (-not (Test-Path (Join-Path $ModDir 'SanctuaryProbe.dll'))) { throw "probe not deployed to $ModDir" }
        "installed to $ModDir"
        return
    }
    'uninstall' {
        if (Test-Path $ModDir) { Remove-Item $ModDir -Recurse -Force; "removed $ModDir" } else { 'not installed' }
        if (Test-GameRunning) { 'the game is still running with the probe loaded; it goes on the next restart' }
        return
    }
    'launch' {
        if (Test-GameRunning) {
            if (-not $Force) { throw 'Sanctuary is already running (the user may be playing). Pass -Force to use it anyway.' }
        } else {
            Start-Process 'steam://rungameid/4511930'
        }
        $a = Wait-Alive 180
        if (-not $a) { throw 'the probe did not report within 180 s (is it installed? probe.ps1 install)' }
        $a
        return
    }
    'alive' {
        $a = Get-Alive
        if ($a) { $a } else { if (Test-GameRunning) { 'game running, probe NOT loaded' } else { 'game not running' } }
        return
    }
}

$batch = if ($File) { Get-Content $File } else { $Lines }
if (-not $batch) { Get-Help $PSCommandPath -Detailed; return }

if (-not (Get-Alive)) {
    if (Test-GameRunning) { throw 'the game is running but the probe is not loaded (probe.ps1 install -Force)' }
    throw 'the game is not running (probe.ps1 launch)'
}

$id = (Get-Date).ToString('yyyyMMdd-HHmmss-fff')
$in = Join-Path $Dir "in\$id.cmd"
$out = Join-Path $Dir "out\$id.txt"
# Write then rename, so the probe never reads half a batch.
[IO.File]::WriteAllLines("$in.tmp", [string[]]$batch)
Move-Item "$in.tmp" $in

$deadline = (Get-Date).AddSeconds($Timeout)
while (-not (Test-Path $out)) {
    if ((Get-Date) -gt $deadline) { throw "no output after $Timeout s (batch $id still queued or running)" }
    if (-not (Test-GameRunning)) {
        if (Test-Path $out) { break }
        throw 'the game exited before the batch finished'
    }
    Start-Sleep -Milliseconds 250
}
Get-Content $out -Raw
Remove-Item $out
