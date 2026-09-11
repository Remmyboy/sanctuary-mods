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

Provenance: the zips are always built from a clean checkout of the committed
HEAD (a temporary git worktree), never from the working tree, and the source
revision the SDK stamps into each DLL is checked against that commit. With
-Publish the script also requires HEAD to be origin/main with no uncommitted
changes to tracked files, tags exactly that commit, pushes the tag, and only
then creates the release from the existing tag. A release's binaries, its tag
and main therefore always name the same commit.

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
# "does this tag already exist" probes fail instead of answering no.
$PSNativeCommandUseErrorActionPreference = $false
$repo = Split-Path $PSScriptRoot -Parent

# Staged away from the game folder so packing never disturbs a running game;
# the clean checkout sits beside it.
$stage = Join-Path ([System.IO.Path]::GetTempPath()) "sanctuary-pack-$PID"
$src = "$stage-src"
$notesFile = $null

# Everything temporary goes however the script ends. The worktree is also
# recorded inside the repository, so it is removed through git.
function Remove-Temp {
    if ($script:notesFile -and (Test-Path $script:notesFile)) { Remove-Item $script:notesFile -Force -ErrorAction SilentlyContinue }
    if (Test-Path $src) {
        & git -C $repo worktree remove --force $src 2>$null | Out-Null
        Remove-Item $src -Recurse -Force -ErrorAction SilentlyContinue
    }
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
}

function Fail($m) { Write-Host "pack-release: $m" -ForegroundColor Red; Remove-Temp; exit 1 }

# A git command whose failure ends the script; returns its trimmed output.
function Invoke-Git([string]$What, [string[]]$GitArgs) {
    $out = & git -C $repo @GitArgs
    if ($LASTEXITCODE -ne 0) { Fail "$What failed (git $($GitArgs -join ' '))" }
    return (($out | Out-String).Trim())
}

if (-not (Test-Path $Body)) { Fail "body file not found: $Body" }
$bodyText = (Get-Content $Body -Raw).TrimEnd()
if ($bodyText -match '\*\*|\[.+\]\(') {
    Write-Host "  note: -Body looks like markdown; it is copied into README.txt verbatim." -ForegroundColor Yellow
}
if ($Notes -and -not (Test-Path $Notes)) { Fail "notes file not found: $Notes" }

# ---- the commit being released ----------------------------------------------
$head = Invoke-Git 'reading HEAD' @('rev-parse', 'HEAD')
$short = $head.Substring(0, 7)
$dirty = Invoke-Git 'reading the working tree status' @('status', '--porcelain', '--untracked-files=no')
if ($dirty) {
    if ($Publish) { Fail "uncommitted changes to tracked files; commit them and land them on main before publishing:`n$dirty" }
    Write-Host "  note: uncommitted changes are not in this build; it is built from HEAD $short." -ForegroundColor Yellow
}
if ($Publish) {
    Invoke-Git 'fetching origin/main' @('fetch', '--quiet', 'origin', 'main') | Out-Null
    $originMain = Invoke-Git 'reading origin/main' @('rev-parse', 'refs/remotes/origin/main')
    if ($originMain -ne $head) { Fail "HEAD $head is not origin/main $originMain; push the release commit to main first" }
}

Remove-Temp
New-Item -ItemType Directory -Force -Path $stage | Out-Null
Invoke-Git 'checking out a clean copy of HEAD' @('worktree', 'add', '--detach', $src, $head) | Out-Null
$tools = "$src\tools"

# ---- identity, straight from the source of truth --------------------------
$plugin = Get-ChildItem "$src\$Mod" -Filter *.cs -Recurse -ErrorAction SilentlyContinue |
    Select-String -Pattern '\[BepInPlugin\("[^"]+",\s*"([^"]+)",\s*"([^"]+)"\)\]' |
    Select-Object -First 1
if (-not $plugin) { Fail "no [BepInPlugin] found under $Mod at $short - is that the right project name, and is it committed?" }
$display = $plugin.Matches[0].Groups[1].Value
if (-not $Version) { $Version = $plugin.Matches[0].Groups[2].Value }
Write-Host "Packing $display $Version ($Mod) from $short" -ForegroundColor Cyan

# ---- build ----------------------------------------------------------------
Write-Host "  building Release..."
$buildLog = & dotnet build "$src\SanctuaryMods.sln" -c Release -v q --nologo -p:DeployPath=$stage
if ($LASTEXITCODE -ne 0) {
    $buildLog | Select-Object -Last 40 | ForEach-Object { Write-Host "    $_" }
    Fail 'build failed'
}
foreach ($need in "$Mod.dll", 'ModLoader.dll') {
    if (-not (Test-Path "$stage\$need")) { Fail "build produced no $need" }
}

# The SDK stamps the commit it built from into every DLL's product version
# ("1.0.0+<sha>"). Checking it is what ties the zips to the tag: a DLL built
# from anything else, a dirty tree or a stale output, cannot pass.
foreach ($dll in @("$Mod.dll", 'ModLoader.dll') | Select-Object -Unique) {
    $pv = [Diagnostics.FileVersionInfo]::GetVersionInfo("$stage\$dll").ProductVersion
    $rev = if ($pv -match '\+([0-9a-f]{40})') { $Matches[1] } else { '(none)' }
    if ($rev -ne $head) { Fail "$dll carries source revision $rev (product version '$pv'), expected $head" }
}
Write-Host "  DLLs carry source revision $short"

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
# A mod may ship data next to its DLL (SanctuaryHud's alert sounds live in
# <Mod>\sounds); anything under the project's sounds folder goes along.
if (Test-Path "$src\$Mod\sounds") { Copy-Item "$src\$Mod\sounds" "$mm\SanctuaryMods\$Mod\sounds" -Recurse }
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
if (Test-Path "$src\$Mod\sounds") { Copy-Item "$src\$Mod\sounds" "$sa\SanctuaryMods\$Mod\sounds" -Recurse }
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

MULTIPLAYER AND TRUST
This mod runs client-side: it never changes the game's Lua files or the
simulation, so a modded client stays lobby-compatible with unmodded
players. That is compatibility, not a safety check. Like every BepInEx
plugin, the DLL runs as full-trust code inside the game with your Windows
account's permissions, so only install mods from a source you trust.

UNINSTALL
Delete SanctuaryMods\$Mod, or switch it off on the Mods page.
"@ | Set-Content "$sa\README.txt" -NoNewline
$saZip = "$outPath\$Mod-$Version-Standalone.zip"
New-Zip $sa $saZip

# ---- verify, so a broken zip cannot reach a release -----------------------
Add-Type -AssemblyName System.IO.Compression.FileSystem
function Assert-Entries($zip, $expected) {
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    try { $names = $archive.Entries.FullName } finally { $archive.Dispose() }
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

# ---- publish --------------------------------------------------------------
$tag = "$Mod-$Version"
if (-not $Publish) {
    Remove-Temp
    Write-Host "Not published. To publish, from main with everything committed and pushed:" -ForegroundColor Yellow
    Write-Host "  ./tools/pack-release.ps1 -Mod $Mod -Body $Body -Publish"
    return
}

# A tag is never moved or reused: someone may already have downloaded it.
& git -C $repo rev-parse --quiet --verify "refs/tags/$tag" | Out-Null
if ($LASTEXITCODE -eq 0) { Fail "tag $tag already exists locally - bump [BepInPlugin] first" }
& git -C $repo ls-remote --exit-code --tags origin "refs/tags/$tag" | Out-Null
if ($LASTEXITCODE -eq 0) { Fail "tag $tag already exists on origin - bump [BepInPlugin] first" }
if ($LASTEXITCODE -ne 2) { Fail "could not list origin's tags (git exit $LASTEXITCODE)" }
& gh release view $tag 2>$null | Out-Null
if ($LASTEXITCODE -eq 0) { Fail "release $tag already exists - bump [BepInPlugin] first" }

$notesText = if ($Notes) { (Get-Content $Notes -Raw).TrimEnd() } else {
    @"
$bodyText

- **Standalone** - everything: BepInEx, the Mod Loader and the mod. Extract into the game's ``engine`` folder.
- **ModManager** - just the mod, for an install that already has the Mod Manager. Extract into ``engine``; it appears under UI Mods.

Client-side only: never changes the Lua files or the simulation, so a modded client stays lobby-compatible with unmodded players. Like any BepInEx plugin it runs as full-trust code inside the game, so only install it from a source you trust. Built for the game update of $BuiltFor.
"@
}
$notesFile = [IO.Path]::GetTempFileName()
$notesText | Set-Content $notesFile -NoNewline

# Tag the built commit itself, rather than letting GitHub resolve a branch
# name at release time, and publish from that tag only.
Invoke-Git "tagging $tag" @('tag', '-a', $tag, $head, '-m', "$display $Version") | Out-Null
& git -C $repo push --quiet origin "refs/tags/$tag"
if ($LASTEXITCODE -ne 0) {
    & git -C $repo tag -d $tag | Out-Null
    Fail "pushing tag $tag failed; the local tag was deleted again"
}
$tagged = Invoke-Git "resolving $tag" @('rev-parse', "refs/tags/$tag^{commit}")
if ($tagged -ne $head) { Fail "$tag resolves to $tagged, not the built commit $head" }

& gh release create $tag --verify-tag --title "$display $Version" --notes-file $notesFile $saZip $mmZip
if ($LASTEXITCODE -ne 0) {
    Fail ("gh release create failed (exit $LASTEXITCODE). Tag $tag is already pushed at $head; once the cause is " +
          "fixed, create the release from that tag with gh release create $tag --verify-tag, attaching $saZip and $mmZip.")
}
$assets = & gh release view $tag --json assets --jq '.assets | length'
if ($LASTEXITCODE -ne 0 -or "$assets".Trim() -ne '2') { Fail "release $tag was created but lists '$assets' assets instead of 2; check it on GitHub" }
Remove-Temp
Write-Host "Published $tag from $short with both zips." -ForegroundColor Green
