<#
.SYNOPSIS
  Where Sanctuary is installed. Dot-source it: . "$PSScriptRoot\game-path.ps1"

  Sets $SteamApps (the steamapps folder that holds appmanifest_4511930.acf)
  and $GameDir (...\Sanctuary Shattered Sun Playtest\engine). The game can
  sit in any Steam library (it moved from C: to D:\SteamLibrary in Nov 2026),
  so the libraries come from Steam's libraryfolders.vdf. $env:SANCTUARY_GAME
  (the engine folder) overrides it.
#>
function Find-SanctuarySteamApps {
    $steam = 'C:\Program Files (x86)\Steam'
    try {
        $reg = Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction Stop
        if ($reg.SteamPath) { $steam = $reg.SteamPath -replace '/', '\' }
    } catch { Write-Verbose "no Steam registry key: $_" }
    $libs = @(Join-Path $steam 'steamapps')
    $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
    if (Test-Path $vdf) {
        foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"')) {
            $libs += Join-Path ($m.Groups[1].Value -replace '\\\\', '\') 'steamapps'
        }
    }
    foreach ($l in $libs) {
        if (Test-Path (Join-Path $l 'appmanifest_4511930.acf')) { return $l }
    }
    throw "Sanctuary (app 4511930) is in none of the Steam libraries: $($libs -join ', ')"
}

if ($env:SANCTUARY_GAME) {
    $GameDir = $env:SANCTUARY_GAME
    $SteamApps = Split-Path (Split-Path (Split-Path $GameDir -Parent) -Parent) -Parent
} else {
    $SteamApps = Find-SanctuarySteamApps
    $GameDir = Join-Path $SteamApps 'common\Sanctuary Shattered Sun Playtest\engine'
}

# The game's Lua tree: engine\lua since build 26880003 (24 Nov 2026),
# engine\LJ\lua before (LJ\lua may linger, holding only old AI logs).
$GameLua = Join-Path $GameDir 'lua'
if (-not (Test-Path (Join-Path $GameLua 'client'))) { $GameLua = Join-Path $GameDir 'LJ\lua' }
