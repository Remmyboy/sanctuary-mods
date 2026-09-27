<#
Generates the Ascendant faction's unit templates from the game's EDA ones.

Each template is the EDA unit with a new id (ua...), the ASCENDANT faction tag
instead of EDA, build lists that name ASCENDANT, EDA's model borrowed through
general.modelTpId, and any upgrade into an EDA unit cut. Re-run it after a game
update to pick up the devs' changes to the EDA units, then re-apply your own
edits in $Tweaks.

  pwsh -File tools/make-templates.ps1 [-GamePath <engine folder>]
#>
param(
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Sanctuary Shattered Sun Playtest\engine'
)
$ErrorActionPreference = 'Stop'
$src = Join-Path $GamePath 'LJ\lua\common\units\unitsTemplates'
$out = Join-Path $PSScriptRoot '..\lua\common\units\unitsTemplates'

# New id -> the EDA unit it is made from.
$Units = [ordered]@{
    ual0000 = 'uel0000'   # commander
    ual1501 = 'uel1501'   # T1 engineer
    ual1001 = 'uel1001'   # T1 tank
    ual1002 = 'uel1002'   # T1 raider
    uas1511 = 'ues1511'   # T1 land factory
    uas1601 = 'ues1601'   # T1 alloy extractor
    uas1611 = 'ues1611'   # T1 energy generator
}

# Per-unit edits after the conversion: [regex, replacement] pairs.
$Tweaks = @{
    # The Ascendant tank trades a little speed for a lot more armour, so the
    # faction is visibly its own thing in a match.
    ual1001 = @(
        @('(?m)^(\s*)max = 275,', '${1}max = 420,'),
        @('(?m)^(\s*)value = 275,', '${1}value = 420,'),
        @('(?m)^(\s*)speed = 3\.3,', '${1}speed = 2.9,')
    )
}

foreach ($id in $Units.Keys) {
    $from = $Units[$id]
    $text = Get-Content (Join-Path $src "$from\$from.santp") -Raw

    $text = $text -replace "tpId = `"$from`",", "modelTpId = `"$from`",`n        tpId = `"$id`","
    $text = $text -replace '"EDA",', '"ASCENDANT",'
    $text = $text -replace 'Tags\.EDA\b', 'Tags.ASCENDANT'
    $text = $text -replace 'unitTypeName = "EDA', 'unitTypeName = "Ascendant'
    $text = $text -replace '(?m)^(\s*)name = "([^"]+)",(\s*\r?\n\s*orders)', '${1}name = "Ascendant ${2}",${3}'
    # Upgrades lead into EDA's tech tree: cut them. An extractor's only build
    # list is its upgrade, so that goes too.
    $text = $text -replace '(?m)^\s*upgradesTo = "[^"]+",\r?\n', ''
    $text = $text -replace '(?m)^\s*canBuild = "Tags\.ue[a-z]\d{4}",\r?\n', ''

    foreach ($t in $Tweaks[$id]) { $text = $text -replace $t[0], $t[1] }

    if ($text -match '"EDA"|Tags\.EDA|"ue[a-z]\d{4}"' -and $text -notmatch "modelTpId = `"$from`"") {
        throw "$id still names EDA after conversion"
    }
    $dir = Join-Path $out $id
    New-Item -ItemType Directory -Force $dir | Out-Null
    # LF endings and no BOM, byte-identical wherever it is generated.
    [IO.File]::WriteAllText((Join-Path $dir "$id.santp"), ($text -replace "`r`n", "`n"), (New-Object Text.UTF8Encoding $false))
    "  $id  <- $from"
}
