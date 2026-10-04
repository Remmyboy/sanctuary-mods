<#
.SYNOPSIS
  Syntax-checks mod Lua with the game's own LuaJIT, without the game.

.DESCRIPTION
  lua-check.ps1 [-Strict] [<file-or-dir>...]     default: the whole repo

  Compiles (never runs) every .lua file and every chunk of Lua embedded in
  C# strings (@"..." literals, and "..." + "..." runs; interpolations become
  a placeholder name). Errors print as <file>:<line> with LuaJIT's message
  and the offending chunk line. Exit code 1 when anything fails to compile.

  Then lints what compiled, from the bytecode, as <file>:<line>: warning:
    - global assignments (missing local). Allowed: __Names, names in a
      `lua-check: globals A, B` comment in the file, and global function
      definitions in game-file appends (lua/append/**, modapi/hooks/**)
    - pcall(...) whose results are thrown away
    - C#: a global set to true/false that the same file reads back with
      GetLuaGlobal / ModLua.GetGlobal (it can't see booleans)
  `lua-check: ok` on the line silences one. Warnings fail only with -Strict.

  Beyond that it knows nothing of behaviour: for that, run the chunk in game through the
  probe (tools/probe.ps1 "luaf chunk.lua").
#>
param(
    [switch]$Strict,
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$Paths
)

$ErrorActionPreference = 'Stop'
$Repo = Split-Path $PSScriptRoot -Parent
$Lua = 'C:\Program Files (x86)\Steam\steamapps\common\Sanctuary Shattered Sun Playtest\engine\Sanctuary_Data\Plugins\x86_64\lua51.dll'
if (-not $Paths) { $Paths = @($Repo) }
if ($Strict) { $Paths = @('--strict') + $Paths }

$proj = Join-Path $PSScriptRoot 'GameRef\GameRef.csproj'
& dotnet build $proj -c Release -v:quiet --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'GameRef build failed' }
& dotnet (Join-Path $PSScriptRoot 'GameRef\bin\Release\net8.0\GameRef.dll') lua $Lua @Paths
exit $LASTEXITCODE
