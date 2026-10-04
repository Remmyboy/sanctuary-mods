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
  probe.ps1 leakcheck <Mod> -Force
                               hot-reload leak check: `objects` counts, touch the
                               deployed SanctuaryMods\...\<Mod>.dll (the loader
                               reloads on a file-time change), wait for its
                               "Hot-loaded" line, -Settle s (5), counts again, delta

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
    [int]$Settle = 5,
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

function Invoke-Batch([string[]]$Batch) {
    if (-not (Get-Alive)) {
        if (Test-GameRunning) { throw 'the game is running but the probe is not loaded (probe.ps1 install -Force)' }
        throw 'the game is not running (probe.ps1 launch)'
    }
    $id = (Get-Date).ToString('yyyyMMdd-HHmmss-fff')
    $in = Join-Path $Dir "in\$id.cmd"
    $out = Join-Path $Dir "out\$id.txt"
    # Write then rename, so the probe never reads half a batch.
    [IO.File]::WriteAllLines("$in.tmp", $Batch)
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
}

# The probe's `objects` counts, as an ordered type -> count table.
function Get-ObjectCounts([string[]]$Before) {
    $text = Invoke-Batch (@($Before) + 'objects')
    $counts = [ordered]@{}
    foreach ($l in $text -split "`r?`n") { if ($l -match '^\s*([\w.]+)=(\d+)\s*$') { $counts[$Matches[1]] = [int]$Matches[2] } }
    if ($counts.Count -eq 0) { throw "objects returned no counts:`n$text" }
    $counts
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

if ($verb -eq 'leakcheck') {
    # Object counts, a hot reload of one mod, counts again. The loader
    # reloads a DLL whenever its file time changes (it renames the assembly
    # itself), so touching the deployed DLL is a reload without a rebuild.
    $name = $Lines[1]
    if (-not $name) { throw 'usage: probe.ps1 leakcheck <ModAssemblyName> [-Settle 5] -Force' }
    if (-not (Get-Alive)) { throw 'the probe is not loaded (probe.ps1 install, launch)' }
    $dlls = @(Get-ChildItem (Split-Path $ModDir) -Recurse -File -Filter "$name.dll")
    if ($dlls.Count -ne 1) { throw "expected one $name.dll under SanctuaryMods, found $($dlls.Count)" }
    $dll = $dlls[0]
    if (-not $Force) { throw "leakcheck touches $($dll.FullName), which hot-reloads $name in the running game. Ask the user, then pass -Force." }
    $manifest = Get-ChildItem $dll.DirectoryName, (Split-Path $dll.DirectoryName) -Filter mod.json -File -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($manifest -and (Get-Content $manifest.FullName -Raw) -match '"kind"\s*:\s*"gameplay"') {
        "note: $name is a gameplay mod: it reloads only outside a match, and starts only when the lobby picks it."
    }

    $before = Get-ObjectCounts @()
    # Whole seconds: the reload line prints the file time as HH:mm:ss.
    $now = [DateTime]::UtcNow
    $stamp = $now.AddTicks(-($now.Ticks % [TimeSpan]::TicksPerSecond))
    $re = "Hot-loaded \d+ plugin\(s\) from $([regex]::Escape($name))\.dll \(built $($stamp.ToString('HH:mm:ss')) UTC\)"
    # gamelog -Wait only sees lines written after it starts: start it first.
    $job = Start-Job -ScriptBlock { param($s, $r, $t) & $s -Wait $r -Timeout $t } -ArgumentList (Join-Path $PSScriptRoot 'gamelog.ps1'), $re, 60
    Start-Sleep -Seconds 3
    if (-not (Test-GameRunning)) { Remove-Job $job -Force; throw 'the game is not running' }
    $dll.LastWriteTimeUtc = $stamp
    $seen = (Receive-Job $job -Wait -AutoRemoveJob) -join "`n"
    if ($seen -notmatch 'matched in') {
        throw "no reload line for $name within 60 s ($seen). A gameplay mod in a match reloads when it ends; see tools/gamelog.ps1."
    }
    "reloaded: $($seen -replace '^matched in \S+: ', '')"
    $after = Get-ObjectCounts @("wait $Settle")
    '{0,-16} {1,8} {2,8} {3,7}' -f 'type', 'before', 'after', 'delta'
    foreach ($k in $after.Keys) {
        $b = if ($before.Contains($k)) { $before[$k] } else { 0 }
        '{0,-16} {1,8} {2,8} {3,7}' -f $k, $b, $after[$k], ('{0:+0;-0;0}' -f ($after[$k] - $b))
    }
    'A plugin that cleans up after itself comes back to about zero; what it grows by per reload, it leaks. Run it twice: the first reload may load shared assets once.'
    return
}

Invoke-Batch $batch
