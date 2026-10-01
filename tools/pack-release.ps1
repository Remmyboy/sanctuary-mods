<#
.SYNOPSIS
Builds a mod's release zips, and optionally publishes the GitHub release.

.DESCRIPTION
The Mod Manager is the one base install; every other mod is an add-in for it.

  <Mod>-<version>-ModManager.zip   just the mod, for an install that has the
                                   Mod Manager. Every mod ships this. The Mod
                                   Manager's own also updates the loader and
                                   the mod API in BepInEx\plugins.
  ModManager-<version>-Standalone.zip
                                   the Mod Manager release only: BepInEx +
                                   the loader + the mod API + the Mod Manager,
                                   the clean install everything else goes on.

Other mods have had no Standalone zip since September 2026: each carried its
own copy of the loader, and an older one extracted later would downgrade the
loader under everything else.

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
    [string]$BuiltFor = '24 September 2026 (0.0.1.20)'
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

# The loader lives in BepInEx\plugins and ships with the Mod Manager; a copy
# under SanctuaryMods would load itself, and that copy would load itself again.
if ($Mod -eq 'ModLoader') { Fail "no ModLoader mode: the loader ships inside the ModManager release; release ModManager" }
# The mod API is the loader's twin: BepInEx loads it from plugins\ under a
# fixed identity, and it ships with the Mod Manager too.
if ($Mod -eq 'ModApi') { Fail "no ModApi mode: the mod API ships inside the ModManager release; release ModManager" }
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
# A Lua-only gameplay mod (ZoneControl) has no DLL: mod.json is its identity.
$luaMod = -not $plugin -and (Test-Path "$src\$Mod\mod.json")
if ($luaMod) {
    $manifest = Get-Content "$src\$Mod\mod.json" -Raw | ConvertFrom-Json
    $display = $manifest.name
    if (-not $Version) { $Version = $manifest.version }
    if (-not $display -or -not $Version) { Fail "$Mod\mod.json needs a name and a version" }
} else {
    if (-not $plugin) { Fail "no [BepInPlugin] or mod.json found under $Mod at $short - is that the right project name, and is it committed?" }
    $display = $plugin.Matches[0].Groups[1].Value
    if (-not $Version) { $Version = $plugin.Matches[0].Groups[2].Value }
}
Write-Host "Packing $display $Version ($Mod$(if ($luaMod) { ', Lua-only' })) from $short" -ForegroundColor Cyan

# ---- Lua-only: the committed files, byte for byte --------------------------
# The lobby compares a gameplay mod's files byte for byte across players, so
# the zip must hold exactly the committed blobs. A checkout (and git archive
# with core.autocrlf) turns mod.json's LF into CRLF; archive with autocrlf off
# and then prove every file against its blob hash.
if ($luaMod) {
    $lua = "$stage\luamod"
    New-Item -ItemType Directory -Force -Path $lua | Out-Null
    & git -c core.autocrlf=false -C $repo archive --format=tar -o "$stage\luamod.tar" $head -- $Mod
    if ($LASTEXITCODE -ne 0) { Fail "git archive of $Mod failed" }
    & "$env:SystemRoot\System32\tar.exe" -xf "$stage\luamod.tar" -C $lua
    if ($LASTEXITCODE -ne 0) { Fail "extracting the archive of $Mod failed" }
    # Repo plumbing, not part of the mod.
    Get-ChildItem "$lua\$Mod" -Recurse -Force -Filter .gitattributes | Remove-Item -Force
    $blobs = @{}
    foreach ($l in (& git -C $repo ls-tree -r $head -- $Mod)) {
        if ($l -match '^\d+ blob ([0-9a-f]{40})\t(.+)$') { $blobs[$Matches[2]] = $Matches[1] }
    }
    $sha1 = [Security.Cryptography.SHA1]::Create()
    $files = Get-ChildItem "$lua\$Mod" -Recurse -File
    foreach ($f in $files) {
        $rel = $f.FullName.Substring("$lua\".Length).Replace('\', '/')
        $bytes = [IO.File]::ReadAllBytes($f.FullName)
        $header = [Text.Encoding]::ASCII.GetBytes("blob $($bytes.Length)`0")
        $hash = -join ($sha1.ComputeHash([byte[]]($header + $bytes)) | ForEach-Object { $_.ToString('x2') })
        if ($blobs[$rel] -ne $hash) { Fail "$rel in the archive does not match its committed blob (line endings?)" }
    }
    Write-Host "  $($files.Count) file(s), each identical to its committed blob"
}

# ---- build ----------------------------------------------------------------
if (-not $luaMod) {
Write-Host "  building Release..."
$buildLog = & dotnet build "$src\SanctuaryMods.sln" -c Release -v q --nologo -p:DeployPath=$stage
if ($LASTEXITCODE -ne 0) {
    $buildLog | Select-Object -Last 40 | ForEach-Object { Write-Host "    $_" }
    Fail 'build failed'
}
foreach ($need in "$Mod.dll", 'ModLoader.dll', 'Sanctuary.ModApi.dll') {
    if (-not (Test-Path "$stage\$need")) { Fail "build produced no $need" }
}

# The SDK stamps the commit it built from into every DLL's product version
# ("1.0.0+<sha>"). Checking it is what ties the zips to the tag: a DLL built
# from anything else, a dirty tree or a stale output, cannot pass.
foreach ($dll in @("$Mod.dll", 'ModLoader.dll', 'Sanctuary.ModApi.dll') | Select-Object -Unique) {
    $pv = [Diagnostics.FileVersionInfo]::GetVersionInfo("$stage\$dll").ProductVersion
    $rev = if ($pv -match '\+([0-9a-f]{40})') { $Matches[1] } else { '(none)' }
    if ($rev -ne $head) { Fail "$dll carries source revision $rev (product version '$pv'), expected $head" }
}
Write-Host "  DLLs carry source revision $short"
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
if ($luaMod) { Copy-Item "$stage\luamod\$Mod\*" "$mm\SanctuaryMods\$Mod\" -Recurse }
else { Copy-Item "$stage\$Mod.dll" "$mm\SanctuaryMods\$Mod\$Mod.dll" }
# A mod may ship data next to its DLL (SanctuaryHud's alert sounds live in
# <Mod>\sounds); anything under the project's sounds folder goes along.
if (Test-Path "$src\$Mod\sounds") { Copy-Item "$src\$Mod\sounds" "$mm\SanctuaryMods\$Mod\sounds" -Recurse }
# The Mod Manager is built on the mod API and on a loader that knows gameplay
# mods, both in BepInEx\plugins, so its add-in zip updates those too.
$framework = $Mod -eq 'ModManager'
$frameworkNote = ''
if ($framework) {
    New-Item -ItemType Directory -Force -Path "$mm\BepInEx\plugins" | Out-Null
    Copy-Item "$stage\ModLoader.dll" "$mm\BepInEx\plugins\ModLoader.dll"
    Copy-Item "$stage\Sanctuary.ModApi.dll" "$mm\BepInEx\plugins\Sanctuary.ModApi.dll"
    $frameworkNote = @"

   This one also updates BepInEx\plugins\ModLoader.dll and adds
   BepInEx\plugins\Sanctuary.ModApi.dll, the framework it runs on. Restart
   the game once after extracting: those two never hot-reload.
"@
}
$t = "Sanctuary $display $Version - Mod Manager add-in"
$needsManager = if ($framework) { @"
This updates an install that already has BepInEx. For a first install use
the Standalone zip of this release instead, which includes everything.
"@ } else { @"
This mod needs the Sanctuary Mod Manager, which brings BepInEx, the mod
loader and the mod framework. If you don't have it yet, install that first:
the Standalone zip of the latest ModManager release at
https://github.com/Remmyboy/sanctuary-mods/releases
"@ }
@"
$t
$('=' * $t.Length)

$needsManager

INSTALL
1. Extract this zip into your Sanctuary 'engine' folder (the one with
   Sanctuary.exe), so the mod lands in SanctuaryMods\$Mod\.
$(if ($luaMod) { @"
2. It is a gameplay mod: it only runs in lobbies whose host switches it on
   (Mods, in the lobby), and every player needs this same copy before Start
   works. Outside those lobbies the game runs vanilla.
"@ } else { @"
2. That's it. If the game is already running the loader picks it up on the
   spot; it shows up under UI Mods on the Mods page, where it can be
   switched off and on and its settings changed.$frameworkNote
"@ })

WHAT IT DOES
$bodyText

UNINSTALL
Delete SanctuaryMods\$Mod, or switch it off on the Mods page.
"@ | Set-Content "$mm\README.txt" -NoNewline
$mmZip = "$outPath\$Mod-$Version-ModManager.zip"
New-Zip $mm $mmZip

# ---- Standalone (the Mod Manager only) -------------------------------------
# The base install. An allowlist, not a copy of the install: BepInEx\config
# there holds the developer's own per-mod settings, and none of that belongs
# in a release.
$saZip = $null
if ($framework) {
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
Copy-Item "$stage\Sanctuary.ModApi.dll" "$sa\BepInEx\plugins\Sanctuary.ModApi.dll"
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
  BepInEx\plugins\Sanctuary.ModApi.dll the mod framework: gameplay mods
                                       picked in the lobby, and the API
                                       anyone's mods are built on
  SanctuaryMods\$Mod\$Mod.dll  the mod; the loader loads every DLL
                                       under SanctuaryMods and reloads it
                                       when the file changes
Other mods from the same author install the same way: drop their folder
into SanctuaryMods.

ALREADY RUNNING BEPINEX?
Copy BepInEx\plugins\ModLoader.dll, BepInEx\plugins\Sanctuary.ModApi.dll
and the SanctuaryMods folder from this zip into your engine folder - AND make sure BepInEx\config\BepInEx.cfg has
    HideManagerGameObject = true
under [Chainloader]. Sanctuary destroys BepInEx's manager object after
start-up otherwise, and every plugin on it stops running right after it
loads. This zip ships that setting; a BepInEx you installed yourself
defaults it to false.

MULTIPLAYER AND TRUST
Outside a lobby the game always runs vanilla, and every lobby starts with no
gameplay mods picked, so you can play with anyone, modded or not. Gameplay
mods only change a match when the lobby host switches them on, and then
every player needs identical copies before Start works. That is
compatibility, not a safety check. Like every BepInEx plugin, a mod's DLL
runs as full-trust code inside the game with your Windows account's
permissions, so only install mods from a source you trust.

UNINSTALL
Delete SanctuaryMods\$Mod, or switch it off on the Mods page.
"@ | Set-Content "$sa\README.txt" -NoNewline
$saZip = "$outPath\$Mod-$Version-Standalone.zip"
New-Zip $sa $saZip
}

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
$mmExpected = @($(if ($luaMod) { "SanctuaryMods/$Mod/mod.json" } else { "SanctuaryMods/$Mod/$Mod.dll" }), 'README.txt')
if ($framework) { $mmExpected += 'BepInEx/plugins/ModLoader.dll', 'BepInEx/plugins/Sanctuary.ModApi.dll' }
Assert-Entries $mmZip $mmExpected
if ($saZip) {
    Assert-Entries $saZip @("SanctuaryMods/$Mod/$Mod.dll", 'SanctuaryMods/README.txt', 'README.txt',
        'BepInEx/plugins/ModLoader.dll', 'BepInEx/plugins/Sanctuary.ModApi.dll', 'BepInEx/config/BepInEx.cfg', 'winhttp.dll',
        'doorstop_config.ini', '.doorstop_version')
}
# Kept an array even when one zip is left: a lone string splatted into gh
# (@zips) goes in a character at a time ("no matches found for `C`").
$zips = @(@($saZip, $mmZip) | Where-Object { $_ })

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

$notesText = if ($Notes) { (Get-Content $Notes -Raw).TrimEnd() } elseif ($luaMod) {
    @"
$bodyText

- **ModManager** - extract into the game's ``engine`` folder, so the mod lands in ``SanctuaryMods\$Mod\``. Needs the [Sanctuary Mod Manager](https://github.com/Remmyboy/sanctuary-mods/releases?q=ModManager).
- **Every player** needs this same copy. The host opens **Mods** in the lobby and switches it on; Start waits until everyone matches.

This is a gameplay mod: it changes the match's Lua, but only in lobbies where the host picks it. Outside those the game runs vanilla. Built for the game update of $BuiltFor.
"@
} else {
    @"
$bodyText

- **ModManager** - the mod, for an install that has the [Sanctuary Mod Manager](https://github.com/Remmyboy/sanctuary-mods/releases?q=ModManager) (install that first if you haven't). Extract into the game's ``engine`` folder; it appears under UI Mods.

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

& gh release create $tag --verify-tag --title "$display $Version" --notes-file $notesFile @zips
if ($LASTEXITCODE -ne 0) {
    Fail ("gh release create failed (exit $LASTEXITCODE). Tag $tag is already pushed at $head; once the cause is " +
          "fixed, create the release from that tag with gh release create $tag --verify-tag, attaching $($zips -join ' and ').")
}
$assets = & gh release view $tag --json assets --jq '.assets | length'
if ($LASTEXITCODE -ne 0 -or "$assets".Trim() -ne "$($zips.Count)") { Fail "release $tag was created but lists '$assets' assets instead of $($zips.Count); check it on GitHub" }
Remove-Temp
Write-Host "Published $tag from $short with $($zips.Count) zip(s)." -ForegroundColor Green
