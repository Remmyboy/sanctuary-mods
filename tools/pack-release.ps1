<#
.SYNOPSIS
Builds a mod's two release zips, and optionally publishes the GitHub release.

.DESCRIPTION
Every release ships two zips:

  <Mod>-<version>-Standalone.zip   BepInEx + the mod loader + the mod, for a
                                   clean install; extracted into `engine`.
  <Mod>-<version>-ModManager.zip   just the mod, for an install that already
                                   has the Mod Manager.

The version and display name are read from the mod's [BepInPlugin] attribute,
so that attribute is the single source of truth and a release cannot disagree
with what the game reports. Everything else is assembled here: the BepInEx
tree comes from the local game install (an allowlist, so a developer's own
mod configs can never ship), and BepInEx.cfg and SanctuaryMods/README.txt are
vendored next to this script.

.EXAMPLE
  ./tools/pack-release.ps1 -Mod EcoManager -Body notes.txt

.EXAMPLE
  ./tools/pack-release.ps1 -Mod BuildHotkeys -Body body.txt -Notes notes.md -Publish
#>
[CmdletBinding()]
param(
    # Project/assembly name, e.g. EcoManager. Must match the folder.
    [Parameter(Mandatory)][string]$Mod,

    # Plain text describing the mod and what is new. Goes into the README.txt
    # of both zips verbatim, so keep it plain: no markdown, ASCII hyphens.
    [Parameter(Mandatory)][string]$Body,

    # Markdown for the GitHub release body. Defaults to $Body plus the standard
    # zip bullets and compatibility footer.
    [string]$Notes,

    # Overrides the version from [BepInPlugin]. Normally leave this alone.
    [string]$Version,

    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Sanctuary Shattered Sun Playtest\engine',
    [string]$OutDir = 'release',

    # Create the GitHub release. Without this the zips are only built, which is
    # the safe default: publishing is not undoable.
    [switch]$Publish,

    # Which game build the release notes claim to target.
    [string]$BuiltFor = '4 September 2026'
)

$ErrorActionPreference = 'Stop'
# Native exit codes are checked explicitly below. Left to the host preference,
# PowerShell 7.4+ can turn a non-zero exit into a throw, which would make the
# "does this tag already exist" probe fail instead of answering no.
$PSNativeCommandUseErrorActionPreference = $false
$repo = Split-Path $PSScriptRoot -Parent
$tools = $PSScriptRoot

function Fail($m) { Write-Host "pack-release: $m" -ForegroundColor Red; exit 1 }

# ---- identity, straight from the source of truth --------------------------
$src = Get-ChildItem "$repo\$Mod" -Filter *.cs -Recurse -ErrorAction SilentlyContinue |
    Select-String -Pattern '\[BepInPlugin\("[^"]+",\s*"([^"]+)",\s*"([^"]+)"\)\]' |
    Select-Object -First 1
if (-not $src) { Fail "no [BepInPlugin] found under $Mod - is that the right project name?" }
$display = $src.Matches[0].Groups[1].Value
if (-not $Version) { $Version = $src.Matches[0].Groups[2].Value }
Write-Host "Packing $display $Version ($Mod)" -ForegroundColor Cyan

if (-not (Test-Path $Body)) { Fail "body file not found: $Body" }
$bodyText = (Get-Content $Body -Raw).TrimEnd()
if ($bodyText -match '\*\*|\[.+\]\(') {
    Write-Host "  note: -Body looks like markdown; it is copied into README.txt verbatim." -ForegroundColor Yellow
}

# ---- build ----------------------------------------------------------------
# Staged away from the game folder so packing never disturbs a running game.
$stage = Join-Path ([System.IO.Path]::GetTempPath()) "sanctuary-pack-$PID"
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $stage | Out-Null
Write-Host "  building Release..."
& dotnet build "$repo\SanctuaryMods.sln" -c Release -v q --nologo -p:DeployPath=$stage | Out-Null
if ($LASTEXITCODE -ne 0) { Fail 'build failed' }
foreach ($need in "$Mod.dll", 'ModLoader.dll') {
    if (-not (Test-Path "$stage\$need")) { Fail "build produced no $need" }
}

$outPath = if ([System.IO.Path]::IsPathRooted($OutDir)) { $OutDir } else { Join-Path $repo $OutDir }
New-Item -ItemType Directory -Force -Path $outPath | Out-Null

function New-Zip($dir, $zip) {
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path "$dir\*" -DestinationPath $zip -CompressionLevel Optimal
}

# ---- ModManager add-in ----------------------------------------------------
$mm = "$stage\mm"
New-Item -ItemType Directory -Force -Path "$mm\SanctuaryMods\$Mod" | Out-Null
Copy-Item "$stage\$Mod.dll" "$mm\SanctuaryMods\$Mod\$Mod.dll"
$t = "Sanctuary $display $Version - Mod Manager add-in"
@"
$t
$('=' * $t.Length)

This is the add-in version: just the mod, for an install that already has
the Sanctuary Mod Manager (which brings the mod loader). If you don't have
that yet, either install the Mod Manager first or use the Standalone zip of
this mod instead, which includes everything.

INSTALL
1. Extract this zip into your Sanctuary 'engine' folder (the one with
   Sanctuary.exe), so the mod lands in SanctuaryMods\$Mod\.
2. That's it. If the game is already running the loader picks it up on the
   spot; it shows up under UI Mods on the Mods page, where it can be
   switched off and on and its settings changed.

WHAT IT DOES
$bodyText

UNINSTALL
Delete SanctuaryMods\$Mod, or switch it off on the Mods page.
"@ | Set-Content "$mm\README.txt" -NoNewline
$mmZip = "$outPath\$Mod-$Version-ModManager.zip"
New-Zip $mm $mmZip

# ---- Standalone -----------------------------------------------------------
# An allowlist, not a copy of the install: BepInEx\config there holds the
# developer's own per-mod settings, and none of that belongs in a release.
$sa = "$stage\sa"
New-Item -ItemType Directory -Force -Path "$sa\BepInEx\core", "$sa\BepInEx\config", "$sa\BepInEx\plugins", "$sa\SanctuaryMods\$Mod" | Out-Null
foreach ($f in '.doorstop_version', 'doorstop_config.ini', 'winhttp.dll') {
    if (-not (Test-Path "$GamePath\$f")) { Fail "missing $f in $GamePath - is BepInEx installed there?" }
    Copy-Item "$GamePath\$f" "$sa\$f"
}
Copy-Item "$GamePath\BepInEx\core\*" "$sa\BepInEx\core\" -Recurse
Copy-Item "$tools\BepInEx.cfg" "$sa\BepInEx\config\BepInEx.cfg"
Copy-Item "$tools\SanctuaryMods-README.txt" "$sa\SanctuaryMods\README.txt"
Copy-Item "$stage\ModLoader.dll" "$sa\BepInEx\plugins\ModLoader.dll"
Copy-Item "$stage\$Mod.dll" "$sa\SanctuaryMods\$Mod\$Mod.dll"
$t = "Sanctuary $display $Version - Standalone"
@"
$t
$('=' * $t.Length)

$bodyText

INSTALL
1. Extract this zip into your Sanctuary 'engine' folder, so that
   winhttp.dll sits next to Sanctuary.exe. Default location:
   $GamePath
2. Launch the game. That's it. The mod appears under UI Mods on the Mods
   page (the cube icon in the menu's sidebar, or F8), where it can be
   switched off and on and its settings changed.

WHAT GOES WHERE
  BepInEx\plugins\ModLoader.dll        the loader - BepInEx starts it
  SanctuaryMods\$Mod\$Mod.dll  the mod; the loader loads every DLL
                                       under SanctuaryMods and reloads it
                                       when the file changes
Other mods from the same author install the same way: drop their folder
into SanctuaryMods.

ALREADY RUNNING BEPINEX?
Copy BepInEx\plugins\ModLoader.dll and the SanctuaryMods folder from this
zip into your engine folder - AND make sure BepInEx\config\BepInEx.cfg has
    HideManagerGameObject = true
under [Chainloader]. Sanctuary destroys BepInEx's manager object after
start-up otherwise, and every plugin on it stops running right after it
loads. This zip ships that setting; a BepInEx you installed yourself
defaults it to false.

MULTIPLAYER
This is a client-side UI mod: it never touches the game's Lua tree or the
simulation, so a modded client stays lobby-compatible with unmodded players.

UNINSTALL
Delete SanctuaryMods\$Mod, or switch it off on the Mods page.
"@ | Set-Content "$sa\README.txt" -NoNewline
$saZip = "$outPath\$Mod-$Version-Standalone.zip"
New-Zip $sa $saZip

# ---- verify, so a broken zip cannot reach a release -----------------------
Add-Type -AssemblyName System.IO.Compression.FileSystem
function Assert-Entries($zip, $expected) {
    $names = [IO.Compression.ZipFile]::OpenRead($zip).Entries.FullName
    foreach ($e in $expected) {
        if ($names -notcontains $e) { Fail "$([IO.Path]::GetFileName($zip)) is missing $e" }
    }
    # A developer's own settings leaking into a release would be silent, so
    # check for it rather than trusting the allowlist.
    $stray = $names | Where-Object { $_ -like 'BepInEx/config/*' -and $_ -ne 'BepInEx/config/BepInEx.cfg' }
    if ($stray) { Fail "$([IO.Path]::GetFileName($zip)) ships stray config: $($stray -join ', ')" }
}
Assert-Entries $mmZip @("SanctuaryMods/$Mod/$Mod.dll", 'README.txt')
Assert-Entries $saZip @("SanctuaryMods/$Mod/$Mod.dll", 'SanctuaryMods/README.txt', 'README.txt',
    'BepInEx/plugins/ModLoader.dll', 'BepInEx/config/BepInEx.cfg', 'winhttp.dll',
    'doorstop_config.ini', '.doorstop_version')

Get-ChildItem $outPath -Filter "$Mod-$Version-*.zip" |
    ForEach-Object { "  {0,-42} {1,9:N0} bytes" -f $_.Name, $_.Length }
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue

# ---- publish --------------------------------------------------------------
$tag = "$Mod-$Version"
if (-not $Publish) {
    Write-Host "Not published. To publish:" -ForegroundColor Yellow
    Write-Host "  ./tools/pack-release.ps1 -Mod $Mod -Body $Body -Publish"
    return
}

if (gh release view $tag 2>$null) { Fail "$tag already exists - bump [BepInPlugin] first" }
$notesText = if ($Notes) { (Get-Content $Notes -Raw).TrimEnd() } else {
    @"
$bodyText

- **Standalone** - everything: BepInEx, the Mod Loader and the mod. Extract into the game's ``engine`` folder.
- **ModManager** - just the mod, for an install that already has the Mod Manager. Extract into ``engine``; it appears under UI Mods.

Client-side only: never touches the Lua tree or the simulation, so a modded client stays lobby-compatible with unmodded players. Built for the game update of $BuiltFor.
"@
}
$notesFile = [IO.Path]::GetTempFileName()
$notesText | Set-Content $notesFile -NoNewline
gh release create $tag --target main --title "$display $Version" --notes-file $notesFile $saZip $mmZip
Remove-Item $notesFile -Force
