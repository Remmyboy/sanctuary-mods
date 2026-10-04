# Writing a mod for Sanctuary: Shattered Sun

Anyone can write a mod on top of the Sanctuary mod framework and share it.
Players install a mod by dropping its folder into `engine\SanctuaryMods\`; the
framework loads it, hot-reloads it when its files change, lists it on the
**Mods** page, and — for mods that change the match — lets the lobby host pick
it and checks that every player has the same copy before the match starts.

A player with no mods at all can still play with anyone: outside a lobby the
game always runs vanilla, and a lobby with no gameplay mods picked is a vanilla
lobby.

New to this? [Your first mod](your-first-mod.md) is a step-by-step walkthrough
that needs only a text editor. This page is the full reference.
[Field notes](modding-field-notes.md) has what we learnt about the game
itself, and the traps that cost us the most time.

## Two kinds of mod

| | **UI mod** | **Gameplay mod** |
| --- | --- | --- |
| What it changes | Your own screen: HUD, hotkeys, camera, info | The match itself: units, factions, rules, AI, art |
| Made of | A C# DLL | Lua and `.santp` files laid out like `LJ\lua`, `.sanpack` art packs, optionally a DLL |
| Who switches it on | Each player, on the Mods page, any time (even mid-match) | The lobby host, in the lobby's **Mods** panel, before Start |
| Other players need it? | No | Yes, byte-identical, or Start stays greyed out |
| Hot reload | Rebuild and it reloads within a second, even mid-match | Lua: next match. DLL: reloads once the match ends |

## Quick start

With the .NET SDK installed:

```bash
dotnet new install <this repo>\templates\sanctuary-mod
dotnet new sanctuary-mod -n FasterTanks --kind gameplay --modAuthor "Alice"
cd FasterTanks
dotnet build
```

`dotnet build` compiles the mod and copies it into
`engine\SanctuaryMods\FasterTanks\`, where the loader picks it up within a
second, whether or not the game is running. The template's options:

- `--kind ui|gameplay` (default `ui`); a UI mod has no `lua\` folder.
- `--modAuthor "Your Name"` goes into `mod.json` and makes the mod's id unique
  (`alice.fastertanks`).
- `--gamePath "D:\...\engine"` if the game isn't in Steam's default folder.
  Or pass `-p:GamePath=...` to any build.

To build without touching the game (for example while you're playing), use
`dotnet build -p:DeployPath=some\other\folder`.

A gameplay mod from the template comes with `lua\<name>\host.lua`, a
[host script](#host-scripts-and-match-events) already named in `mod.json`:
put the match's rules there.

There are five worked examples in [`examples/`](../examples):

- **ExampleGameplayMod** is Lua only. It appends to `common/colors.lua` so one
  army plays in a loud colour, with the colour and army as
  [options](#options-let-the-host-tune-your-mod). Copy the folder into
  `SanctuaryMods`, pick it in a lobby, and start a match against the AI.
- **SupplyDrop** is Lua only. Every few minutes every army gets alloys and
  energy. It shows a host script, timers and options working together.
- **EngineersAndRaiders** is Lua only. Factories can build only engineers and
  raiders, with an option for which raiders. It shows how to change what can
  be built, in the menus and on the host (see [Recipes](#recipes)).
- **AscendantFaction** is Lua and a `mod.json`, no C#. It adds a fourth
  faction with two commanders to pick from, using EDA's models (see
  [Add a faction](#add-a-faction)).
- **ExampleUiMod** is C#. It shows which gameplay mods are live, binds
  settings that appear on the Mods page, and subscribes to match events.

## A mod folder

```
SanctuaryMods\
  FasterTanks\
    mod.json            what the mod is (below)
    FasterTanks.dll     optional: C#
    lua\                gameplay mods: mirrors the game's LJ\lua
      append\
        common\colors.lua   added to the end of common\colors.lua
      units\...\x.santp     replaces the game's file of that name
      fastertanks\util.lua  a new file: Import("fastertanks/util.lua")
    packs\              gameplay mods: art, as .sanpack files (see Art packs)
```

One folder is the whole mod. Players install it by copying the folder in and
uninstall it by deleting the folder. There's no restart either way.

### mod.json

```json
{
  "$schema": "https://raw.githubusercontent.com/Remmyboy/sanctuary-mods/main/docs/mod.schema.json",
  "id": "alice.fastertanks",
  "name": "Faster Tanks",
  "version": "1.0.0",
  "author": "Alice",
  "description": "Tanks move faster.",
  "kind": "gameplay",
  "luaRoot": "lua",
  "url": "https://example.com/fastertanks",
  "requires": ["bob.tankcore"],
  "apiVersion": 1,
  "options": [
    { "key": "bonus", "label": "Speed bonus (%)", "type": "number", "default": 20, "min": 0, "max": 50, "step": 5 }
  ]
}
```

The `$schema` line is optional. With it, editors such as VS Code check the
file as you type and suggest the fields.

| Field | Meaning |
| --- | --- |
| `id` | **Required.** 1–64 characters from `a-z 0-9 . _ -`. This is how lobbies name the mod to other players, so start it with your own name. |
| `name`, `version`, `author`, `description` | Shown on the Mods page and in the lobby. A player with a different `version` is told "has Faster Tanks 1.1 (host 1.2)". |
| `kind` | `"ui"` or `"gameplay"`. Only `"gameplay"` makes the folder's DLLs follow the lobby selection. A folder with Lua or `.santp` files is always gameplay. |
| `luaRoot` | The folder inside the mod that mirrors `LJ\lua`. Default `lua`. Nothing outside it is overlaid, so docs and scratch files are safe to keep in the mod folder. |
| `url` | Where to get the mod. It's shown to players who are missing it. It's never opened or fetched automatically. |
| `requires` | Ids of other gameplay mods that must be picked alongside this one. If one isn't, the lobby says so and holds Start. |
| `apiVersion` | The ModApi major version you wrote against (`1`). |
| `options` | Settings the lobby host picks for a gameplay mod. See [Options](#options-let-the-host-tune-your-mod). |
| `hostScript`, `clientScript` | Lua files of yours, relative to `luaRoot`, that the framework imports in the host's simulation and in every player's client when the mod is picked. See [Host scripts and match events](#host-scripts-and-match-events). |
| `gameVersion` | The game version you made and tested the mod for, such as `"0.0.1.20"`, or a prefix such as `"0.0.1"`. On any other version the Mods page shows "made for game 0.0.1.20", so after a game update players can see which mods might need one too. The mod still loads either way. |
| `factions` | Factions the mod adds to the lobby's faction dropdown. See [Add a faction](#add-a-faction). |
| `ais` | AIs the mod brings, which the host gives to AI seats. See [Add an AI](#add-an-ai). |

A folder with no `mod.json` still works the way mods worked before
manifests: its DLL is a UI mod, its Lua files are a gameplay mod whose id is
the folder name, and `luaRoot` is the folder itself.

**Same copy** means the same *content hash*. That is a SHA-256 over every
overlaid file's path and bytes, every art pack, the options' keys, types and
ranges, the factions, and the DLLs of a gameplay mod. The rest of `mod.json` isn't part of it, so rewording
the description or an option's label doesn't make you incompatible. Anything
else does, which is the point: two players whose Lua differs by one byte
would desync.

## Options: let the host tune your mod

A gameplay mod can declare settings in `mod.json`. When the host picks the mod
in a lobby, its options appear under it in the Mods panel. The host sets them,
everyone else sees the values, and the match runs with them on every machine.

```json
"options": [
  { "key": "minutes", "label": "No-rush time", "type": "number", "default": 20, "min": 0, "max": 60, "step": 5,
    "description": "Minutes before armies may attack each other." },
  { "key": "noAir", "label": "No air units", "type": "toggle", "default": false },
  { "key": "color", "label": "Colour", "type": "choice", "default": "pink",
    "choices": [ { "value": "pink", "label": "Hot pink" }, { "value": "lime", "label": "Lime" }, "gold" ] }
]
```

| Field | Meaning |
| --- | --- |
| `key` | **Required.** What your code reads the value by. A Lua name: letters, digits and `_`, not starting with a digit, not a Lua keyword. |
| `type` | **Required.** `toggle` (on/off), `choice` (one of `choices`), `number`, or `units` (a set of units). `int` is a number with `step` 1. |
| `label` | Shown in the lobby. Defaults to the key. |
| `description` | Shown on the Mods page. |
| `default` | `true`/`false`, a choice's value, a number, or a list of unit ids. Missing: off, the first choice, the lowest number, or no units. |
| `choices` | For a choice: values, or `{ "value": ..., "label": ... }` pairs. The code sees the value; the lobby shows the label. |
| `min`, `max`, `step` | For a number. With both `min` and `max` the lobby shows a slider, otherwise a text box. Values are clamped to the range and snapped to `min` plus whole steps. |

Up to 32 options per mod. Mistakes in them (a bad key, a default outside the
range) are listed under Problems on the Mods page, and the option falls back
to something sensible rather than breaking the mod.

**Reading them in Lua.** The framework writes the values to
`modoptions/<your mod id>.lua` before any of your files run, on every
player's machine:

```lua
local Options = Import("modoptions/alice.fastertanks.lua").Options

if Options.noAir then ... end            -- toggle: true or false
local ticks = Options.minutes * 60 * 10  -- number: a Lua number
if Options.color == "lime" then ... end  -- choice: the value string
if Options.banned[tpId] then ... end     -- units: a set, true for each picked unit id
```

**Units.** A `units` option lets the host pick units from a list of every
unit the match can build: the game's, plus those of the picked mods. In the
lobby it shows how many are picked and a button that opens a grid per section
(land, air, naval, structures): a column per faction, a row per kind of unit
under its tech level, and in each cell the unit's strategic symbol and name.
A click picks a unit; a kind's, faction's or tech level's header picks all of
them. Other players can open the same grid to see the host's pick.
Commanders, and units no builder can make, aren't listed. In C#,
`options.GetUnits(key)` gives the ids. See the Unit Restrictions mod for one
in use.

The file always has every option you declared, set to the host's value or
the default, so you never need to check for nil. It exists whenever your mod
is picked, including in replays, so import it at the top of any file.

**Reading them in C#.** A gameplay DLL reads the same values:

```csharp
var options = Modding.Options(this);     // or Modding.Options("alice.fastertanks")
var minutes = options.GetNumber("minutes");
var noAir   = options.GetBool("noAir");
var color   = options.GetString("color");
```

`options.IsLive` is true while the values are the ones the current match
runs with; otherwise they're the defaults.

**What players see.**

- While the host drags a slider or flips a switch, Start waits for a moment,
  then the values go out. Everyone's machine rewrites the options file and
  reports back, the same check as the mod itself. Start opens again once
  everyone matches.
- The new values also go into the lobby chat.
- A host's values are remembered for the next lobby they host.
- Players need the same Mod API release as the host, because the options
  file and the match events come from it. If they don't have it, the lobby
  names both versions.

The values are part of the game's Lua hash, so a player who somehow ran with
different values couldn't start. Keep them to what the match needs. A
personal preference, like how a panel looks, belongs in a UI mod's
`Config.Bind` instead.

## Host scripts and match events

Most rules come down to "when the match starts, do this", "after 20 minutes,
do that" or "when an army is out, check this". The framework gives you both
the place and the timing.

**The place.** Name a Lua file in `mod.json`, and the framework imports it
when your mod is picked:

```json
"hostScript": "norush/host.lua",
"clientScript": "norush/client.lua"
```

- The host script runs in the host's simulation. That's where the match's
  rules belong: what can be built, who can attack, who wins.
- The client script runs on every player's client and in replays. It's for
  what a player sees.

Both are imported once, while the game loads, before the first tick. Paths
are relative to your `lua` folder. You don't need to find a game file to
append to.

**The timing.** `modapi/events.lua` is always there when any gameplay mod is
picked:

```lua
local Events = Import("modapi/events.lua").Events
local Options = Import("modoptions/alice.norush.lua").Options

Events.OnMatchStart(function()
    -- The armies and the map are set up.
end)

Events.After(Options.minutes * 60, function()
    -- Once, this far into the match, in game time.
end)

local drop = Events.Every(60, function() ... end)   -- every minute
-- drop:Cancel() stops it.

Events.OnTick(function(tick) ... end)               -- 10 times a second
Events.OnArmyDefeated(function(army) ... end)       -- host only

Events.IsHost()     -- true in the simulation, false in a client
Events.GameTime()   -- seconds since the start
```

- Times are game time: they pause and speed up with the game.
- A timer set before the match starts counts from the start.
- Armies are in the global `Armies` table. Skip `army.civilian`; each army
  has `army:IsAlive()`, `army:GiveResources("alloys" or "energy", n)`,
  `army:SetAlly(other)`, `army:SetEnemy(other)` and `army:SetNeutral(other)`.
- A handler that errors is logged with its traceback (in the game's log) and
  skipped; the other handlers, and other mods', keep running. A host script
  that fails to load is logged the same way.
- A handler that errors is logged in full the first time; after that it
  keeps being called but its errors aren't logged again, so a broken
  `OnTick` can't flood the log.
- `Events.IsHost()` is only settled inside a handler or in your
  host/client script. A file the game imports early, such as an append to
  `tags.lua`, runs before the framework knows which side it's on.

**Kills and damage (host only).** The game doesn't record who damaged or
killed a unit, so the framework works it out:

```lua
Events.OnUnitKilled(function(victim, info)
    -- info.army, info.unit: who dealt the killing damage (either may be nil)
    -- info.cause: "projectile", "area", "beam", "dash", "deathExplosion",
    --             or "none" (an army's defeat, a script's Destroy)
end)

Events.ModifyDamage(function(victim, amount, info)
    if info.army == Armies[1] then return amount * 1.25 end   -- army 1 deals +25%
end)

Events.OnUnitDamaged(function(victim, amount, info) ... end)  -- after the hit

Events.Kills(army)   -- enemy units that army has killed
```

- **Credit.** Splash damage from a shell is credited to the unit that fired
  it. A death explosion is credited to the unit that exploded, and its
  army.
- **What counts as a kill.** Every kill fires once. Units removed rather
  than killed (captures, upgrades, `Delete`) don't fire at all. Friendly
  kills are reported as they are, but `Kills` only counts enemies.
- **Modifiers.** Each `ModifyDamage` handler gets the amount the previous
  one returned, in the order they were registered, and returns a number (or
  nothing to leave it). The result never goes below 0. Modifiers apply to
  projectiles, splash, beams and dashes alike.
- **Only when used.** These hooks only go into the game when some picked
  mod uses one of the calls. They go in once the first tick has set the
  match up, so register from your host script or from an `OnMatchStart`
  handler: either way, kills by units that exist from the start are credited
  to them.

The [`SupplyDrop`](../examples/SupplyDrop) example is a complete mod built
this way.

**Helpers (host).** What most rules mods end up writing for themselves
(Mod API 1.8):

```lua
local players = Events.Players()   -- the player armies, AI included, by army id
Events.ArmyName(army)              -- the name the player chose
Events.Ally(a, b)                  -- allies both ways round; Events.Enemy(a, b) too

-- Runs fn; if it errors, it's reported once for that name: in the log, and
-- in the match's message log where players see it.
Events.Guard("Alice's No Rush: spawning", SpawnWave)
Events.Report("Alice's No Rush: map", "no spawn markers")

Events.Tell(army, "Your wave is ready")   -- one player's message log

-- Each client gets what fn returns for it, only when it has changed since
-- the last time (army is nil for an observer).
Events.SendToEachClient("NoRushState", function(army, clientId)
    return { secondsLeft = SecondsLeft(), mine = army and Waves(army) }
end)

-- A client's SendToHost(data, "NoRushRequest"), with the army that sent it
-- (nil for an observer).
Events.OnRequest("NoRushRequest", function(army, data, clientId) ... end)
```

Name the mod in what you pass to `Guard` and `Report`, so players know
whose problem it is. Mods using these need Mod API 1.8 or later; check
`if not Events.OnRequest then ... end` to say so rather than fail to load.

## Panels and notices (no DLL needed)

A gameplay mod can put its own panel on the match HUD from its client script,
in Lua. The Mod API draws it on the game's own HUD canvas, in the game's font
and at the player's UI Scale, beside the HUD mods' panels. Players can drag
it, resize it by the grip in its bottom corner, and click its title to fold it
away. Each player's position, size and folding are remembered.

```lua
-- lua\norush\client.lua (the mod's clientScript)
local UI = Import("modapi/ui.lua").UI

local panel = UI.Panel("alice.norush", { title = "NO RUSH", x = 20, y = 300 })

local function Draw(secondsLeft)
    panel:Set({
        UI.Text("Attacks allowed in " .. secondsLeft .. " s", { size = 24, color = "FFC040" }),
        UI.Rule(),
        UI.Row({
            UI.Swatch(Armies[2].color),
            UI.Text("Bob"),
            UI.Button("Wave", function() SendToHost({ op = "wave" }, "NoRushRequest") end),
        }),
    })
end

UI.Toast("No rush", "Attacks are allowed in 10 minutes", { seconds = 6 })
```

| Call | What it does |
| --- | --- |
| `UI.Panel(id, { title, x, y, align })` | Makes a panel, or returns the one with that id. `x` and `y` are where it first appears, in 1080p pixels from the top-left (`align = "right"` measures `x` from the right edge). Start the id with your mod's id. |
| `panel:Set(items)` | Replaces what the panel shows. Call it whenever something changes. Elements that are the same kind as before are updated in place, so calling it every second is cheap. |
| `panel:Show(bool)`, `panel:SetTitle(text)`, `panel:Remove()` | |
| `UI.Text(text, { size, color, width, rich })` | A line of text. `width` wraps it. `rich = true` lets TextMeshPro tags such as `<color=#FF0000>` through; it's off by default, so a player's name shows as typed. The game's font has no bold weight, and TextMeshPro's stand-in smears it, so there's no `bold`. |
| `UI.Button(label, fn, { color, enabled, size })` | `fn(button)` runs in this player's client on a click, `button` being `"left"` or `"right"`. |
| `UI.Row(items, { spacing })`, `UI.Column(items, { spacing })` | Elements side by side, or one above another. |
| `UI.Rule()`, `UI.Space(size)`, `UI.Swatch(color, size)` | A line across the panel, empty space, a square of colour. |
| `UI.Fill()` | Stretchy space in a row: what comes after it lines up on the right, and rows with one line up with each other (Mod API 1.6). |
| `UI.Toast(title, text, { seconds, color })` | A notice across the top of the screen for a few seconds. |

Sizes are in canvas units: text is 20 by default, and the game's build-menu
tiles are 80 across. Colours are `"RRGGBB"` strings, or an army's `color`.

**A panel is one player's view.** The client script runs on every player's
machine and in replays, so each player draws their own panel from what their
client knows. A button's function runs only in that client, so to change the
match it sends a request to the host: `SendToHost(data, "YourRequest")` in the
client, then handle it in your host script. To show a player something only
the host knows, send it to them: `SendToClient(data, "YourState", clientId)`
on the host, `RegisterListener("client_YourState", fn)` in the client.

**Who sent a request?** If your rules depend on which player asked, take
the request with `Events.OnRequest(name, fn)` in your host script: `fn`
gets the sender's army (Mod API 1.8).
[`PhantomX`](../PhantomX/lua/phantomx/host.lua) does this.

`modapi/ui.lua` is only put in place for matches where a picked mod's Lua
names it, so mods that don't draw panels run with exactly the files they did
before. It needs Mod API 1.5.0 or later.

**Replays of secrets.** A player's replay shows that player's panel as it
was, so a mod that keeps things from each player (hidden roles) can't show
them in its replays on its own. `modapi/replay.lua` (Mod API 1.6) lets each
client keep notes beside its replay: once the match is over and nothing is
secret, send everyone the full story from the host, save it with
`Replay.Note(key, data)` in the client, and in a replay read it back with
`Replay.Get(key)`, which has it from the start. `Replay.Playing()` says
whether this is a replay. Key notes by your mod's id and keep them small (a
few KB of plain tables). [`PhantomX`](../PhantomX/lua/phantomx/client.lua)
does this to show every player's role in its replays.

## Gameplay Lua

When the host starts a match, the framework swaps the picked mods' files into
the game's in-memory file cache on every player's machine. The game reads
all its Lua from that cache. Nothing on disk is changed, and after the match
the cache goes back to vanilla.

There are three ways a file can go in:

- **Append** (`lua\append\<path>`) adds your code to the end of the game's file, in
  the same Lua chunk, so the file's locals and globals are in scope and you can
  change what it defines. Many game files end in a top-level `return { ... }`;
  your code goes just before that return, so a function you replace also
  replaces the one the file hands out. Several mods can append to the same file,
  in the host's pick order. **Prefer this**: it survives game updates, and mods
  stack instead of clashing. Don't `return` from an append.
- **Replace** (`lua\<path>`, same path as a game file) swaps the file entirely.
  If two picked mods replace the same file, the one picked later wins, and the
  lobby panel warns about it.
- **Add** (`lua\<new path>`) creates a new file for your other files to
  `Import`.

Every file is compiled when the overlay goes on. A syntax error is logged
against your mod and file (`BepInEx\LogOutput.log`) instead of surfacing
halfway through a match.

Things that trip everyone up:

- **Game files are modules.** Every file runs in its own environment. Its
  "globals" live in the table `Import` returns, not in `_G`. From your own
  file, reach them through the module:
  `local Colors = Import("common/colors.lua").Colors`.
- **Host and client.** Only the host runs the simulation (`host\...`,
  `common\...` used by the host). Every player runs the client side
  (`client\...`). Both read the same overlaid files, which is why everyone
  needs the same copy.
- `.santp` unit templates are loaded by the game but aren't in the game's own
  Lua hash. The framework's content hash covers them, so the lobby still
  catches a mismatch.
- **The host is the authority, menus aren't.** Clients send requests
  ("queue 3 of this unit", "build that here"), and hotkey mods, scripts and
  future UI send them without going through your menu. A rule that only
  changes a menu doesn't hold on its own, so enforce it where the host
  handles the request too; EngineersAndRaiders does both.
- **Your append runs once per VM.** The host VM and every client VM each run
  the file, so anything with side effects (logging, counters) happens once
  per VM.

## Recipes

Each of these is a folder you could ship as it stands.

**Change what can be built.** Every builder's build list is its template's
`construction.canBuild` tag expression (e.g.
`"Tags.EDA * Tags.BUILDABLE_BY_T1_FACTORY"`). `ParseTagsFromString` turns it
into a set of unit ids; it's defined in `common/systems/tags.lua`, and the menu
and the host's `buildQueueUtils.CanBuild` both use it. Append to `tags.lua` to
filter what it returns for `BUILDABLE_BY_` expressions. Then append to
`common/commands/definitions/buildQueue.lua` to make the host refuse queue
requests that aren't on the list. The full version, with an option for which
raiders are allowed, is
[`examples/EngineersAndRaiders`](../examples/EngineersAndRaiders).

**Change a unit's numbers.** Unit stats live in
`common/units/unitsTemplates/<id>/<id>.santp` (the `ue`/`uc`/`ug` prefixes are
EDA, Chosen and Guard; `l` land, `a` air, `n` naval, `s` structure). Copy the
file to the same path under your `lua\` folder and edit it; a replacement wins
over the game's copy. That's simple, but a game update that changes the unit
means updating your copy.

**Hook a game function.** Append to the file that defines it, keep the
original, and replace it:

```lua
-- lua\append\host\units\unitsClasses\unitsDefault.lua
local originalComplete = HostFactory.CompleteBuildQueueItem
function HostFactory:CompleteBuildQueueItem(index)
    -- your code before
    return originalComplete(self, index)
end
```

Callers inside the file pick up your version too, since they look the
function up in the same table.

**Add your own module.** Put it at `lua\<yourname>\util.lua` and
`Import("yourname/util.lua")` from your appends. A folder named after you
can't collide with the game's files or another mod's.

**Add new units.** Put each one at
`lua\common\units\unitsTemplates\<id>\<id>.santp`. The game finds unit
templates by scanning that folder, and the framework makes new folders visible
to the scan. A new unit needs art: ship your own in an [art pack](#art-packs), or
borrow an existing unit's with `general.modelTpId = "<its id>"`. With
`modelTpId` the game builds the unit's model, skeleton and material from that
id, and the framework makes the rest follow: the placement ghost, the
portrait (unless your pack has one for the unit), the wreck, and the
hierarchy maps capture needs.

### Add a faction

A faction is a `factions` entry in `mod.json` plus its units. No C# is
needed. While the host has the mod picked, the faction is in every lobby
row's faction dropdown, the AI can play it, and the match knows it.

```json
"factions": [
  {
    "key": "dycom",
    "name": "Dycom",
    "tag": "DYCOM",
    "unitPrefix": "ud",
    "icon": "icons/dycom.png",
    "looksLike": "CHOSEN",
    "commanders": [
      { "name": "Spider", "unit": "udl0000" },
      { "name": "Mech", "unit": "udl0001" }
    ],
    "ai": { "folder": "AI/mods/AI-Dycom", "air": false, "naval": false }
  }
]
```

| Field | Meaning |
| --- | --- |
| `tag` | **Required.** The faction tag in every one of its units' `tags` list, like `"DYCOM"`. Build lists name it (`"Tags.DYCOM * Tags.BUILDABLE_BY_T1_FACTORY"`), so the tech tree stays inside the faction. |
| `unitPrefix` | **Required.** The two letters its unit ids start with (`ud` for `udl0000`). |
| `name` | Shown in the lobby. |
| `key` | Your own name for it, for Lua and C# to find it by. Default: the tag in small letters. |
| `commander` or `commanders` | The starting unit. With several, each is its own dropdown entry ("Dycom", "Dycom (Mech)"). The first is the faction's own entry. |
| `icon` | A PNG in the mod folder, shown beside the name. |
| `looksLike` | `EDA`, `CHOSEN` or `GUARD`. The game picks shield, build-beam, factory-platform and adjacency materials by those three factions' tags. Your units borrow this one's. Without it they get EDA's, and the game logs a line for each unit. |
| `ai` | The AI your faction's AI armies play: a folder under your `lua\` laid out like the game's `AI\mods\AI-Sanctuary-Rush`, usually a copy of it. `air` and `naval` say which layers it may use (land is on unless you say `"land": false`). Leave `ai` out and the stock AI plays your faction. It picks units by role tags and your faction tag, so it works if your units carry the stock role tags. |

What the framework does with it:

- **Numbering.** The game's three factions are lobby values 0 to 2. The
  picked mods' factions come next, in the host's pick order, one value per
  commander. Every player works out the same numbers from the same pick, so
  two faction mods can be picked together. Lua sees the value + 1, as
  always.
- **FactionsData.** The framework adds an entry per commander to
  `FactionsData` in `common/systems/factions.lua`. Each entry has `name`,
  `tpLetter`, `tag` and `initialUnit`; a further commander's entry also has
  `variantOf`. Don't add your own. Your Lua can look a faction up with
  `Import("modapi/factions.lua").Find("<mod id>", "<key>").index`, or map
  faction numbers to tags with `.TagsByIndex()`.
- **The stock AI.** Its faction tables (`{[1] = "EDA", [2] = "CHOSEN",
  [3] = "GUARD"}` in `AI\AIFunctions.lua` and each AI's
  `AIPlatoonFunctions.lua`) are pointed at the full list. That includes a
  copy of the AI in your own `lua\`.
- **Seats.** When the host drops the mod, or a change of pick renumbers the
  factions, players and AI on a modded faction move to its new number, or
  back to EDA if it's gone.

[`examples/AscendantFaction`](../examples/AscendantFaction) is a complete
faction with two commanders, all in `mod.json` and Lua.
`tools/make-templates.ps1` in the example generates its units from EDA's. It
gives them new ids and the new faction tag, borrows EDA's models, cuts EDA's
upgrade links and makes a few stat tweaks. Re-run it after a game update to
pick up the devs' changes to those units.

A faction mod written before `factions` existed, with its own DLL adding the
dropdown entry and its own `FactionsData[4] = ...`, clashes with this: drop
both and declare the faction instead.

What a faction can't do (yet): **sounds**. The game loads its sound banks
from their own files on disk, not from packs, so new units use the game's
sounds.

### Add an AI

An AI is a folder laid out like the game's `AI\mods\AI-Sanctuary-Rush`:
`AIPlatoonFunctions.lua`, `formers\` and `strategies\`, plus whatever files of
its own it imports through `modDirectory`. The host gives it to any AI seat
from that seat's **Player/AI** dropdown in the lobby ("AI: DuckAI"). Picking it
there switches the mod on too. Different seats can play different AIs in one
match, and seats left on plain **AI** play the game's own.

**No mod.json needed.** Drop the AI folder straight into `SanctuaryMods` as
you would into the game's `AI\mods`. The framework sees `AIPlatoonFunctions.lua`
at its root and makes it a gameplay mod named after the folder. The AI's files
go under `AI\mods\<folder>` for the match, so `modDirectory` works as usual.
An AI named like one of the game's own (`AI-Sanctuary-Rush`, say) goes beside
it as `AI\mods\<folder> (mod)` and never replaces the stock AI for other seats.

**A whole AI folder works too.** AI authors often share the game's entire
`AI` folder with their AI in its `mods\`. Drop that in as it is. Each AI of its
own becomes a mod. Its copies of the game's AIs and its shared `AI\*.lua` files
are left out, and the Mods page says so: every match runs the game's own
shared AI code, so one author's AI can't change another's.

**With a mod.json** in the AI folder, you choose the id, name, version and
link, and can describe the AI:

```json
{
  "id": "duck.duckai",
  "name": "DuckAI",
  "version": "2.0",
  "author": "Duck",
  "ais": [ { "name": "DuckAI", "description": "Takes the map early.", "air": true, "naval": false } ]
}
```

A mod laid out like `LJ\lua` (with other files besides) can ship one or more
AIs by folder:

```json
"ais": [ { "key": "duck", "name": "DuckAI", "folder": "AI/mods/DuckAI" } ]
```

| Field | Meaning |
| --- | --- |
| `name` | Shown in the seat's dropdown. |
| `folder` | The AI folder under the mod's `lua\`. Leave it out in an AI folder: the AI is the folder itself. |
| `key` | Your own name for it. Default: made from the name. |
| `description` | Shown on the Mods page. |
| `land`, `air`, `naval` | The movement layers the AI may use, as the game's `useLayer`. Default land and air. |

What the framework does with it:

- **Routing.** The seat's army gets `modDirectory`, `modName` (the folder's
  name) and `useLayer` from the pick, after its faction's AI (if any), so the
  host's pick wins.
- **Shared functions from newer games.** Some AIs are written against the
  developers' next AI release and call shared functions this game doesn't
  have yet. The framework adds the ones it knows of (such as
  `AIMarkerGenerator.IsPathMapPathableWithinReach`) only where the game lacks
  them, so the game's own versions take over after an update.
- **Faction tables.** An AI's `{EDA, CHOSEN, GUARD}` tables are pointed at the
  full faction list like the stock AI's, so it can play modded factions.
- **Same copy, replays.** Seats and AIs are part of the Lua every player
  checks before Start, and replays record them.

## Art packs

Models, skeletons, animations, materials, textures, UI sprites and effects
live in the game's `.sanpack` files: zips whose entries are paths such as
`Units/udl0000/LOD0/udl0000_lod0.sanmodel` or
`UI/Sprites/Icons/Units/udl0000.sansprite`. A gameplay mod can ship its own.
Put them in a `packs\` folder beside `mod.json`:

```
SanctuaryMods\
  Dycom\
    mod.json
    lua\...
    packs\
      Dycom.sanpack
```

- **When.** The packs go in while a match with the mod loads: at the host's
  Start, as each client loads, and when a replay of such a match plays. They
  come out when the match is over, before any other match loads. Nothing on
  disk is touched, and the game's own packs are never edited.
- **New and replaced files.** An entry with a new path adds art: a new unit
  finds `Units/<its id>/...` and `UI/Sprites/Icons/Units/<its id>.sansprite`
  there. An entry with the same path as one of the game's replaces the
  game's for that match. If two picked mods have the same path, the one
  picked later wins.
- **Everyone needs the same packs.** The host's simulation reads unit meshes
  and skeletons too. Packs are part of the content hash, so a player with a
  different pack can't start.
- **Making them.** A `.sanpack` is a zip (stored or deflated) with the game's
  layout inside, which Blender exporters for the game can write. Keep a
  unit's files under `Units/<id>/` exactly as the game's are.
- **Iterating.** A pack is held open while its match runs. Rebuild it
  between matches; the next match picks up the new one.
- **Not from packs:** sounds (see above) and the game's startup files. A
  pack can't change the main menu.

## Testing and debugging

- **Try a gameplay mod alone:** host a lobby, add an AI in the second slot,
  pick your mod in the Mods panel and start. The AI doesn't know about your
  rules, so expect it to struggle with restrictions.
- **Two players:** give the other player the same zip of your mod. The Mods
  panel lists each player with "has them all", "missing …", "has a different
  copy …" or "no mod support".
- **Logs:**
  - `engine\BepInEx\LogOutput.log` shows what the framework did: `Sanctuary Mod API` lines for the
    catalog, the overlay ("Lua overlay: 2 file(s) from …"), the factions and
    their numbers ("Factions: Dycom = 3, …"), the art packs ("Art packs: 1
    mounted …"), the pick and Start refusals, and your mod's syntax errors
    ("doesn't compile …").
  - The Mods page lists each mod's problems. That includes game Lua files it
    replaces whole: each is one more file to update after a game patch, and
    one more that another mod can't touch. An append usually does the job.
  - `%USERPROFILE%\AppData\LocalLow\Enhearten Media PTY\Sanctuary\Player.log`
    shows the game's own Lua errors: `HostLua` for the simulation, `ClientLua`
    for the UI, each with a stack trace naming the file and line. An appended
    line's number is past the end of the game's own file.
- **Iterate on Lua** in a lobby: edit a file and the host's pick picks it up
  within two seconds. The panel shows a new content hash, and every player has
  to have the same edit again. Lua already running in a match never changes;
  start a new one.
- **Iterate on C#:** `dotnet build` while the game runs, and the mod reloads
  within a second (gameplay DLLs wait for the match to end). If the game is a
  match you care about, build with `-p:DeployPath=` somewhere else instead.

## C# mods

A mod's DLL is a [BepInEx 5](https://docs.bepinex.dev/) plugin (`net472`,
`[BepInPlugin]` on a `BaseUnityPlugin`). The framework's loader creates it
instead of BepInEx, so that it can hot-reload. Reference the game's DLLs from
`engine\Sanctuary_Data\Managed` and the framework's from
`engine\BepInEx\plugins\Sanctuary.ModApi.dll`. The template sets this up, all
`Private=false`, so nothing gets copied beside your DLL.

### Hot reload rules

A rebuild destroys your plugin component and creates one from the new DLL.
Mono can't unload the old assembly, so it stays in memory with its static
state until the game exits. So:

1. **Undo everything in `OnDestroy`** that you set up in `Awake`: Harmony
   patches (`_harmony.UnpatchSelf()`), GameObjects you created, handlers on
   the game's static events, and Lua you injected.
2. **Use a new Harmony id per load**: `new Harmony("alice.fastertanks." + Guid.NewGuid())`.
3. **Keep state in fields, not statics**: the old copy's statics would
   linger, and the new copy starts with fresh ones.
4. **One DLL per mod.** Each DLL is loaded under a fresh name, so one mod DLL
   can't reference another. Libraries the game or BepInEx already have
   (Harmony, Newtonsoft.Json, Sanctuary.ModApi) are referenced, never shipped.
   The loader ignores copies found in `SanctuaryMods`.
5. **Settings**: anything you `Config.Bind` appears on the Mods page under
   your mod's name. A `bool` is a switch, an `AcceptableValueList<string>` a
   selector, an `AcceptableValueRange` a slider, anything else a text box.
   Settings save to `BepInEx\config\<your plugin GUID>.cfg`.

A **gameplay** mod's DLL (`"kind": "gameplay"`) is only created while a lobby
has picked the mod, and it's destroyed when the lobby or match ends. It runs on
every player's machine; `Modding.IsHost` tells you whether that machine runs
the simulation. Its rebuilds wait until the match is over.

### The API (`Sanctuary.ModApi`)

```csharp
using Sanctuary.ModApi;

Modding.Self(this)             // your ModInfo: Folder, Manifest, Id, Version...
Modding.FolderOf(this)         // for data files shipped beside the DLL
Modding.InLobby / IsHost / InMatch / InReplay
Modding.ActiveGameplayMods     // the gameplay mods live right now (empty = vanilla)
Modding.IsActive("bob.tankcore")
Modding.Options(this)          // your gameplay mod's option values: GetBool, GetNumber, GetInt, GetString, GetUnits

ModEvents.OnLobbyEntered(this, isHost => ...);
ModEvents.OnSelectionChanged(this, () => ...);   // gameplay mods live here, or their options, changed
ModEvents.OnMatchStarting(this, () => ...);
ModEvents.OnMatchEnded(this, () => ...);
ModEvents.OnLobbyLeft(this, () => ...);
// Handlers are dropped when `this` is destroyed: a reload never calls an old copy.

ModLua.Ready                   // the client Lua VM exists (in a match or replay)
ModLua.Run("Warn('hello')")    // run a chunk in your own client's VM
ModLua.GetGlobal("MyValue")    // read a _G global back as a string

Lobby.Selection                // the host's pick, as everyone sees it
Lobby.Players                  // who has what: Ok, Pending, Problem, Vanilla
Lobby.StartBlockedReason       // null, or why Start is held
Lobby.SetSelection(ids)        // host only, before Start
Lobby.SetOption(id, key, value) // host only, before Start; Lobby.ResetOptions(id)

ModCatalog.Mods                // every mod folder, with manifest and content hash

Factions.Current               // the picked mods' factions: Value, Label, Mod, Faction, Commander
Factions.NameOf(value)         // "EDA", "Dycom (Mech)", ... for a lobby faction value
Factions.ValueOf("bob.dycom", "dycom")  // a faction's lobby value, or -1
Packs.Mounted                  // the art packs in the game's asset table right now
UnitCatalog.Buildable(mods)    // every unit a match with these gameplay mods can build: Id, Name, Role, FactionName, Tech, Domain
```

`ModLua` is presentation-side: it runs in *your* client's VM only and never
reaches the host's simulation or other players. To change the match, ship Lua
in a gameplay mod instead.

The API's major version is `1`. Mods built against 1.x keep working on every
1.y.

### Bringing an existing BepInEx mod over

A plugin that already works from `BepInEx\plugins` works from `SanctuaryMods`
unchanged. Moving it there gets you hot reload, the Mods page switch and
settings, and your name on it:

1. Give it a folder, `SanctuaryMods\<YourMod>\`, with the DLL and a
   `mod.json` (`"kind": "ui"` unless it changes the match).
2. Make sure `OnDestroy` undoes `Awake` (see the rules above), or a reload
   leaves the old copy's patches in place.
3. Don't ship Harmony, BepInEx or Newtonsoft.Json beside it; the game has them.
4. Reference `Sanctuary.ModApi.dll` only if you use it. A mod that references
   it doesn't load without it. Any install from this framework has it, but a
   plain BepInEx install doesn't.

A plugin that patches the simulation, so every player must run it, is a
gameplay mod: set `"kind": "gameplay"` and the lobby takes care of the rest.
Such a plugin's `Awake` runs when the host picks the mod in a lobby, not at
game start, and it's destroyed when the match ends.

## How the lobby decides

1. Every lobby starts with no gameplay mods. The host switches them on in
   the lobby's **Mods** panel; nothing is ever on by default, and ladder
   lobbies can't have any. The **Mods**
   button beside Settings shows a count, and a `!` while someone is missing
   something. The pick also goes into the lobby chat. Players with the
   framework get the pick at once, apply the mods if their copies are
   identical, and report back. A picked mod's options appear under it; the
   host sets them and everyone else sees the values.
2. **Start** stays greyed out while any player is missing a mod, has a
   different copy, or has no mod support at all. The panel and the chat say who
   and what: "Bob has Faster Tanks 1.1 (host 1.2)". The host can switch the mod
   off, or ask Bob to update.
3. With nothing picked there's no check at all, and players without any
   mods play as usual.
4. When the match starts the pick is frozen. The files stay exactly as they
   were until the match is over, even if you rebuild.
5. Replays of a modded match remember its mods and their option values (a
   `.mods.json` beside the replay) and put them back on to play, as long as
   they're installed and identical.

The lobby messages travel on the game's own lobby connection as message types
vanilla players silently ignore, so a modded host and vanilla players can
always see each other's lobbies.

## Sharing your mod

Zip the mod's folder (`FasterTanks\` with `mod.json`, the DLL and `lua\`)
and tell players to extract it into `engine\SanctuaryMods\`. Players need the
framework, which comes with the Mod Manager (its Standalone zip is the full
install). Bump `version`
whenever the files change, so players with an old copy are told which one
they have.

The framework forgives the usual install slips:

- **A folder around the mod.** Some zip tools extract to
  `SanctuaryMods\FasterTanks-1.0\FasterTanks\mod.json`. The framework finds
  the mod inside, up to three folders down, and the Mods page suggests
  moving it up.
- **Several mods in one zip.** A pack of mods in one folder works the same
  way: each folder with a `mod.json` is its own mod.
- **An archive nobody extracted,** or a DLL dropped loose in `SanctuaryMods`.
  These show at the top of the Mods page, saying what to do.

A DLL belongs to the mod whose folder it's in, however deep, so
`FasterTanks\bin\FasterTanks.dll` is still FasterTanks's. A `mod.json` further
down inside a mod's folder doesn't make a second mod.

**Share the zip, not a checkout.** The content hash is byte-exact, and Git on
Windows rewrites line endings on checkout (`core.autocrlf`). Two players who
cloned your repository on different machines can end up with "different
copies" of identical code. The template ships a `.gitattributes` that stops
Git touching `lua\`; keep it, and have everyone play from the same zip.

Like every BepInEx plugin, a mod's DLL runs as full-trust code on the
player's PC. Only a mod's author can make it safe, so only publish what you'd
run yourself, and say where your source is.
