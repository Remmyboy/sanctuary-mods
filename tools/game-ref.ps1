<#
.SYNOPSIS
  Per-build reference copies of the game, for reading and for post-patch checks.

.DESCRIPTION
  game-ref.ps1 snapshot [-Force]   snapshot the installed build (Steam build id) if not done yet
  game-ref.ps1 list                snapshots, newest last
  game-ref.ps1 path [build]        a snapshot's folder (default: the installed build's)
  game-ref.ps1 diff [old] [new]    what a patch changed: API lines, enum values, Lua files
  game-ref.ps1 check [build]       mod source vs a snapshot (and the one before it)

  A snapshot lives in %LOCALAPPDATA%\SanctuaryRef\<buildid>\ and holds
    cs\<Assembly>\   ilspycmd's decompiled project, one file per type (grep it)
    lua\             a copy of engine\LJ\lua (client, common, host, AI, docs)
    api.txt          every type/member of the game assemblies, one line each
    api-engine.txt   the same for Unity and other third-party assemblies
    info.txt         build id, date, Trebuchet.dll timestamp
  It is the developers' code: it stays on this machine, never in the repo.

  After a game patch: snapshot, then diff, then check. Fix what check
  reports, rebuild every mod (enum ints are baked into built DLLs) and
  release the ones whose behaviour depends on what moved.
#>
param(
    [Parameter(Position = 0)][string]$Command = 'help',
    [Parameter(Position = 1)][string]$A,
    [Parameter(Position = 2)][string]$B,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$Root = Join-Path $env:LOCALAPPDATA 'SanctuaryRef'
$Repo = Split-Path $PSScriptRoot -Parent
$Steam = 'C:\Program Files (x86)\Steam\steamapps'
$Game = Join-Path $Steam 'common\Sanctuary Shattered Sun Playtest\engine'
$Managed = Join-Path $Game 'Sanctuary_Data\Managed'
$Decompile = @('Trebuchet', 'Michsky.UI.Beam')

function Get-InstalledBuild {
    $acf = Get-Content (Join-Path $Steam 'appmanifest_4511930.acf') -Raw
    if ($acf -notmatch '"buildid"\s+"(\d+)"') { throw 'no buildid in appmanifest_4511930.acf' }
    $Matches[1]
}

function Get-Snapshots {
    if (-not (Test-Path $Root)) { return @() }
    Get-ChildItem $Root -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'api.txt') } |
        Sort-Object { [long]$_.Name }
}

function Resolve-Snapshot([string]$Build) {
    if (-not $Build) { $Build = Get-InstalledBuild }
    $dir = Join-Path $Root $Build
    if (-not (Test-Path (Join-Path $dir 'api.txt'))) { throw "no snapshot for build $Build (game-ref.ps1 snapshot)" }
    $dir
}

function Get-Previous([string]$Dir) {
    $all = @(Get-Snapshots)
    $i = [array]::IndexOf(@($all.FullName), $Dir)
    if ($i -gt 0) { $all[$i - 1].FullName } else { $null }
}

function Invoke-GameRef {
    $proj = Join-Path $PSScriptRoot 'GameRef\GameRef.csproj'
    & dotnet build $proj -c Release -v:quiet --nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'GameRef build failed' }
    & dotnet (Join-Path $PSScriptRoot 'GameRef\bin\Release\net8.0\GameRef.dll') @args
    $script:GameRefExit = $LASTEXITCODE
}

switch ($Command) {
    'snapshot' {
        $build = Get-InstalledBuild
        $dir = Join-Path $Root $build
        if ((Test-Path (Join-Path $dir 'api.txt')) -and -not $Force) { "build $build already snapshotted: $dir"; return }
        New-Item -ItemType Directory -Force $dir | Out-Null
        "snapshotting build $build to $dir"

        'copying Lua...'
        $lua = Join-Path $dir 'lua'
        if (Test-Path $lua) { Remove-Item $lua -Recurse -Force }
        Copy-Item (Join-Path $Game 'LJ\lua') $lua -Recurse

        'writing API listings...'
        Invoke-GameRef api $Managed $dir (Join-Path $Game 'BepInEx\core')
        if ($GameRefExit -ne 0) { throw 'GameRef api failed' }

        foreach ($asm in $Decompile) {
            "decompiling $asm (a minute or two)..."
            $out = Join-Path $dir "cs\$asm"
            if (Test-Path $out) { Remove-Item $out -Recurse -Force }
            & ilspycmd -p -o $out -r $Managed (Join-Path $Managed "$asm.dll") | Out-Null
            if ($LASTEXITCODE -ne 0) { Write-Warning "ilspycmd failed on $asm" }
        }

        $treb = Get-Item (Join-Path $Managed 'Trebuchet.dll')
        @(
            "build $build",
            "snapshotted $(Get-Date -Format 'yyyy-MM-dd HH:mm')",
            "Trebuchet.dll $($treb.LastWriteTime.ToString('yyyy-MM-dd HH:mm')) $($treb.Length) bytes"
        ) | Set-Content (Join-Path $dir 'info.txt')
        "done: $dir"
        $prev = Get-Previous $dir
        if ($prev) { "previous snapshot: $(Split-Path $prev -Leaf); next: game-ref.ps1 diff, then game-ref.ps1 check" }
    }
    'list' {
        $installed = Get-InstalledBuild
        foreach ($s in Get-Snapshots) {
            $info = (Get-Content (Join-Path $s.FullName 'info.txt') -ErrorAction SilentlyContinue) -join '; '
            "$($s.Name)$(if ($s.Name -eq $installed) { ' (installed)' })  $info"
        }
        if (-not (Test-Path (Join-Path $Root "$installed\api.txt"))) { "installed build $installed has no snapshot yet" }
    }
    'path' { Resolve-Snapshot $A }
    'diff' {
        $new = if ($B) { Resolve-Snapshot $B } elseif ($A) { $null } else { Resolve-Snapshot '' }
        $old = if ($A) { Resolve-Snapshot $A } else { Get-Previous $new }
        if ($A -and -not $B) { $new = Resolve-Snapshot '' }
        if (-not $old) { throw 'only one snapshot so far: nothing to diff against' }
        "diff $(Split-Path $old -Leaf) -> $(Split-Path $new -Leaf)"
        $o = Get-Content (Join-Path $old 'api.txt')
        $n = Get-Content (Join-Path $new 'api.txt')
        $cmp = Compare-Object $o $n
        $removed = @($cmp | Where-Object SideIndicator -eq '<=' | ForEach-Object InputObject)
        $added = @($cmp | Where-Object SideIndicator -eq '=>' | ForEach-Object InputObject)
        "`n== Enum values changed or removed (released DLLs carry the old ints)"
        $removed | Where-Object { $_ -like 'E *' } | ForEach-Object {
            $key = ($_ -split ' = ')[0]
            $now = $added | Where-Object { $_ -like "$key = *" }
            "  $_  ->  $(if ($now) { ($now -split ' = ')[1] } else { 'REMOVED' })"
        }
        "`n== Types removed"
        $removed | Where-Object { $_ -like 'T *' } | ForEach-Object { "  $_" }
        "`n== Members removed or changed ($(@($removed | Where-Object { $_ -match '^[FPMV] ' }).Count))"
        $removed | Where-Object { $_ -match '^[FPMV] ' } | ForEach-Object { "  - $_" }
        "`n== Types added"
        $added | Where-Object { $_ -like 'T *' } | ForEach-Object { "  $_" }
        "`n== Members added: $(@($added | Where-Object { $_ -match '^[FPMV] ' }).Count) (see the full list with Compare-Object on api.txt)"
        "`n== Lua files"
        & git diff --no-index --stat=200 (Join-Path $old 'lua') (Join-Path $new 'lua')
        "`nfull Lua diff: git diff --no-index `"$old\lua`" `"$new\lua`""
    }
    'check' {
        $snap = Resolve-Snapshot $A
        $prev = Get-Previous $snap
        $args2 = @('check', $Repo, $snap) + $(if ($prev) { @($prev) } else { @() })
        Invoke-GameRef @args2
        exit $GameRefExit
    }
    default { Get-Help $PSCommandPath -Detailed }
}
