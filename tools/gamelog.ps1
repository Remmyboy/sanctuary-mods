<#
.SYNOPSIS
  What the game and the mods logged, without the noise.

.DESCRIPTION
  gamelog.ps1                  game state, mods loaded, problems, recent mod lines
  gamelog.ps1 -Mod Eco         only lines whose source or text matches "Eco"
  gamelog.ps1 -Tail 100        more recent lines (default 30)
  gamelog.ps1 -Previous        the previous launch's Player.log (crashes land there)
  gamelog.ps1 -Wait 'regex'    block until a new line matches (default 120 s, -Timeout)
  gamelog.ps1 -Deployed        each deployed mod DLL: build time, version, which worktree built it
  gamelog.ps1 -Sessions        the past game sessions kept in BepInEx\LogArchive (Mod Manager's [Logs] Keep)
  gamelog.ps1 -Session 1       the same report for a kept session: 0 = this one, 1 = the one before...,
                               or its start time (2026-10-01_21-16), from both its logs

  The Mod Manager's log archive (BepInEx\LogArchive, when [Logs] Keep is on)
  holds <session>_bepinex.log (the whole session's BepInEx log, written as it
  goes) and <session>_player.log (that session's Player.log, saved at the next
  launch), <session> being the game's start time.

  Two logs matter and neither is complete:
    BepInEx\LogOutput.log   plugin Logger lines (overwritten every launch)
    LocalLow\Enhearten Media PTY\Sanctuary\Player.log
                            Unity's log: exceptions thrown in mod code, Lua
                            Warn/Error output, the game's own errors. Rotates
                            to Player-prev.log on launch. (The "Sanctuary
                            Shattered Sun" folder beside it is a stale demo log.)
  Crash dumps: %TEMP%\Enhearten Media PTY\Sanctuary\Crashes.
#>
param(
    [string]$Mod,
    [int]$Tail = 30,
    [switch]$Previous,
    [string]$Wait,
    [int]$Timeout = 120,
    [switch]$Deployed,
    [switch]$Sessions,
    [string]$Session
)

$ErrorActionPreference = 'Stop'
$Game = 'C:\Program Files (x86)\Steam\steamapps\common\Sanctuary Shattered Sun Playtest\engine'
$BepLog = Join-Path $Game 'BepInEx\LogOutput.log'
$LocalLow = Join-Path $env:USERPROFILE 'AppData\LocalLow\Enhearten Media PTY\Sanctuary'
$PlayerLog = Join-Path $LocalLow ($(if ($Previous) { 'Player-prev.log' } else { 'Player.log' }))
$Repo = Split-Path $PSScriptRoot -Parent
# The main checkout, whose .claude\worktrees holds every worktree.
$MainCheckout = if ($Repo -match '^(.*?)\\\.claude\\worktrees\\') { $Matches[1] } else { $Repo }

# Lines that are always there and never the problem.
$Noise = @(
    'Unable to start Unity log writer',
    'persistentEffect1',
    'The referenced script \(Unknown\) on this Behaviour is missing',
    'Fallback handler could not load library',
    'already registered'
)
$NoiseRe = ($Noise -join '|')

# The Mod Manager's archive of past sessions, newest first.
$Archive = Join-Path $Game 'BepInEx\LogArchive'
function Get-Sessions {
    if (-not (Test-Path $Archive)) { return @() }
    Get-ChildItem $Archive -File | Where-Object { $_.Name -match '^(\d{4}-\d\d-\d\d_\d\d-\d\d-\d\d)_(bepinex|player)\.log$' } |
        Group-Object { $_.Name.Substring(0, 19) } | Sort-Object Name -Descending |
        ForEach-Object {
            $files = $_.Group
            [pscustomobject]@{
                Stamp   = $_.Name
                BepInEx = $files | Where-Object { $_.Name -like '*_bepinex.log' } | Select-Object -First 1
                Player  = $files | Where-Object { $_.Name -like '*_player.log' } | Select-Object -First 1
            }
        }
}

if ($Sessions) {
    $all = @(Get-Sessions)
    if ($all.Count -eq 0) { "No kept sessions in $Archive (Mod Manager: [Logs] Keep = true starts it)."; return }
    "Kept sessions in $Archive (newest first):"
    for ($i = 0; $i -lt $all.Count; $i++) {
        $s = $all[$i]
        $kb = { param($f) if ($f) { '{0,7:N0} KB' -f ($f.Length / 1KB) } else { '      -   ' } }
        '  {0,2}  {1}  bepinex {2}  player {3}' -f $i, $s.Stamp, (& $kb $s.BepInEx), (& $kb $s.Player)
    }
    '  (a session''s player log is saved when the game next starts)'
    return
}

if ($Session) {
    $all = @(Get-Sessions)
    $pick = if ($Session -match '^\d+$' -and [int]$Session -lt $all.Count) { $all[[int]$Session] }
            else { $all | Where-Object { $_.Stamp -like "$Session*" } | Select-Object -First 1 }
    if (-not $pick) { "No kept session '$Session'; gamelog.ps1 -Sessions lists them."; exit 1 }
    $BepLog = if ($pick.BepInEx) { $pick.BepInEx.FullName } else { Join-Path $Archive "$($pick.Stamp)_bepinex.log" }
    $PlayerLog = if ($pick.Player) { $pick.Player.FullName } else { Join-Path $Archive "$($pick.Stamp)_player.log" }
}

function Read-Lines([string]$Path) {
    if (-not (Test-Path $Path)) { return @() }
    # The game holds the file open; read through a shared stream.
    $fs = [IO.File]::Open($Path, 'Open', 'Read', 'ReadWrite')
    try { (New-Object IO.StreamReader($fs)).ReadToEnd() -split "`r?`n" } finally { $fs.Dispose() }
}

if ($Wait) {
    $start = @{ $BepLog = @(Read-Lines $BepLog).Count; $PlayerLog = @(Read-Lines $PlayerLog).Count }
    $deadline = (Get-Date).AddSeconds($Timeout)
    while ((Get-Date) -lt $deadline) {
        foreach ($f in @($BepLog, $PlayerLog)) {
            $lines = @(Read-Lines $f)
            if ($lines.Count -lt $start[$f]) { $start[$f] = 0 }   # rotated by a relaunch
            $hit = $lines | Select-Object -Skip $start[$f] | Where-Object { $_ -match $Wait } | Select-Object -First 1
            if ($hit) { "matched in $(Split-Path $f -Leaf): $hit"; exit 0 }
        }
        Start-Sleep -Milliseconds 500
    }
    "no line matching '$Wait' within $Timeout s"
    exit 1
}

if ($Deployed) {
    $dlls = @(Get-ChildItem (Join-Path $Game 'SanctuaryMods') -Recurse -Filter *.dll -ErrorAction SilentlyContinue) +
            @(Get-ChildItem (Join-Path $Game 'BepInEx\plugins') -Filter *.dll -ErrorAction SilentlyContinue)
    # Every build output in the main checkout and its worktrees, by file hash.
    $builds = @{}
    Get-ChildItem $MainCheckout -Recurse -Filter *.dll -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\bin\\' } |
        ForEach-Object { $h = (Get-FileHash $_.FullName -Algorithm SHA1).Hash; if (-not $builds[$h]) { $builds[$h] = $_.FullName } }
    $loaded = @{}
    foreach ($l in Read-Lines $BepLog) {
        if ($l -match 'Hot-loaded (\d+) plugin\(s\) from (\S+?)\.dll \(built ([\d:]+) UTC\)') { $loaded[$Matches[2]] = "loaded (built $($Matches[3]) UTC)" }
    }
    # The SDK stamps the commit into ProductVersion (1.0.0+<sha>).
    & git -C $Repo fetch -q origin main 2>$null
    '{0,-28} {1,-9} {2,-8} {3,-16}  {4}  {5}' -f 'DLL', 'commit', 'on main', 'file time', 'built by', 'this launch'
    foreach ($d in $dlls | Sort-Object Name) {
        $v = $d.VersionInfo
        $sha = if ($v.ProductVersion -match '\+([0-9a-f]{7,40})') { $Matches[1] } else { $null }
        $onMain = if (-not $sha) { '?' } else {
            & git -C $Repo merge-base --is-ancestor $sha origin/main 2>$null
            if ($LASTEXITCODE -eq 0) { 'yes' } elseif ($LASTEXITCODE -eq 1) { 'NO' } else { 'unknown' }
        }
        $h = (Get-FileHash $d.FullName -Algorithm SHA1).Hash
        $from = if ($builds[$h]) {
            $rel = $builds[$h].Substring($MainCheckout.Length).TrimStart('\')
            if ($rel -match '^\.claude\\worktrees\\([^\\]+)') { "worktree $($Matches[1])" } else { 'main checkout' }
        } else { 'no local build (release zip, or rebuilt since)' }
        $state = if ($loaded[$d.BaseName]) { $loaded[$d.BaseName] } else { '' }
        '{0,-28} {1,-9} {2,-8} {3:yyyy-MM-dd HH:mm}  {4}  {5}' -f $d.Name, $(if ($sha) { $sha.Substring(0, 7) } else { '-' }), $onMain, $d.LastWriteTime, $from, $state
    }
    return
}

# --- the default report
$proc = Get-Process -Name Sanctuary -ErrorAction SilentlyContinue
if ($Session) { "Kept session $($pick.Stamp)" }
elseif ($proc) { "Game: RUNNING (pid $($proc.Id), started $($proc.StartTime.ToString('HH:mm:ss')))" } else { 'Game: not running' }
foreach ($f in @($BepLog, $PlayerLog)) {
    if (Test-Path $f) { "  {0}  {1:HH:mm:ss}" -f $f, (Get-Item $f).LastWriteTime } else { "  (missing) $f" }
}

$bep = @(Read-Lines $BepLog)
$player = @(Read-Lines $PlayerLog)

"`n== Mods loaded"
$bep | Where-Object { $_ -match 'Hot-loaded|Mod API .*ready|Loading \[|plugin\(s\) from' -and $_ -notmatch $NoiseRe } |
    ForEach-Object { '  ' + ($_ -replace '^\[(\w+)\s*:\s*([^\]]+)\]\s*', '') } | Select-Object -Last 40

"`n== Problems (deduplicated, both logs)"
$problems = [ordered]@{}
for ($i = 0; $i -lt $bep.Count; $i++) {
    $l = $bep[$i]
    if ($l -match '^\[(Error|Warning|Fatal)' -and $l -notmatch $NoiseRe) { $problems["BepInEx: $l"]++ }
}
for ($i = 0; $i -lt $player.Count; $i++) {
    $l = $player[$i]
    if ($l -match $NoiseRe) { continue }
    # "is invalid": the engine refusing a Lua call about an entity that is gone.
    if ($l -match 'Exception|LUA ERROR|Lua error|\[string ".*"\]:\d+:|^\s*Error|error:|NullReference|Crash!!!|is invalid\.') {
        # Keep the first two frames of a stack so the source is visible.
        $frames = @($player[($i + 1)..([Math]::Min($i + 2, $player.Count - 1))] | Where-Object { $_ -match '^\s*(at |\S+\s*\(|\S+:\S+ \()' })
        $key = "Player.log: $l" + $(if ($frames) { "`n      " + ($frames -join "`n      ") } else { '' })
        $problems[$key]++
    }
}
if ($problems.Count -eq 0) { '  none' }
foreach ($k in $problems.Keys) { '  ({0}x) {1}' -f $problems[$k], $k }

"`n== Recent mod lines$(if ($Mod) { " matching '$Mod'" })"
$bep | Where-Object { $_ -match '^\[' -and $_ -notmatch $NoiseRe -and (-not $Mod -or $_ -match $Mod) } |
    Select-Object -Last $Tail | ForEach-Object { '  ' + $_ }
