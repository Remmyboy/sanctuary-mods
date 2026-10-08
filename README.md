# Sanctuary Mods

A monorepo of client-side BepInEx mods for _Sanctuary: Shattered Sun_ (demo
and playtest, `engine` build). One project per mod, each building to its own
DLL that hot-reloads independently — so any mod can be shared, loaded or
unloaded on its own.

The UI mods are presentation-side only: they never touch the game's Lua tree
(which the multiplayer lobby hashes — `ComputeLuaHash` over `*.lua` under
`engine\LJ\lua\`) and never touch the simulation (which is hash-checked per
tick between players), so a modded client stays lobby-compatible with unmodded
players. The exceptions are called out in their own sections below.

It is also a **mod framework** anyone can build on: drop a mod's folder into
`engine\SanctuaryMods\` and the [Mod Loader](#modloader), [Mod API](#modapi) and
[Mod Manager](#modmanager) load it, hot-reload it, list it on the Mods page
and — for gameplay mods that change the match — let the lobby host pick it and
hold Start until every player has an identical copy. Outside a lobby the game
always runs vanilla, so players without mods can play with anyone.
[docs/your-first-mod.md](docs/your-first-mod.md) walks you through making a
mod, from an empty folder to a match. [docs/writing-mods.md](docs/writing-mods.md)
is the full author's guide, with a `dotnet new`
[template](templates/sanctuary-mod/) and five [examples](examples/).
[docs/modding-field-notes.md](docs/modding-field-notes.md) is everything we
learnt about the game along the way: how it fits together, Lua and UI traps,
game data, maps, debugging and surviving patches.

Lobby-compatible is not the same as safe. Every DLL here, like any BepInEx
plugin, is a full-trust client plugin: it runs inside the game process with
the permissions of the Windows account playing, and an unchanged Lua hash is
no check against cheating or harmful code. Nothing in the game or the loader
enforces good behaviour, so install DLL mods only from a source you trust.

**Install the [Mod Manager](#modmanager) first.** Its **Standalone** zip is
the one base install: BepInEx, the mod loader, the mod framework and the Mod
Manager. Extract it into the game's `engine` folder. Every other mod ships one
**ModManager** zip, which is just the mod; extract it into `engine` too. It
appears under UI Mods and can be switched on and off from there. (Before
September 2026 every mod also had a Standalone zip. Those carried their own
copy of the loader, and an old one extracted later would downgrade it.) Each
mod builds to `<name>.dll`, and the project link is its source.

| Project | Download | What it does |
| --- | --- | --- |
| [SanctuaryHud](SanctuaryHud/) | [**0.16.1**](https://github.com/Remmyboy/sanctuary-mods/releases/tag/SanctuaryHud-0.16.1) | The mini-map the game doesn't have; economy strip in the game's own style, optionally replacing the built-in panel; SanctuaryUI: the orders row, unit and build card, selection row and build strip docked into one panel in place of the game's bottom panels, all built on the game's own UI canvas; commander widget and alerts; reclaim values and build countdowns over the map; post-game match stats with a FAF-style score and a QUIT button; factory rally points shown, waypoints you can drag, delete and select by, and a factory queue you reorder by dragging; every panel resizable by its corner grip |
| [IdleEngineers](IdleEngineers/) | [**0.7.1**](https://github.com/Remmyboy/sanctuary-mods/releases/tag/IdleEngineers-0.7.1) | Idle engineers and factories as clickable tiles, in the eco panels' shape, on the game's own UI canvas; resize the panel by its corner grip |
| [EcoManager](EcoManager/) | [**0.9.1**](https://github.com/Remmyboy/sanctuary-mods/releases/tag/EcoManager-0.9.1) | BUILD and ALLOY tile panels in FA's shape, on the game's own UI canvas: everything under construction by spend, extractors by tier; an engineer's assist starts an upgrade and holds it paused until an engineer starts building it; resize either panel by its corner grip |
| [BuildHotkeys](BuildHotkeys/) | [**0.5.1**](https://github.com/Remmyboy/sanctuary-mods/releases/tag/BuildHotkeys-0.5.1) | One hotkey per *role*, same key every faction, cycling by tier; pause and repeat-build keys; extractor placement that snaps at screen size; any of the game's own hotkeys moved to another key |
| [LadderReporter](LadderReporter/) | [**0.4.0**](https://github.com/Remmyboy/sanctuary-mods/releases/tag/LadderReporter-0.4.0) | Reports ranked results; launches matchmade games; uploads the match's stats and replay to its SanctuaryDB page if you opt in |
| [ReplayManager](ReplayManager/) | [**0.5.1**](https://github.com/Remmyboy/sanctuary-mods/releases/tag/ReplayManager-0.5.1) | Watch the game's replays fog-free from any seat, with every economy in a table you can sort by any column |
| [CameraUtilities](CameraUtilities/) | [**0.2.1**](https://github.com/Remmyboy/sanctuary-mods/releases/tag/CameraUtilities-0.2.1) | Switches off icons, range rings, order lines and the UI, and unlocks how far out units are drawn, for cinematics |
| [ModManager](ModManager/) | [**0.15.0**](https://github.com/Remmyboy/sanctuary-mods/releases/tag/ModManager-0.15.0) | Mods page in the menu's side bar and on F8 in a match: mod toggles, one-click updates from the menu, settings (switches, sliders, text) with their descriptions on hover; the lobby's Mods panel where the host picks gameplay mods, and community AIs picked per AI seat |
| [ZoneControl](ZoneControl/) | [**0.5.1**](https://github.com/Remmyboy/sanctuary-mods/releases/tag/ZoneControl-0.5.1) | Gameplay mod: Supreme Commander's Zone Control on the converted Zone Control for FAF 8P V2 map. No commanders and no building; every zone you hold sends you units, and kills buy levels, heroes, artillery and upgrades |
| [PhantomX](PhantomX/) | [**0.2.1**](https://github.com/Remmyboy/sanctuary-mods/releases/tag/PhantomX-0.2.1) | Gameplay mod: Supreme Commander's Phantom-X. Everyone starts allied until secret phantoms are chosen and fed a share of everyone's income; paladins, marks, timed reveals and the phantom war, all on an in-game panel |
| [UnitRestrictions](UnitRestrictions/) | [**0.1.1**](https://github.com/Remmyboy/sanctuary-mods/releases/tag/UnitRestrictions-0.1.1) | Gameplay mod: the host takes units out of the match: land, air, naval or experimentals as a whole, a kind of unit for every faction, or one faction's unit alone |
| [BalancePatch](BalancePatch/) | — | Gameplay mod: a community balance pass. Teching up pays, commanders earn less, land costs alloys and air costs energy, engineers can be raided, artillery and bombers lead their targets, point defences see their range, and fixes such as the EDA T3 fighter shooting the ground |
| [MapLocalFiles](MapLocalFiles/) | — | Lets Lua read files from the loaded map's folder |
| [ModLoader](ModLoader/) | [**1.5.0**](https://github.com/Remmyboy/sanctuary-mods/releases/tag/ModManager-0.15.0) | Loads and hot-reloads every mod above from `SanctuaryMods`; ships with the Mod Manager |
| [ModApi](ModApi/) | [**1.8.0**](https://github.com/Remmyboy/sanctuary-mods/releases/tag/ModManager-0.15.0) | Ships with the Mod Manager. The framework's stable core: gameplay mods applied per lobby, the Start check, modded replays, art packs, factions, AIs per seat, Lua panels for gameplay mods (no DLL needed), unit-list options with a picker, and the API mods are built on |

[All releases](https://github.com/Remmyboy/sanctuary-mods/releases) · MapLocalFiles
has no release of its own yet; build it from source if you need it.

## SanctuaryHud

- **Economy strip** across the top: alloy on the left, energy on the right,
  each showing current storage, gross income, gross spend and net per second,
  over a capacity bar that lengthens with your storage and reddens as the store
  heads for empty. `BUILD SPEED N%` appears when a resource can't pay for what
  is queued (what it can pay over what is asked for, which is how fast every
  build now goes), and `WASTING N/s` when a full store earns more than it
  spends. Source: Harmony postfix on `SanctuaryUI.EconomyPanelUI`, the C#
  receiver of Lua's `Engine.UI_SetEconomyValues`.

  While a resource is stalling, its spend figure is what your queue is
  **asking for** (`RequestedTotal`); otherwise it is what the economy actually
  paid (`RequestedStalled`), as on the game's own panel. During a stall actual
  spend is capped by income, so showing it would just mirror the income back
  at you (`+12 −12`) and hide the shortfall. A resource only counts as
  stalling when its own store can't cover a tick of demand, which is the test
  the host's `economy.lua` throttles on: construction draws alloy and energy
  together and is slowed by whichever runs short, so an energy stall cuts
  alloy spending as well, and a full alloy store must not show a stall
  because of it. Net stays on actual spend, since that is what really moves
  the store.

  The rates are smoothed every frame on real elapsed time towards the latest
  update, with a fixed quarter-second time constant, so the strip trails the
  game's own panel by the same small amount at any frame rate, and income and
  net trail it by the same amount as each other. Storage is never smoothed.
  The strip stays up through a pause: the game's economy panel being visible
  counts as "in a match" even while the stream is silent.
- **Looks**: the strip is uGUI on the game's HUD canvas like the rest
  (see SanctuaryUI below): its texts are the game's own TextMeshPro font,
  its alloy and energy marks the game's own icons, and the tints come off
  the game's own panel, on the same near-black blue with an accent-blue
  hairline as the front menu. The commander widget is built the same way.
  Numbers abbreviate exactly as the game's readouts do (`1.2K` above 999).
- **Hide the game's own bars** (`Top bar · HideGameEconomyBars`, off by
  default, in the Mod Manager's settings): switches off the built-in alloy
  and energy readouts so the strip is the only economy display. The buttons
  that share that panel (menu and pause among them) move into the middle of
  the strip, between alloy and energy, drawn with the game's own icons and
  its version line under them; hovering one names it. A click there is sent
  to the game's hidden original as a pointer click, so each does exactly what
  the game's button does, and an invisible uGUI target under that part of
  the strip keeps the click from also landing on the map. It all comes back
  whenever the overlay is hidden with **F10**, and when the mod unloads.
- **Size** (`Overlay · Scale`, 0.6 to 1.5): the whole HUD — the strip, the
  commander widget, and the SanctuaryUI rows and card — drawn larger or
  smaller together, on top of the game's own UI Scale. At 1 the strip is
  its usual size and the rows and card a fifth up on their first release,
  which read small. Each panel also has a size of its own on top of it, set
  by dragging the grip in its corner: the strip (`TopBar · StripScale`) and
  the commander widget (`TopBar · CommanderScale`) from their bottom-left,
  the bottom panels together (`BottomPanels · Scale`) from the top-right of
  the rightmost one, and the mini-map from its bottom-right. `TopBar · Locked`
  and `BottomPanels · Locked` take the grips away. The gross in and gross out figures sit one
  over the other beside the net, so the two figures being compared line up.
  Each half leads with its resource's mark — an ingot for alloy, a bolt for
  energy — in place of the word; the same marks sit in front of every alloy,
  energy and build-power figure across these mods (a hammer for build
  power, a clock for time), so a figure never needs a label.

### SanctuaryUI

The HUD's own versions of the game's panels along the bottom of the screen,
under one switch (`Bottom panels · ReplaceGamePanels`, on by default) with everything
below it in the same section of the Mod Manager page. Off leaves the game's
panels exactly as they are and the rest of the settings do nothing. Each
stand-in keeps the game's own panel running underneath at zero alpha — Lua
goes on filling it in and flipping it with the selection, and its buttons
stay live — and gives it back when the overlay is hidden with **F10**, when
the switch goes off, or when the mod unloads. All of them step aside under
the game's menus, the F8 Mods page and the result screen, as the strip does.

They dock into one panel: a left column the width of the game's own orders
and information panels, the unit card standing on the orders row, and the
build area against its right edge, the selection row and the options along
the bottom with the tier tabs and the queue above them, no gaps between.
One hairline runs along every exposed top edge of the combined shape, with
thin dividers where pieces meet.

- **Orders row** (`OrdersRow`) in place of the game's orders panel
  bottom-left. The game draws all twenty-one order buttons for any selection
  and dims the ones that don't apply; of the bright ones only Stop and the
  toggles (pause, repeat build, shield, intel, production) do anything when
  clicked in the current build — the Lua registers no click function for
  move, attack, patrol and the rest, which are hotkeys and right-clicks
  anyway. The row shows only the buttons the game has enabled for the
  selection, and with `HideUnwiredOrders` (on by default) only the ones that
  are wired, so a factory gets pause, repeat build and stop and a tank gets
  stop. Each button is a clone of the game's own — its dashed plate in the
  order's colour, its icon, and the frame its glow shader lights while the
  toggle is on or the mouse is over it — mirroring the concealed one and
  passing it every click, so whatever Lua hung on it runs unchanged. The
  button's name comes up as the game's own tooltip.
- **Unit card** (`UnitCard`) in place of the game's unit information panel.
  The game's card is a bitmap mock-up with fifteen text fields over it: the
  template id next to the name, income to three decimal places, and no
  telling which figure is which without learning the picture. The
  replacement draws from the same values (a postfix on
  `InformationPanelUI.SetValues` catches every update): the class as the
  title ("Tier 3: Tank") with the unit's own name beside it, the shield as
  a gauge above the health where the unit has one (read from the client,
  since the game's card values never carry it; while the shield is coming
  up the bar is its recharge instead, and turns into the shield amount once
  it is up), a health bar with `current / max` and regen, armour and bubble
  shields only when the unit has them, build progress while it is being
  built, then what it adds
  to or takes from the economy per second behind the resource marks and
  only where it is not zero, and build power behind its hammer. Figures
  round and abbreviate like the strip; no template id, no build cost. It
  steps aside while more than one unit is selected — the game's card
  describes one unit of the group, whichever came first — unless a unit is
  under the mouse, when it is about that unit.

  A **factory or engineer** shows what it is working on: the current job's
  art, large, at the right of the usage band with its percentage under it,
  and the rest of its queue as small tiles beside it, counts in their
  corners. For an engineer that is assisting, the job it is helping with is
  the one shown even though it is not in its own queue. This comes from a
  Lua query four times a second while the card is up (the unit's
  `predictedBuildQueue` and its `buildTarget`'s progress over that target's
  `buildTime`); the card keeps its width, so the queue shows at most two
  small tiles beside the job.

  Hovering a **build option** turns it into a build card: one line of alloy
  cost, energy cost and time behind their marks. The alloy, energy and
  build-power marks are the game's own: its card draws them as icon glyphs
  in a TextMeshPro font, which are cloned; the clock is the HUD's. The time is the template's
  `buildTime` over the selected builders' build power — engineers assisting
  one job add up, a factory builds alone, so the strongest selected one
  counts. With the replacement off,
  `TidyGameUnitCard` (on by default) still hides the template id on the
  game's own card and rounds its income figures.
- **Selection row** (`SelectionRow`) in place of the game's selection list,
  which stacks the selected unit types upwards in a narrow column on the
  left edge. The same buttons — the game's own plate, portrait, strategic
  icon and count, in the build area's tile shape — stand as a row along the
  bottom at the left end of the build options, with a clear break between the game's groups (air, land, naval,
  structures). Left click keeps just
  that type, right click drops it, both passed to the game's own button.

  The row and the build strip are built the game's own way rather than
  drawn: uGUI on a root of the HUD's own placed on the game's HUD canvas,
  just above its panels. Each tile is a clone of the build panel's own
  button prefab (72 by 104 canvas units, so every row matches) with the
  game's element script taken off, mirroring one
  of the concealed panel's buttons every frame and passing it every pointer
  event. What that buys over the IMGUI stand-ins: the game's UI Scale
  setting applies, the count text is the game's own TextMeshPro in its own
  font, a click over it never reaches the map (the game gates on its event
  system, so no invisible shield is needed), and a fault in one tile's
  update cannot stop the rest of the HUD taking clicks. The orders row and
  the unit card are built the same way (the card's texts are TextMeshPro in
  the game's font, its gauges filled Images), and so are the EcoManager and
  IdleEngineers panels, on the shared helpers in `shared/HudCanvas.cs` and
  `shared/HudPanel.cs`, as are the economy strip, the commander widget, the
  alerts and the mini-map. Only the labels over the map (reclaim values
  and build countdowns) are still drawn in OnGUI, which suits them.
- **Build strip** (`BuildStrip`) in place of the game's build options, tier
  tabs and build queue, each of which is a dashed panel with paging arrows
  and "coming soon" placeholders. The HUD lays the bottom out itself from
  where the game's strip starts: the selection row, then only the options
  the selection actually has (placeholders are recognised by the default
  portrait they wear); above the options the tier tabs, only when more than
  one is live, then the queue with its counts and progress. A long list
  wraps onto further lines upward rather than running off the screen. Every
  tile is a clone of the game's own button standing in for one on the
  concealed panel, and every tier tab a clone of the game's own toggle, so
  clicks (shift and right included) and hovers do what they do on the
  game's panels, and a tab press sends the click the game's toggle needs to
  light up.

  **Drag a queue tile to reorder the factory's queue** (`QoL · ReorderQueueByDragging`,
  off by default, like everything in the QoL section). A bar shows where it will land. Dropped at the very front,
  it cancels what the factory is building and starts the moved item instead;
  anywhere else, the current build carries on. The host has no reorder: its
  one queue command adds or takes away N of an item, and a new item goes on
  the end. So a move takes off everything from the first position that
  changes to the back, last first, and puts it back in the new order, each
  step predicted and sent the way a queue click is. Taking off the item in
  hand is what makes the host cancel the current build. A move that would put
  a unit ahead of the factory upgrade it needs is refused, since the queue
  would drop it. The drag disarms the press it started from, so letting go
  never also counts as a click on the tile. With several factories selected
  the queue only shows when they share it, and the move applies to all of
  them.

  With the strip left to the game, `HideLoneTierTab` (on by default) still
  conceals the tier tabs whenever no more than one of them can be clicked —
  a tier-1 factory's single T1, or a structure whose only option is its own
  upgrade.
- The buttons carry no template id, but a portrait is a template's
  foreground icon, so a once-a-match Lua query ties every icon to its
  template; that is how a hovered build option names the template for the
  build card.

Each stand-in dumps the game panel's object tree to the log the first time
it conceals it, and a **cost meter** logs the HUD's own Update and OnGUI
time per frame every ten seconds in a match, so "is it the mod" is
answerable from the log alone.
- **Commander widget** top-right: the game's own strategic icon with a health
  bar underneath; click to select the commander and move the camera to it,
  keeping roughly your current zoom (`Top bar · CommanderJumpZoom`). It
  stays away while a replay plays, where ReplayManager's army rows carry
  each player's state and its panel often sits in that corner.
- **Reclaim values** over the map while **Left Alt** is held (`Map labels ·
  ReclaimHoldKey`; `None` keeps them up permanently): the alloys left in every
  wreck and harvestable prop the client knows about, drawn at the spot.
  Values closer together on screen than `ReclaimClusterPixels` (110 at 1080p) are
  summed into one figure at their value-weighted centre, so zoomed out a
  battlefield reads as one number the way FAF's overlay groups it, rather
  than a smear of digits. Energy shows as a smaller amber `E` line only where
  it is the point. `ReclaimMinValue` hides trivia. The prop table
  (`__Entities.Props`) holds wrecks and map props alike with their template's
  `economy.harvest`, and the host streams `reclaimProgress` as they are
  eaten, so what is left is `harvest × (1 − progress / harvestTime)`. A
  decayed wreck stays in that table with no render entity behind it, so each
  is checked with `Engine.IsValidLocalID` before it counts. Positions are
  cached Lua-side per prop; only the values are re-read, once a second.
- **Build countdowns** (`Map labels · BuildCountdowns`): under your structures still
  under construction, upgrades included, a `m:ss` time-to-finish and a thin
  progress bar. The rate is measured from successive progress samples
  (half-second poll, filtered), so it reflects whatever is actually assisting;
  before anything has been measured the template's own build time stands in.
  The colour says what is happening to it: normal while something is
  building it, dark orange while your alloy or energy is stalling (a stall
  throttles every build), and red once nothing is building it and it hasn't
  moved for a few seconds, an abandoned site; the estimate stays either way,
  since there is no better number. What is building a site comes from the
  units whose build routine targets it (`isBuilding` / `buildTarget`). An
  upgrade whose upgrader is paused and
  that nothing is building is left out altogether; an engineer assisting it
  builds straight through the pause, and then it shows. A game pause (the
  economy stream going quiet) freezes the clocks instead. Labels are placed
  soonest-first, one that
  would overlap another is skipped, and `MaxBuildCountdowns` (12) caps them, so a
  busy base shows the handful nearest completion rather than a wall.
- **Alerts** (`Alerts · …`): toasts top-centre under the strip, with a short
  sound at `Volume` when `Sound` is on (it is off by default; voice packs
  below). *Commander under attack* on
  any health loss, re-sounding at most every eight seconds while it goes on;
  *commander critical* once below `CommanderCriticalAt` (35%), re-armed after
  repair; *structure complete* when a countdown finishes; *player
  disconnected* (`PlayerDisconnected`), a white toast naming them when the
  game reports a player dropping out, which it otherwise shows as small text
  top right, and one when your own connection to the host is lost. That
  comes from a postfix on `LogPanelUI.AddLogText`, the method both notices go
  through; the game's own line stays, several such toasts can stand at once,
  and a player quitting reads the same as one disconnecting, since the game
  sends the same message for both. Clicking a commander
  toast jumps to it like the widget does. Health is re-read four times a
  second off the commander's cached sim id, since the once-a-second ECS poll
  would make the alert a second late. All of it is for the active player
  only: "own" means exactly one focused army, so a replay's all-armies view
  (every army focused) and a seatless observer (none) get no commander
  tracking, countdowns or alerts, rather than both sides' at once; watching
  a replay from one seat gets that player's.

  **The strip and the commander widget follow the same rule**, and step
  aside entirely in the all-armies view — there is no single economy to
  report there, and what stood on screen was the last seat's figures going
  stale. The game's own readouts come back while they are away, so that
  view is never left with no economy display at all. The seat is read from
  the client's own focused army; a value it cannot read counts as focused,
  so a failed read is never what
  makes the HUD vanish. The mini-map stays up regardless: it is the one
  thing here that reads just as well watching everybody.

  Which completions get a toast is one switch each under `Structure alerts`,
  by role (factory, radar, extractor, energy, defence, tech centre,
  strategic, other) and separately for a fresh build and for an upgrade to
  the next tier, plus `AnyTier4`. Defaults: factory and radar upgrades,
  tech centres, strategic weapons and anything tier 4; not a new extractor
  or generator. The role comes off the template's tags, the tier off
  `general.techNumber`, and "upgrade" from the `upgrader` link the game keeps
  on the new-tier entity while the upgrade runs (remembered, since the game
  clears it the moment it lands). The built-in tones are synthesised: under
  attack is a falling 520→390 Hz pair, critical three 330 Hz pulses,
  complete a rising 660→880 Hz pair. Voice packs replace them: a subfolder
  of `SanctuaryMods\SanctuaryHud\sounds\` holding `commander-under-attack`,
  `commander-critical`, `structure-complete`, `structure-upgraded` and
  `player-disconnected` WAVs (a line a pack lacks gets the tone)
  (any PCM or float WAV, any rate or channel count), chosen with
  `Alerts · VoicePack`, a left/right chooser on the Mods page listing the
  packs found at load plus `tones` for the built-in sounds. Three ship:
  `machine` (a female-voiced synthetic combat AI, the default),
  `announcer` (a military radio read) and `caretaker` (Sanctuary's own
  intelligence, calm and vast), in that order, with any pack a player adds
  after them; a loose WAV in the sounds folder itself is the fallback for a
  line a pack lacks.
  `Alerts · Volume` is 0 to 100 like the game's own audio sliders, and
  shows as one on the Mods page. The game's audio runs through Wwise and a Unity AudioSource
  plays nothing in this build, so the mod goes round the engine: it
  renders each sound at the configured volume to a 16-bit WAV in
  `BepInEx\cache\SanctuaryHud\` and plays that through the Windows
  waveform API (`winmm` PlaySound), which the system mixer handles like
  any other app's audio. Files under the project's `sounds` folder deploy
  with the build and ship in the release zips. Sound is off by default
  (`Alerts · Sound`); the toasts show regardless.

### Mini-map

The mini-map the game doesn't have. Sanctuary ships no map panel at all —
there is no `minimap` anywhere under `engine\LJ\lua`, and `UIPanelType`, the
complete list of the game's own panels, goes `Information, Orders,
Construction, ConstructionFilter, ConstructionQueue, Selection, Log, Economy,
PauseMenu, GameResult, Chat` — so today the only way to know what is happening
away from your screen is to pan there.

A panel showing the map from above, shaded where you cannot see, with every
contact you are allowed to see drawn as its strategic icon in its army's
colour, and the alloy deposits nobody has taken yet. Clicking or dragging
anywhere on the map moves the camera there; the border drags the panel and the
bottom-right corner resizes it (`MiniMap · Locked` stops both, and the panel stays wholly on screen). **F2** shows and hides it (`MiniMap · ToggleKey`).

There is deliberately no outline of what the camera is looking at. One was
built and then taken out again: at the zoom levels that matter it is either the
whole map — a camera at 648 units with a 50° field of view sees 1057 × 595 of
world, which is past every edge of a 512 map — or a small box that tells you
little you can't see from the screen itself.

Buildings that are only queued — placement ghosts, which the game draws on the
battlefield as outlines — are left off: on a mini-map they would read as
finished structures, which is worse than not showing them at all.

**The backdrop is the map's own preview.** Every map folder ships a
`preview.png` beside its `.sanmap` (512², 1024², 400² or 256², depending on the
map), and the file is simply read and decoded. The game's own loader,
`LobbyManager.GetMapPicture`, looks like the tidier route and was the first
thing tried — but it goes through the gamedata index, and starting a *second*
match or replay in one session calls it at a moment when that index throws,
after which the map has no picture for the rest of the game. Reading the file
depends on no game state, so it cannot fail that way. What you get with the
preview are the numbered start-position circles, which are baked into the
image; they are a fair price for a backdrop that exists for all 150 maps and
needs no work per map.

**Which way up was measured, not assumed.** A flipped z axis is the one error
here that looks perfectly fine — the map is simply mirrored, and nothing about
it reads as wrong. Ambush Pass is 256×256 with its `Spawn` markers at
`ARMY_1 (x 98, z 228)` and `ARMY_2 (x 164, z 40)`; on its preview those
numbered circles sit at `(0.385, 0.107)` and `(0.639, 0.842)` as fractions
from the top-left. Two points, both axes: x runs left to right unflipped, and
world **+z is up** the image. The transform keys off map size — which ranges
from 256 to 2048 across the shipped maps — rather than assuming a scale.

**The mini-map shows the playable area, not the whole world.** On most maps
those are the same thing. On six — Seton's Clutch, The Forge, There Is Time,
Theta Passage, Two Step Shuffle and White Desert — the terrain is twice the
size of the play space, and the map carries an area named `PlayableArea` that
fences play into the centred half; the game's own `GetDefaultPlayableArea`
looks that name up and puts its barriers there. Each map's preview is rendered
of that playable area, so framing the mini-map on it makes the picture fill the
panel and puts every unit in the right place.

That was not the first reading. Projecting every army's Spawn marker on all
150 shipped maps against the start circle baked into each preview separates
144 full-map previews from exactly those six, which first looked like previews
left stale after a map was scaled up — and the first release drew the picture
into the middle of a world-sized panel, leaving three-quarters of it an empty
black border. The six are not stale; they are fenced. The framing now comes
from the game's own playable area rather than a list of names, so it holds for
a custom map too. (A map that names its rect just `Playable`, as Fields of Isis
does, is not matched by the game's lookup and plays across the whole map — and
its preview is full-map to match.)

**It shows what the game shows, and no more.** Contacts go through the
client's own `ClientUnit:IsHighlightable()` — vision or radar, and not an
upgrade shell — so the set of contacts on the mini-map is exactly the set the
game is already drawing on the battlefield.

**A radar contact is drawn as the game draws it.** `OnIntelRadar`
*disables* a unit's strategic icon and enables a separate radar icon, which
`unitTemplateLoader` builds as `<shape>_<tech>_none_normal` — the same plate
with the role symbol left out — and draws pure white rather than in the army's
colour. So a radar blip tells you roughly how big a thing it is and nothing
else, and that is what the mini-map draws too: the blank plate, in white. Only
contacts you can actually see get their real icon and their owner's colour.

Those icons come from the strategic icon atlas the game binds as a global
shader texture, looked up by the image names the *template* records for each
of the two cases. Icons are registered during match load, so a contact seen
before the registry has filled in draws as a plain square and picks up its
icon on a later poll.

**A click moves the camera without changing the zoom.** It goes through
`cameraController.FitCameraToPositions`, the client's own mover — the one the
game uses to focus a control group — which fits the bounding box it is handed
but only ever outwards: it takes the greater of the fitted height and the
height the camera is already at. Handing it a *small* square around the target
therefore leaves the height exactly as it was and moves the camera and nothing
else, which is what a click on a mini-map should do. Dragging is a continuous
pan, rate-limited to about 16 a second, since each one is a chunk across the
Lua bridge, and the release always lands exactly where you let go.

One thing that comes with going through the game's own mover: it rebuilds the
camera rotation from the pitch alone, so **a click straightens the camera to
north-up** if you had rotated it. That is exactly what the game's own focus key
does to a control group, so it is at least consistent, but it is worth knowing
before you use the mini-map mid-fight with a rotated camera.

An IMGUI panel is invisible to Unity's event system, and the game's input only
declines a click when it is over uGUI — so without help the same press would
also land on the battlefield, clearing the selection or starting a box drag.
An invisible uGUI raycast target stands under the panel while it is drawn,
which is enough to make the game ignore it; that is the same trick the HUD's
economy strip uses, and it now lives in [shared/HudCore.cs](shared/HudCore.cs)
so both use one copy. The panel's border is its handle and the corner resizes
it, so a press on the map itself is always a camera move and never a nudge of
the window. While a drag is in progress the shield covers the whole screen,
because the *release* is what the game acts on: letting go outside the panel
with a building queued would otherwise plant it wherever the cursor ended up.

**The fog** shades the whole map and lets the units you share intel with lift
the shade around themselves, so an empty patch reads as nothing there rather
than nothing known (`MiniMap · ShowFog`, `MiniMap · FogDarkness`).

The obvious source for it is the game's own fog buffer — `FowPass` renders the
focused army's intel into a render texture and publishes it as the global
`_FowBuffer` — and that is what this did first. It is wrong, and instructively
so: the pass builds its renderer list from `ctx.cullingResults` of the **main
camera**, and only overrides the *matrices* to the map-wide orthographic view.
So the vision volumes that reach the buffer are culled to whatever the player
happens to be looking at, and zooming in erases vision from the rest of the
map — which on a mini-map put fog over the player's own base.

Coverage is therefore worked out here instead, from the `intel.visionRadius`
each unit carries on its template, rasterised into a small mask. That is not an
approximation of the game's rule — it *is* the rule: intel is a plain collider
overlap, a detector volume of that radius against each unit's signature volume
(`host/managers/intel/IntelDetector.lua`, `OnColliderEnter`/`OnColliderExit`),
and there is no line-of-sight or occlusion test anywhere in the intel system.
Terrain does not block sight in this game, so a circle is the whole of it.

Only units whose intel you actually share report a radius, so an enemy can
never lift your fog, and the all-armies observer view a replay uses reports
none at all, since nothing is hidden from it.

Two things the shield can't help with, since the game gates only clicks on
"is the cursor over UI": the **scroll wheel** over the panel still zooms the
world, and a panel dragged hard against a screen edge sits in the 8-pixel
**edge-pan** border, so the camera will creep while you use it. The default
position is inset clear of that.

**Alloy spots** are the deposits with no extractor on them yet. The game's own
rule is not simply "enabled": `ClientAlloyResourceSpot.RecalculateRendering`
hides a marker while the spot is disabled *or* while an extractor the player
can see is standing on it, and both tests are repeated here. That is
deliberately not read off the marker's own flag, because CameraUtilities' "hide
alloy spot markers" switch writes that flag — and running both mods should not
silently empty this layer.

Contacts are re-read a configurable 8 times a second (`MiniMap · RefreshHz`, 2 to
20); army colours and deposits every two seconds, since they barely change.
The contact sweep is a single Lua chunk that walks each army's units, applies
the visibility test inside the chunk so invisible units never reach the
payload, and numbers the distinct icon names it saw so a name is sent once
rather than once per unit. Colours are read off the army object rather than
derived, because they are handed out by registration-order colour id and not by
lobby army id — deriving one lands on somebody else's colour.

In a replay the mini-map follows whichever view ReplayManager is showing,
the all-armies view included, without any special case.

Hotkeys: **F10** toggles the overlay, **F2** the mini-map, **F9** dumps the UI
hierarchy to the log.
Everything the HUD draws steps aside while the game's pause menu or a
front-end screen (settings, the Mods page) is open over the match, as the
game's own HUD does: IMGUI would otherwise draw on top of them.

### Match stats

The post-game screen FAF players know from the score panel (`Match stats ·
Enabled`, on by default): when the game's VICTORY or DEFEAT text comes up, a
window opens over it with the whole match for every army. Nothing shows while
the match is being played. **CLOSE** leaves a **MATCH STATS** button under the
game's result text to bring it back, and **F3** (`Match stats · ToggleKey`)
opens and closes it too, but only once the match has a result. `AutoOpen` off
keeps it to the button. (This was its own mod, MatchStats, before 0.14.0.)

- **The table**, one row per army, allies together (the best-scoring team
  first, by score within it), each in its colour with its faction, the
  result (and when a defeated army went out), and YOU on your own row. Both
  tabs lead with the **SCORE**. Two tabs:
  - **ECONOMY**: alloy and energy gathered and spent, the peak alloy
    income, alloy and energy **wasted** (income above spend while the store
    sat full), and the time spent **stalling** on each (ticks where the
    host throttled spending below what was asked for).
  - **UNITS**: built, by land, air, naval, engineers and structures, with
    the alloy value built; lost, with the alloy value lost; the alloy value
    of enemy units killed; the peak army value and the peak number of units.
- **The charts**, one line per army over the match, time along the bottom:
  score, alloy income, energy income, alloy spent (those three smoothed over ten
  seconds, since a raw second jumps with every reclaim and stall), alloy
  gathered as a running total, army value, units, and alloy stored. Hovering
  the chart puts a cursor on it and a readout of every army's value at that
  moment, highest first. Your own line is drawn thicker and on top.

**Where the figures come from.** The HUD counts from the game's own unit
and economy updates as the match runs, by wrapping those commands' `Receive`
fields in the client VM — a table-field swap, no file touched, so the lobby's
Lua hash is unchanged — and keeps count there, in the client's memory,
reading it out every two seconds. Nothing is sent anywhere, and nothing is
shown until the match has a result. The counting hooks are shared source
(`shared/Stats`) with LadderReporter, whose opt-in stats upload reads the
same figures; whichever of the two mods goes first in a match installs them
for both.

- Economy figures are per tick in the stream (`economy.lua` adds income
  straight into the store), so a second's figures are ten ticks summed.
  Income already includes reclaim: the host computes `income = generation ×
  multiplier + harvest`.
- A unit counts as **built** when it finishes, which counts an upgrade too
  (the new tier is a new entity); a placement ghost never finishes, so never
  counts, and the commander isn't "built". A `DestroyUnit` is a **loss**
  (killed, dashed through, self-destructed); a removal — an upgrade's old
  tier, a cancelled site — only takes the unit off the army value.
- **Army value** is the alloy cost of the finished mobile units alive,
  commander aside. The commander counts as a loss when it dies, but not in
  the lost value, where its cost would swamp everything else.
- **Kills** are shared. The host's `DestroyUnit` carries no killer (the
  game has no score of its own either), so each loss is split evenly among
  the loser's enemies still in the game. In a 1v1 that is exact; in a team
  game it credits the team, evenly across its players; in a free-for-all it
  is an approximation.
- Empty map slots get an army from the host but never any storage, and are
  left out, and take no share of a kill.

**The score** is FAF's (`CalculateBrainScore` in its `lua/sim/score.lua`)
with alloy for mass:

```
score = (alloy spent + energy spent / 10) / 2
      + max(0, ((alloy killed − alloy lost) + (energy killed − energy lost) / 10) / 2)
      + 5000 × commander kills
```

Half of everything spent, plus half of the battle's net value, which never
goes below nothing, plus a bonus a commander. FA brings energy to mass at
20 : 1; Sanctuary's unit costs are set at 10 : 1 (the median over its 295
unit templates, and nearly every one), so that is the rate here. FAF takes
a commander's own value back out of a kill so that the kill is worth only
its bonus; a commander here costs 100,000 alloy, so it is never counted in,
killed or lost.

**What it can't see.** Only what happened while the HUD was loaded: hot-load
it mid-match and the charts start there, and the totals count from then
(units already standing are tracked, but not counted as built). Turning
`Enabled` off mid-match hides the screen; the counting already hooked in
carries on until the match ends. The window is uGUI on the game's HUD canvas,
just above the result panel, so it takes the game's UI Scale and font; its
dimmed backdrop takes every click. It sits beside the HUD rather than on it,
so hiding the HUD (F10) doesn't hide it. The first time the result screen
comes up, the HUD logs the result panel's object tree.

### QoL

Everything here lives in the **QoL** section of the Mods page and is **off by
default**, except the queue right-click fix and the commander delete check
below; the factory queue drag above (`ReorderQueueByDragging`) is there too.

**Deleting your commander asks first** (`ConfirmCommanderDelete`, **on** by
default). Delete (or Ctrl-Delete) with your commander in the selection opens a
popup — blow it up or cancel — instead of the commander going at once;
pressing Delete again confirms, and it cancels itself after 8 seconds. Any
selection without the commander deletes as before. It swaps the two delete
functions on `client/simpleEvents.lua` for ones that hold the order while the
popup is up, then send the game's own order for the same units.

**Right-clicking a queue item takes from that item** (`QueueRightClickTakesClickedItem`,
**on** by default, as it fixes the game). Factories start with repeat build on,
and each finished item goes back on the end of the queue carrying the same
queue ids as the original (`HostFactory:CompleteBuildQueueItem`). The game's
right-click sends one of those ids and the host takes from the back, so with
`Tank ×2 | Raider | Tank ×1` a right-click on the first tank removed the last
one. This swaps the queue panel's click handler: a right-click sends an id no
later item shares, or, when every id is shared, takes the items behind it off,
takes from the clicked one and puts them back. Left clicks are the game's own.
It covers the game's queue panel and the build strip; the commands are the
game's own, so it works against unmodded hosts.

**Match clock** (`ShowMatchClock`): the time since the match started and the sim
speed (`12:34   1.5×`, or `PAUSED`), under the menu buttons in the middle of
the economy strip, where the game's version line was. The game has no clock.
The speed is the one the host last announced (its "Speed changed to …" log
line), since a client's own engine speed need not follow the host's.

**Right-click cursors** (`RightClickCursors`): the cursor shows what a
right-click there would do — a sword for attack-move (with Alt held), a
crosshair for attack, an open hand for assist, a wrench for repair (which also
resumes a half-built structure), a pickaxe for reclaim, a fist for capture —
and the pack's plain arrow for a move and everywhere else over the map. The
UI, the pause menu and the Mods page keep the normal cursor. The game never changes its cursor, so without this there
is no telling whether a right-click on a frame will assist it or finish
building it. It runs the same decision the game's right-click handler makes,
from the same inputs, every frame. The cursors are from Kenney's
[Cursor Pack](https://kenney.nl/assets/cursor-pack) (CC0), embedded in the DLL
and scaled to the screen: 32 pixels at 1080p, 48 at 1440p, 64 at 4K.

**Ctrl-A selects every unit of the selected types** (`SelectAllOfSelectedTypes`, on Ctrl-A):
with a T1 tank and a T1 scout selected, Ctrl-A selects every finished T1 tank
and T1 scout of yours on the map. The game's double-click does this for one
type and only on screen. With nothing selected, Ctrl-A keeps the game's own
meaning (hold it to box-select only air units). It swaps the press handler on
the game's live key map, as BuildHotkeys does, so no file changes.

**Factory rally points you can see.** Right-clicking the ground with a factory
selected sets where its units go, but the game keeps that point on the host
(`HostFactory.rallyPoint`) and never tells the client, so nothing draws it.
This remembers the point your client sent and draws it the way a move order is
drawn — a line from the factory and a blue marker — for the selected
factories, and for all of your factories while **Shift** shows every order. An
upgraded factory keeps its rally point on the host, and here too. Points set
before the mod loaded, or in an earlier match, are not known.

**Waypoints you can drag.** Left-press a move, attack-move or build marker —
your selected units', or any of yours while Shift is held — and drag it; let
go to move it there. A building shows its placement ghost as it moves, with
the usual green and red footprint, and an extractor snaps onto the nearest
deposit. Rally points drag the same way. Right-click while holding one puts it
back.

**Ctrl-click** a move, attack-move or build marker to delete that waypoint
from every unit that has it; deleting a unit's only waypoint stops it.
**Double-click** a marker to select the units sharing it (Shift adds them to
the selection); on a rally point that is its factory. A single click on a
marker over open ground does nothing, where it would otherwise empty the
selection and take the markers with it; over a unit it is the ordinary click.
Only positional markers take these clicks, so Ctrl-clicking a unit that
happens to be the target of an assist or attack still toggles it in the
selection.

The game has no way to edit an order: the host takes only *add* and *clear*
(`commonOrders.lua`), and the game's own order manager carries
`todo !! draggable waypoints`. So moving a waypoint **re-issues the queue** of
every unit that shares it, with that one position changed, through the same
`ClientIssueOrder` a right-click uses. Units with identical queues go as one
group, so a shared order stays shared. Other orders in the queue go back as
they were: a move with its formation alignment, an assist or attack on the
same target, a building on the same spot. A building someone has started
comes back as *repair* of the frame, which continues its construction; a
building still at 0% is deleted with its order on the host, so the mod clears
the queue first and places it again once the old placement has gone. That
costs the engineers a brief pause (a sim tick plus your latency), during which
the queue they are about to get is drawn in its place. The game only
re-predicts orders on its next sim tick, so the mod runs that prediction
itself the moment it re-issues, and the marker lands where you dropped it
rather than flicking back first. Markers on
a target (attack, assist, repair, reclaim, capture) do not drag, and neither
does a building already under construction.

To re-issue an order the mod needs what it was issued with (which building,
which target), which the client does not keep. It records that from your own
order commands, so a queue holding an order from before the mod loaded does not
drag.

Everything it sends is a command an unmodded client sends, and no Lua file
changes, so it stays lobby-compatible. `QoL · ShowRallyPoints`,
`QoL · DraggableWaypoints` and `QoL · WaypointGrabPixels` (how close a press has to land)
are in the QoL section of the Mods page, and the first two are off by default:
turn them on to use them. They work with the overlay hidden too:
they are controls, not display.

## IdleEngineers

The idle panel, in the shape of the eco panels: one clickable tile per tech
tier of idle engineers, stacked down a column with the commander on its own
line above them — clicking selects that group. A tile is the unit's own
build-menu art on the game's own plate (brown for land, blue for water),
with the count in the corner and the tier under it. Hidden entirely when
nothing is idle; only as wide as what it is showing; draggable within the
screen, and its position persists (`Panel · PosX`, `PosY`), with `Panel ·
Scale` for its size and `Panel · Locked` to stop it moving during a game.
The panel is uGUI on the game's own HUD canvas (see the SanctuaryUI
notes above): the drag is a uGUI drag and a click on a tile stops at the
tile, so nothing of either reaches the map, and the texts are the game's
font at its UI Scale.
On the right half of the screen it keeps its right edge fixed as it changes
width and lays itself out from that edge. It steps aside under the game's
menus, the F8 Mods page and the result screen.

**Idle factories** sit underneath (`Factories · Enabled`, on by default):
one FACTORIES heading with a tile per type and tier along a row beneath it.
Clicking the heading selects every idle factory; clicking a tile selects
just those. Switching the setting off hides the section at once and stops
the lookup behind it.

A selected unit keeps its tile: the game turns a unit's idle marker off
while it is selected, so a poll reading only that marker lost every idle
engineer on the click that selected them, and the panel emptied itself. The
poll now also asks the client's Lua which selected units are idle by the
game's own test (finished, no order, nothing queued).

A factory counts as idle exactly when the game draws its idle adornment:
finished, no order, nothing in its queue — the same rule the game applies to
engineers, re-checked whenever the order or the queue changes. So a factory
that is upgrading, or paused with something queued, is not idle. The type is
read from the client's tag tables (`LAND_FACTORY`, `AIR_FACTORY`,
`NAVAL_FACTORY`) rather than the strategic icon, because the T3 naval
factories ship with the air symbol and would otherwise file under AIR; the
tier is the template's `general.techNumber`. Factories are picked out in the
same once-a-second sweep over your own army's units that finds EcoManager's
extractors, and only while the setting is on.

Idle state and unit identity come from the DOTS icon buffers rather than
Harmony hooks, because the icon FFI receivers are Burst-compiled and cannot be
patched. Selection and camera moves run through the client's own Lua via an
emitted call to `luaL_dostring` — client-side only, so still lobby-compatible. That
plumbing lives in [shared/HudCore.cs](shared/HudCore.cs), which is compiled
into each mod that needs it — so each DLL is fully standalone, at the cost of
each running its own copy of the once-a-second poll.

The row art is *not* the strategic icon the poll identifies units by: that is
the abstract map symbol, shared across factions and tiers, so it could never
tell a T2 engineer from a T3 one. The build buttons draw a per-template
`.sansprite` instead, loaded through the game's own pipeline into a registry
keyed by `AssetID`. `SanctuaryUI.Utils.TryGetLoadedSprite` is the public way
in, and `EM.Core.AssetID` wraps exactly the `uint` Lua reports as
`general.foregroundIconID.index`, so the army sweep also keeps one
representative template per row and hands its icon id to that lookup. Sprites
are windows into a packed atlas, so each is drawn through its own
`textureRect`. Art and label are both kept: the art is what you recognise, the
label is what makes the tier certain. Art the game hasn't loaded yet is retried
each second, and if the lookup is missing altogether the rows are just
labelled.

## EcoManager

Two panels in the shape of FA's UI-Party eco manager.

The **BUILD** panel is two columns of tiles, alloy on the left and energy on
the right, one tile per template your army has **under construction**: the
unit's build-menu art with its build progress along the top, that column's
spend in the corner, the count, and the tier. Each column is headed by its
total and sorted by its own resource, so the top of the left column is what
is eating the alloy and the top of the right what is eating the energy.
Hovering a tile shows both rates, the progress, and how many builders are on
it, in the game's own tooltip. `Build · MaxRows` (8) caps the list at the
biggest spenders of each column, so a late game is not a wall of tiles. **Left-click selects the builders** working on that template — engineers,
factories, or the extractors upgrading themselves. **Right-click pauses them**,
and right-click again resumes them: the tile dims and shows a pause mark while
the panel is holding its builders. That is what an eco manager is for — see
what is eating the economy, and stop it in one click.

The **ALLOY** panel is the extractor tiles, a row per tier: on the left the
extractors sitting at that tier, and on the right — only while any are — a
second tile of the same art tagged UP, counting the ones upgrading away from
it. Clicking a tile selects that group.

Each panel has its own `Enabled` switch, `Scale` and position (draggable,
persists), so either can be dropped or shrunk without the other; `Tooltips`
turns the hover text off for both. The BUILD panel is hidden until the first
build is under way, the ALLOY panel until the first extractor. Both are uGUI
on the game's own HUD canvas, like the idle panel: the column marks are the
game's own alloy and energy icons, a drag never reaches the map, and no
click shield is needed.

### Where the spend comes from

The economy stream only carries army totals, but the client knows enough to
split the spend by what is being built. Every builder holds `buildTarget`
while its build routine runs, and its `buildPower` is kept current by the
host's `SetBuildPower` command; the host's own drain
(`ResourceEntity:RecalculateBuildDrain`) is cost × Σ build power ÷ build
time per second. So one sweep a second over your own units sums the build
power pointed at each half-built thing, groups by template, and has the
demand — the same figure the game adds into "requested" on its economy
panel. Two things to know: it is the *demand*, before a stall scales it
down, so during a stall the tiles add up to more than you are actually
spending; and adjacency discounts never reach the client, so a structure
beside a storage reads a little high.

Factories, engineers, assisting engineers and upgrading extractors all go
through the same build routine, so one sweep covers the lot: an upgrade
shows as its higher-tier template under construction, built by the extractor
below it, exactly as the game models it. Placement ghosts (progress zero) are
queued, not started, and are left out.

Pausing sends the game's own Pause toggle (`RequestUnitsToggle`) for the
builders on the tile, the way the orders panel does. A paused builder ends
its build routine and drops `buildTarget`, so nothing on the client ties it
to what it was building afterwards; the panel remembers what each tile
paused so the same tile can release exactly that. A tile that disappears —
its targets finished, cancelled or destroyed — releases anything it was
holding, since a paused extractor also stops extracting, and unloading the
mod releases everything.

### Extractor tiles

The tier comes off the strategic icon, `structure1_t{n}_alloy_normal`, which is
uniform across all three factions. Upgrading state is the game's own upgrade
adornment — the one `ClientUnit:CheckShowUpgradingAdornment` drives from
`IsUpgradeQueued()` — so it lights and clears exactly in step with the icon the
game itself draws. An upgrading extractor is counted in both its tier row and
the upgrading row, since it is still live at its current tier until the
upgrade completes.

That icon alone is *not* enough to identify an extractor, though: alloy
storages (`ues1602`) and the Tier-3 alloy furnace (`ues3603`) carry the very
same icon, and the render entity holds no template id. So the client's Lua
supplies identity instead — every template is filed into `Tags[tag][tpId]` as
it loads, making `Tags.ALLOYS_EXTRACTION` exactly the set of extractor
template ids, and `Armies[focused].units` exactly our own units. One query per
poll turns that into a set of LocalIDs, which the panel matches against. It
doubles as the ownership filter, so the extractor tiles never depend on the
army-colour match the idle rows use.

### Assist starts the upgrade

Ordering an engineer to assist a structure with an empty build queue does
nothing today — the engineer walks over and stands there — so the obvious
gesture for "help this extractor along" is a dead end. With
`AssistStartsUpgrade` (default on) an assist ordered onto one of your own
finished extractors queues its upgrade first, then issues the assist exactly
as the game would, so the engineer arrives to real work and keeps its order.
It is scoped to extractors on purpose: factories upgrade too, and silently
spending that much because someone assisted one would be a nasty surprise.

This is the one thing in the repo that *acts* rather than displays. It goes
through the game's own client path — the same `ModifyBuildQueue` prediction
and `UpdateQueueAmount` command the construction panel sends when you click
the upgrade button — so the host validates and replicates it like any other
order, and no files change, so the lobby hash is untouched. What it costs you
is that an assist click now spends alloy.

### Paused until an engineer starts on it

Sending five engineers to five extractors starts five upgrades at once, and the
economy goes flat while every one of them crawls. With `AssistPausesUpgrade`
(default on) each upgrade started this way is **paused as soon as it starts**
and released when an engineer actually starts building it — so the cost is
spread over the walk instead of landing all at once, and an engineer that gets
killed on the way never spends anything at all.

"Starts building" is the host's own signal, not distance. The host tells every
client when a builder begins work (`OnStartBuilding`), which sets `isBuilding`
and `buildTarget` on that unit. An engineer assisting an upgrade builds the
upgrade site on the extractor's spot, and that site's `upgrader` points back at
the extractor. So when one engineer walks into a cluster of extractors, only the
one it works on unpauses. An engineer that is merely nearby, still walking, or
has the assist further down its queue releases nothing. Any builder counts, an
ally's included; the extractor building its own upgrade does not.

The pause waits at least `AssistPauseSeconds` after the queueing, and until the
upgrade site shows some progress. Before that the site is still a placement
ghost, which an assisting engineer will not start on, so pausing any earlier
could hold the upgrade for good. An entry whose upgrade never took is dropped.
A cancelled upgrade releases the pause on its way out, so nothing is ever left
stopped with no explanation — and neither is unloading the mod.

Pausing goes through `RequestUnitsToggle`, which takes explicit unit ids, so
none of this disturbs your selection. The watch runs five times a second from
the C# side, because a second's granularity would be visible on both halves.

One thing to know: if no engineer ever starts on it — killed, or re-tasked —
the extractor stays paused with the upgrade queued. That is the safe failure
(it costs nothing), but it is yours to unpause.

The hook is a runtime wrapper around the client's `IssueAssistOrder`:
`inputActions.lua` binds the key to
`Import("client/inputEventsFunctions.lua").IssueAssistOrder()`, resolved at
press time, so replacing that field intercepts every assist without editing a
file. It is removed again when the mod is unloaded or the setting is switched
off, and each match's fresh Lua state reinstalls it.

### Counting

That query counts only completed extractors. An upgrading extractor builds
its replacement as a second entity, present from the moment the upgrade
starts and already wearing the higher tier's icon — so a T1 mid-upgrade would
otherwise read as a finished T2. The T1 stays until the upgrade lands and is
the one carrying the upgrade adornment, so it is what the UP tile counts.
The replacement, meanwhile, is what the build tiles show: the
higher tier under construction, with the upgrade's cost as its spend.

### Odds and ends

- The assist hook only queues an upgrade when the selection holds an
  engineer or the commander: a tank told to assist an extractor just guards
  it, and must not spend alloy on the way.
- Both panels lead with the resource marks — an ingot for alloy, a bolt for
  energy, centred over their columns — in place of the words, the same
  marks the HUD uses everywhere.
- Both panels stay wholly on screen whatever the resolution, and `Panel ·
  Locked` stops either being dragged during a game. They step aside under
  the game's menus, the F8 Mods page and the result screen.

## BuildHotkeys

One hotkey per **role** rather than per panel slot, so the same key means the
same thing whichever faction you are playing, and pressing it again walks down
the tiers.

The game's own construction hotkeys are nine fixed letters resolved by tag
category, first displayed match wins (`constructionPanelHotkeys.lua`). That
leaves two gaps: you cannot reach a *specific* unit — the T1 and T3 tank are
both `Tags.TANK`, so only one of them has a key — and whole categories have no
key at all. Shields, artillery, air and naval factories, tech centres, walls
and storage all render their button with `?` on it.

**A role is a tag expression**, not a list of template ids, which is what makes
it faction-agnostic for free. `PointDefence` is
`DEFENCE * ANTI_SURFACE * STRUCTURE`; that resolves to `ues1001` for EDA,
`ucs1001` for Chosen and `ugs1001` for Guard without naming any of them. The
three factions share 77 of their ~99 roles, and the T1–T3 core — factories,
point defence, anti-air, radar, energy, extractors, tech centres — is
essentially universal, so one table covers everyone.

**Tier cycling falls out of the templates.** Each one carries its own gating
(`BUILDABLE_BY_T2_ENGINEER` and friends), so `GetBuildableTags` already returns
exactly what the selected builder can make. Intersect that with the role, sort
by tech tier descending, and the first press gives you the best you can
currently build. Where a faction lacks a tier the cycle is simply shorter:
only Chosen has a T3 point defence, so **X** gives them T3 → T2 → T1 and
everyone else T2 → T1. No per-faction configuration anywhere.

Cycling continues while the previous press is still uncommitted — its template
sitting on the cursor — which has no time limit, since you may be lining a
placement up. Placing it, cancelling, or changing selection starts the cycle
over. A factory never enters build mode, so repeat presses there queue another
of the same rather than walking the cycle; FAF instead resets its cycle on a
timer, which is what makes its factories cycle too, and `Building · CycleSeconds` (0 by
default, 1.1 to match FAF) turns that on here.

**Escape stops every selected factory**, as it does in FAF, rather than opening
the pause menu. It sends the Stop button's own order rather than editing the
queue: emptying the queue alone leaves a factory that is assisting another one
still slaved to it, and it pulls the next item straight back off that factory's
queue. The host's stop drops the assist along with the queue and the item in
hand. Only your own factories are stopped — a tank sharing the selection keeps
its orders. With no selected factory queued or assisting the key falls through
untouched, so escape still opens the menu; and because there is no getter for
panel visibility, only a setter, the mod mirrors the menu's state by watching
that setter (which the menu's own close button goes through too) so escape
still *closes* the menu rather than stopping a factory behind it.
`Builder keys · StopFactoriesKey` rebinds or blanks it.

`Game interface · PauseMenuKey` moves the pause menu off escape — to **F11**, say; F1 is
taken by the game's debug menu — so it never opens by accident when escape had
no factory to stop. The game's own toggle moves to the new key as it is, and
escape keeps only the closing half: an open menu still shuts on escape.

Holding **Shift** queues five, as the stock hotkeys do. Holding **Alt** walks
the cycle backwards, as it does in FAF — and a *fresh* Alt press opens at the
far end, which makes it the direct route to the cheapest option: the T1 factory
you mean to upgrade later, rather than the T3 one the forward cycle opens on.

**Several roles can share a key**, merging into one cycle ranked by tier first
and role order second. That does two jobs at once. Where the roles cannot
coexist it reads as "first one that applies": **R** is the land factory's tank
and the naval factory's warship, and no factory builds both. Where they can
coexist it reads as a round-robin: **W** is land, air, then naval factory off
repeated presses, each at its best tier, before the cycle drops a tier and
comes round again — one key for the whole decision instead of three. Splitting
them back onto separate keys is just a config edit.

Note the ordering that falls out of this for a high-tier engineer: W gives the
T3 land factory before the T1 one, so placing a cheap T1 factory to upgrade
later takes several presses. If that reads wrong in play, the fix is a
per-role "prefer lowest tier" flag rather than a change to the cycle.

Roles stop at T3 on purpose. The experimentals above are faction-specific
one-offs that want their own keys — without the cap, Guard's T4 Experimental
Generator (`ugs4621`, tagged `ENERGY_PRODUCTION`) would sit at the top of the
energy cycle and a tap of **D** would try to start one.

**The unit keys follow Zulan's**, the Forged Alliance hotbuild layout that FAF
later absorbed: mnemonic, and the same letter reused across domains, because a
factory only ever builds one of them.

| Key | Land factory | Air factory | Naval factory |
| --- | --- | --- | --- |
| **E** | engineer | | |
| **S** | scout | scout | submarine |
| **T** | tank | transport | |
| **B** | raider | bomber | battleship |
| **F** | | fighter | frigate |
| **D** | | | destroyer |
| **G** | | gunship | |
| **O** | | torpedo bomber | |
| **R** | mobile artillery | | |
| **N** | mobile anti-air | | |
| **V** | sniper | | |

That reuse is the same "roles sharing a key" merge described above, resolved by
whichever factory is selected. Two departures: Zulan's gives each warship class
its own key rather than walking a line of tiers, so frigate/destroyer/battleship
are split rather than cycled; and **V** for the sniper is ours, since Zulan's has
no sniper and its V is a mobile shield with no shared-tier equivalent here. Its
cruiser, carrier, missile launcher, amphibious tank and stealth field have no
counterpart in Sanctuary's shared roles either, so those keys are simply unused.

Structure keys are not Zulan's — its structure layout is not something we could
verify — and keep the stock construction letters where they already fit
(W/E/S/D/X/C/R).

Every role's key is a config entry, so they are all rebindable from the F8 mod
manager in the game's own hotkey format (`G`, `Ctrl-G`, `Ctrl-Alt-G`); blank
unbinds. Where a unit key lands on an order key — **G** is Repair, **F**
attack-move, **V** reclaim — nothing is lost: the role only claims the press
when a factory is selected and has something to build, and orders like repair
and reclaim mean nothing to a factory. Any other selection falls straight
through to the order, because Construction runs at a higher group priority and
returning `false` lets the event carry on. `M` is left unbound, so the stock
"upgrade structure" hotkey still works; `Builder keys · UpgradeKey` moves it.
The other stock construction letters are not offered for moving: they find
their template through the button labels the roles take over, so W, E, S, D,
X, C and R are the roles' keys under another name, and Y (mobile anti-air,
the **N** role) no longer finds anything.

The settings page lists keys first and tuning after: the structure and unit
roles; **Builder keys** (pause, repeat build, stop factories, upgrade); the
game's own keys in their `Game …` sections, the pause menu's key among
**Game interface** with chat; then **Building** (cycle time, extractor snap)
and **Overlay**. Keys saved under the sections from before 0.4.0 (`Toggles`,
`Cancel`, `Menu`, `Cycle`, `Placement`) are carried over the first time 0.4.0
loads.

**Every other game hotkey can move too.** The `Game …` sections of the
settings list the game's own actions — orders, selection, control groups,
camera, chat, and the rest (self-destruct,
game speed, the HUD and icon toggles) — each defaulting to the keys the game
ships it on, so the box shows what the key is today. Type the keys it should
answer to instead, comma-separated in the game's format: `Q, Shift-Q` for
attack (the `Shift-` form is what queues it), `F5` for control group 1,
`AnyModifier-K` for a camera pan that works whatever modifier is held. Blank
unbinds it; `LeftButton`, `RightButton` and `MiddleButton` are keys as well.
F1 is best avoided, since the game's debug menu sits on it.

The game keeps every hotkey in one table, `inputActions.lua`, which
`inputSystem.lua` copies into `LoadedActionMap` as one entry per key, each a
shallow copy of the action's handlers. The move is made on that live map, so
the Lua tree and the lobby hash are untouched. Because each copy shares its
action's functions, the mod finds an action's current keys by matching them,
wherever they are. It takes every moved action off its keys before putting
any back, so two can swap. A key taken from an action you left alone comes off
that action; a key another group also binds fires both, since few actions
consume the press. Both kinds are written to the log, as is a moved action
this version of the game doesn't have. Taking the mod out puts the game's own
map back exactly. Actions left at their defaults are not touched at all, so if
a game update changes a default, the new one applies. The mouse itself
(pointer, clicks, zoom, rotation), the append and alt modifiers, and the pause
menu (`Game interface · PauseMenuKey` covers that) are left out. An action a later game
version adds is read out of the live table at the first match and gets a
setting from then on.

**The construction buttons relabel themselves.** Each one draws its hotkey in
the corner, filled from `constructionPanelHotkeys.GetHotkeyForTemplate` —
`constructionPanel` holds the module table rather than the function and looks
the field up per button, so replacing it relabels every button with the key
that actually builds it. Modifiers compress to one character (`^S` for
`Ctrl-S`) to fit where a single letter went. Anything no role claims keeps the
stock answer, which is usually the `?` it shows today.

**An overlay shows what you just picked.** A cycle is otherwise invisible
until you place something — you cannot tell whether W→W landed on the air
factory or on a lower-tier land one. So each press publishes its whole cycle
and a strip appears: every option in that key's
cycle left to right in the order further presses reach them, drawn with
the same art the build menu uses — each unit's icon over the domain plate
behind it (`backgroundIconID`, keyed on `iconUIType`: land, air, water,
amphibious), so the strip reads like a slice of the panel rather than floating
cut-outs, and land/air/naval separate at a glance. The live one is lit and
underlined, the rest faded. A factory shows only its pick unless
`Building · CycleSeconds` is set: without a cycle window a repeat press queues another of
the same, so the rest of the list would advertise options pressing again cannot
reach.

A long cycle shows **one tech tier at a time** rather than all of it: a T3
engineer's factory key is nine entries once naval factories are in, which would
span the screen at this icon size. A small `T3` / `T2` label then names the
band you are in — the key itself gets no column, since you just pressed it —
and the next entry leans half into view past the right edge, which says "there
is more" without asking anyone to read a count. No peek on the final band is
itself the signal that the cycle ends there, and with the whole cycle on screen
the tier label goes too, since it would read as applying to a strip that spans
tiers. Banding is by tier rather than by a fixed block of
three, because a block would straddle two tiers whenever a faction lacks a
domain at one of them — Chosen's T3 point defence, which nobody else has, is
enough to shift every block after it. A cycle that already fits is shown whole:
point defence is one entry per tier, and banding that would leave a single icon
on screen. `Overlay.MaxShown` (default 3) caps a band.

`Overlay.ShowNames` adds a caption underneath naming the entry
you are on ("Tier 1: Land Factory") — off by default, since the art usually
carries it and the name is only needed to separate two tiers that share a
sprite.

Those icons are *not* the strategic icon atlas the commander widget samples;
they are per-template `.sansprite` assets loaded through the game's own
pipeline into a registry keyed by `AssetID`. `SanctuaryUI.Utils`
`.TryGetLoadedSprite` is the way in, and `EM.Core.AssetID` wraps exactly the
`uint` that Lua reports as `general.foregroundIconID.index`, so the panel
passes that index across and resolves the real `Sprite` — drawn through its
`textureRect`, since each one is a window into a packed atlas. If the lookup
ever goes missing the overlay just lists names.

The strip fades a couple of seconds after the last press,
and is drawn rather than built from `GUI.Window`, so it can never swallow a
click meant for the battlefield under it. `Overlay.Show`, `Overlay.Seconds`,
`Overlay.IconSize` (40), `Overlay.MaxShown` and `Overlay.PosY` (860, just clear
of the build panel) control it.

**A modifier the game thinks is still held gets let go.** `inputSystem.lua`
builds a key's `Alt-` / `Ctrl-` / `Shift-` prefix from its own record of which
keys are down, and only a key-up clears it. Alt-Tab out during a loading screen
can lose that key-up, and every key then arrives as `Alt-<key>` — for W, the
reverse cycle, which opens on the air factory. The game's own hotkeys break the
same way. So ten times a second, while the game has focus (and straight away
when it gets focus back), any modifier the record holds but the keyboard
reports up is cleared, with a line in the log when that happens. "The keyboard"
is Windows' `GetAsyncKeyState`: until 0.4.1 this asked Unity's `Input.GetKey`,
which learns key state from the same window messages the game does, so after
Alt-Tab it said Alt was still held too and nothing was let go. When Unity and
Windows disagree, the log says so once a match.

Nothing here edits a Lua file, so `ComputeLuaHash` is untouched and a modded
client still joins unmodded lobbies. The binding is a runtime insert into
`inputSystem.lua`'s `LoadedActionMap` (which `CallAction` reads live on every
event, so it takes effect immediately and is restored on unload), and the build
goes through `constructionPanel.lua`'s own `ConstructionClickFunction` — the
same observer check, the same local prediction and the same host-validated
command a button click sends. Returning `false` when nothing matched lets the
key fall through to whatever it normally does, and because chat disables every
action group but `MouseControls`, typing already suppresses these for free.

### Pause and repeat build

`Builder keys · RepeatBuildKey` (**Z**) switches repeat build on the selected
factories and again off, as the orders panel's toggle does — on if any
selected unit has it off, else off — through the game's own `SetToggle`
command. It sits *behind* whatever else the key does here: a build role on
the same key fires first, and the toggle only when that had nothing to build
for the selection. Blank to unbind.

`Builder keys · PauseKey` is the game's own pause toggle (**P**), moved like any
other game hotkey. Until 0.4.0 the mod had a pause key of its own; the
game's does the same job, so the setting now moves the game's instead, and a
key saved for the old one carries over — anyone who had it on **X** keeps X.
The game's pause is in the Orders group, below the build keys, so it stacks
the same way: X is the point-defence role by default, so with engineers
selected X builds point defence, and with factories selected, which have
nothing under that role, X pauses them.

### Extractor placement

Placing an extractor snaps it onto a deposit near the cursor. The game's
`FindClosestResourceSpot` fixes that at 8 world units, which zoomed out is a
couple of pixels. `Building · ExtractorSnapPixels` (40) makes the snap a
fixed size on screen instead: the mod turns that pixel radius into world
units from the camera's height and field of view and pushes it into the
hook a few times a second as you zoom. `Building · ExtractorSnapDistance`
(8) is the floor in world units, however far in you zoom; set the pixel
value to 0 to use the floor alone. The hook is the same search as the
game's, swapped in on the module table so the game's own callers reach it,
and put back on unload.

## LadderReporter

Reports ranked 1v1 results to the [SanctuaryDB ladder](https://www.sanctuarydb.net/ladder)
automatically, and — new in 0.2 — lets the ladder launch a matchmade game
with no lobby interaction from either player.

**Reporting.** The host computes each army's win condition in Lua and
broadcasts every change to every client; a runtime wrapper around the
client's `WinConditionUpdate` sees the result. Identity is the game's own
Steam session: at report time the mod mints a Steam web-API ticket, sends it
with the result, and cancels it once the request is over. The ticket proves
which Steam account sent the report, not that the result in it is true, so
the ladder doesn't take it on trust: a player's own loss applies at once, a
claimed win applies when the opponent's client agrees or after a 15-minute
window, and reports that contradict each other freeze the match as disputed.
Only Steam lobbies with exactly two human players on opposing teams and no AI
are reported; skirmish, LAN, AI and team games are recognised and left alone,
as is a game you are only watching. Spectators in a ladder game don't stop it
reporting.

**Matchmaking.** The site pairs queued players, picks map, factions, slots
and host, and runs the countdown. The mod never polls the site: it listens
on `127.0.0.1:27555` (loopback only, `Matchmaking.LocalPort`), and the
SanctuaryDB page in the player's browser asks it every couple of seconds
what the game is doing (`GET /status`), relays that inside the polls the
page already makes, and hands the match object over when there is one
(`POST /match`). Only requests from `https://www.sanctuarydb.net` (plus
`Matchmaking.DevOrigins` for a dev server) are answered, so no other web
page can push a match into the game; a game left open in the menu costs the
site nothing. A pushed match is validated before anything acts on it (ids,
Steam IDs, slots, and a map that has to be a relative `Maps/.../*.sanmap`
path inside the game), and a malformed one is answered with a 400. Nobody
needs the mod to queue: the site only picks the
automatic path when *both* players' games are seen in the main menu with
the mod, and falls back to today's manual hosting otherwise. The mod's own
calls to the site (session id, progress events) carry a bearer
token from one Steam ticket, minted when the first match arrives, and the
result report carries a Steam ticket of its own. When a
match reaches `launch`:

- the host's mod creates the lobby on the assigned map (`CreateLobby`),
  moves the UI to it, posts the session ID to the site, seats itself
  (faction, slot, ready), kicks anyone who isn't the assigned opponent, and
  starts the game as soon as the joiner is seated and ready;
- the joiner's mod sees the session ID on the page's next push and joins by
  ID through the same public entry point Steam "join game" uses
  (`InterfaceManager.JoinSessionFromInvite`), then seats itself.

Both bring the game window back if it's minimised or buried (a `user32`
restore, with a taskbar flash when Windows refuses focus), post progress
events, and mirror the site's timeouts locally so both sides converge if a
push is late. Any failure leaves the lobby, tells the player why in a
small overlay, and points at manual hosting. A matchmade game's result
report carries its `matchId` so the site can close the match.

Everything the site side needs is in [docs/matchmaking-site-plan.md](docs/matchmaking-site-plan.md)
and the site repo's `docs/local-bridge.md`.
To test the launch flow without the site, point `Matchmaking.MockFile` (F8
window) at a copy of [docs/matchmaking-mock-host.json](docs/matchmaking-mock-host.json)
or [matchmaking-mock-joiner.json](docs/matchmaking-mock-joiner.json); `"me"`
stands in for your own Steam ID, and the joiner's file takes the session ID
the host logs.

**Stats and replay uploads (0.4, opt-in).** After a reported game the mod
can upload the match's stats and its replay to the match's page on
SanctuaryDB. Both are **off by default**; nothing leaves the game unless
the player turns them on in the `[Upload]` section (F8 window):

| Setting | Default | What it does |
| --- | --- | --- |
| `Upload.Stats` | false | Upload this match's stats (economy, units, score over time) after a ranked game, shown on the match page |
| `Upload.Replays` | false | Upload the replay of each ranked game once it is decided (or after you leave the match), so anyone can download and watch it |
| `Upload.MaxReplayMB` | 30 | Larger replays are not uploaded |
| `Upload.ForceUploadMatchId` | empty | Debug only, for testing against a site dev server: a match UUID. Every finished game (against AI, any player count) then runs the uploads against that id, skipping the ranked 1v1 check |

- **Stats.** The collector is SanctuaryHud's MatchStats one, compiled into
  both mods from `shared/Stats`, so the upload works without SanctuaryHud;
  whichever mod installs it first in a match serves both. It goes in as
  early in a ranked-looking match as the client allows. The stats are read
  once, when the game's victory/defeat panel comes up (or 5 s after the
  result), while the match's Lua VM is still alive: per player totals
  (alloy and energy gathered, spent, wasted, stalls, peaks; units built,
  lost and killed with their value; peak army; score) and a timeline in 5 s
  buckets. Only the seated players are sent, keyed by Steam ID. They are
  posted once the report's answer says which match the site filed it under
  (`/api/report` now returns `matchId`; a matchmade game also knows it).
- **Replays.** The game writes a match's `.sanreplay` while it plays, one
  whole network frame at a time, and closes it only when you leave the
  match. 5 s after the result the mod copies it as it stands, cut after the
  last whole frame, so the replay ends at the result instead of with the
  players idling on the result screen; if that fails, or you left first, it
  copies the closed file once the game lets go of it. The copy (and the
  `.mods.json` sidecar, if any) goes to
  `BepInEx\cache\LadderReporter\pending\<matchId>.sanreplay`, safe from the
  game's keep-the-newest-15 prune, hashed on a worker thread. Uploads run
  in the menu or a lobby, and on the result screen for that match's own
  replay, and stop the moment a game starts loading: a slot request to the
  site, a PUT of the file straight to storage, then a completion call. Retries back off 1, 5 and 30 minutes, then wait for the
  next launch; a pending replay is dropped once uploaded, when the site says
  it already has it (the opponent uploaded first) or refuses it, or after 7
  days. A replay still waiting for its match to close survives a crash or a
  quit and goes next launch.
- **DryRun** (`Report.DryRun`) writes the stats JSON and the replay slot
  request to `BepInEx\cache\LadderReporter\dryrun\` instead of sending them.
- `GET /status` on the local bridge also answers
  `"uploads": { "stats": bool, "replays": bool, "pending": n }`, so the
  match page can show that an upload is on its way.
- **Status card.** While the game's victory/defeat panel is up, a small
  card beside it says what happened to the result (recorded, waiting for
  the opponent, disputed, not reported), the stats and the replay (with
  its upload progress), with a link to the match page once the site has
  said which match it is. With both uploads off it says where to turn them
  on instead. It is on a canvas of the mod's own, so it shows over
  SanctuaryHud's stats screen and with the HUD hidden. A replay upload that
  ends after you've left the match is told by a toast in the menu.

All three calls (`POST /api/mm/match/{id}/stats`, `.../replay`,
`.../replay/done`) carry the matchmaking bearer session, minted from a Steam
ticket when first needed. Stats and replays are cosmetic: the result and
the rating come from the report alone.

## ReplayManager

Makes the game's own replays watchable properly: any player's point of view
or every army at once, the fog lifted, every army's economy with whole-game
totals, and a transport with pause, speed, forward seek and restart. Since the
playtest update of 2026-09-04 the game records every match to
`%USERPROFILE%\AppData\LocalLow\Enhearten Media PTY\Sanctuary\Replays\*.sanreplay`
and plays them from the main menu's replay list; the panel appears whenever
one is playing, and **F7** shows and hides it.

**What the game does.** Its replay is a recording of the match behind a
small header (map, game version + Lua hash, recording client). Playback goes through
`ReplayClientSockets`, a fake socket that synthesises the launch messages
and then reads recorded packets into the client's receive buffer, paced by
the sim speed, at most 32 ticks ahead. The client only steps a tick once its
packet is buffered, so the socket's feed rate is the playback rate. This is
the same design the mod used before (see [archive/](ReplayManager/archive/)), so
the mod now only drives the game's socket:

- **pause** is a Harmony prefix on the socket's `Receive` that feeds nothing
  once the launch messages are through;
- **speed** is the engine's own `ClientEngine.SetReplaySpeed` (0.1× to 16×),
  which is what the socket paces by;
- **position** is frames read (a postfix on `TryReadFrame`) minus frames
  still queued; **length** is a scan of the file's frame headers;
- **fast-forward** runs at 16× until the target tick. There are no snapshots
  to seek with, so the seek bar only goes forward — dragging left of the
  current tick does nothing — and **RESTART** is the way back to the start:
  it leaves through the game's quit path (scene reload), calls the game's own
  `StartReplayPlayback` on the same file again, and fast-forwards from zero.

**Seats and economy.** The view buttons switch playback between each
recorded player's point of view and an all-armies view, and the FOG toggle
follows the view (or can be set by hand). The client is marked an observer
so clicks can't issue orders into the void. The per-army economy figures and
player names are read from the recording as it plays, by hooks installed
before its first tick is applied.

**The result screen.** The game puts up VICTORY or DEFEAT the first time the
focused army's result arrives, and in the all-armies view every army counts
as focused, so a replay used to show DEFEAT (and MatchStats' window with it)
the moment the first player was wiped out. The mod wraps the client's
`WinConditionUpdate` with the panel calls muted, and shows the panel itself
once the match is decided — some army has won — as VICTORY or DEFEAT for the
army being watched, or GAME OVER in the all-armies view. The game's handler
still sees every update, so other wrappers (MatchStats) do too.

**The panel** has the clock, play/pause, a log-scale speed slider, +1
minute, a FOG toggle, a TIMELINE toggle that hides the total length and the
seek bar for watching without knowing when the game ends, QUIT, a
forward-only seek bar with a **RESTART** button beside it, and one row
per army: the name button (in the army's own colour) switches to that army's
view, then alloy and energy as a storage bar, net / in / out per second, and
the amount used so far in the game. ALL shows every army. Armies that never
show an economy — the empty slots of a map bigger than the game played on it,
and the neutral army — are left out of the table; once an army has appeared
it keeps its row, so being wiped out does not remove a player. Every column
heading sorts the table: a click puts the highest first, a second click the
lowest, and ARMY goes back to seat order; armies with no economy yet stay at
the bottom. Drag the panel to move it and the grip in its bottom-right corner
to resize it; both are remembered (`PanelX`, `PanelY`, `PanelScale`). The
panel stays wholly on screen, and `UI · Locked` stops it being dragged. The
resource columns are headed by the HUD's ingot and bolt marks.

The panel is built like the HUD's (the game's font, its plates, clicks that
stop at the panel) but on a canvas of the mod's own rather than the game's
HUD canvas, so it stays up through a rewind and when Camera Utilities hides
the game's UI.

**Caveats.** A replay is tied to the game build and Lua hash it was recorded
with; the game's own list greys out mismatches. Playback is a normal client,
so the other mods in this repo behave as they would in a match and follow
the focused army. A recorded change of sim speed by the host would override
the speed slider for a moment; the panel re-applies its value.

## CameraUtilities

Takes the game's overlays off the picture, one at a time, for recording
cinematics — or just for a cleaner view. Every switch is in two places: the
mod's settings on the **Mods** page, and a small in-match panel on **F4**,
because during a shot you don't want to leave the game to change one. The
config entries are the single source of truth, so a change either way persists
and both views agree.

- **Strategic icons** — three ways: always, never, or only above a camera
  height. The last is the interesting one: icons are what you want at
  strategic zoom and exactly what you don't want in a close shot, so
  `WHEN FAR` keeps them until you dive in and drops them below the threshold
  (100 world units by default — the panel shows the live camera height next to
  the setting so you can pick one against the shot you're framing). This is
  the rule the game's own `rendering.lua` carries a TODO for.
- **Intel ranges** — the vision, fog-of-war, radar, sonar, omni and
  counter-intel rings.
- **Attack ranges** — direct, indirect, anti-air, anti-naval and counter.
- **Build ranges** — build and assist, so a screen full of engineers stops
  drawing circles.
- **Order lines** — the lines and markers drawn for whatever is selected:
  move, build, attack, assist, reclaim, and the whole-army view of them the
  append key brings up.
- **Planned buildings** — the outlines of buildings an engineer or commander
  has queued but not started. They come back on their own the moment
  construction begins.
- **Alloy spots** — the marker on a deposit with no extractor on it yet.
  Switching it back off hands the decision to the game's own
  `RecalculateRendering` rather than forcing every marker on, so a spot that
  gained an extractor meanwhile stays hidden, as it should.
- **Health bars** — every health and progress bar.
- **Game UI** — the whole HUD. The game's switch turns off its whole UI
  canvas, so this mod's panel sits on a canvas of its own: it stays up, still
  takes clicks, and F4 still gets everything back.
- **Unit draw distance** — how far the camera can get before units stop being
  drawn at all. The game stops drawing anything mobile past 100 world units
  and structures past 160, which is why a zoomed-out battle is nothing but
  strategic icons; set a larger figure and the models keep going. This is the
  one setting here that isn't live — see below.

Presentation-side only: these are all client rendering flags the client's own
Lua already drives, so no simulation state is touched, no hashed file changes,
and a client running this is lobby-compatible with unmodded players.

**How it holds.** The flags have to be re-asserted — the game rewrites the icon
flag every render update, and units spawn with their rings on — so rather than
pushing a chunk across the FFI at frame rate, one chunk installs a small agent
in the client VM and C# pushes the wanted state only when it changes. The agent
wraps `rendering.RenderUpdate` (a field on the module table `clientMain` calls
through, so replacing it intercepts every frame without editing a file) and
runs after the game's own call:

- **icons** are the two engine calls above, per frame, so they follow the
  camera without lagging it;
- **range rings** are a per-unit, per-material flag the game itself never
  writes — it only moves the per-unit master, on intel and selection changes,
  which ANDs with ours — so a sweep four times a second is enough to catch
  units as they appear, and it skips any unit already at the wanted mask;
- **planned buildings** are ordinary client units with no build progress —
  placement ghosts — so the same sweep switches their renderer off. Only ever
  ones that are ours and visible right now, and only ever back on for ones the
  mod itself hid: writing the renderer on for a unit the game had hidden would
  reveal it;
- **health bars** go through the *global* bar scale, where 0 means don't
  render, because the per-unit master is rewritten every tick by
  `ClientUnit:UpdateProgressBars`. That scale is the one thing here that
  outlives a match while the agent holding it does not, so the sweep
  reconciles against the live value rather than against a remembered "already
  hidden", and never reads a zero back as the scale to restore — a match that
  inherited the zero from the last one would otherwise record it as the
  game's own value and keep the bars off for good (0.1.1 fixed exactly that);
- **the UI HUD** has a toggle and no getter, so the agent keeps its own belief
  of the state and only toggles on a change.

Order lines are the odd one out, because they aren't a flag: the order manager
tears down last tick's line and marker prefabs and rebuilds them from scratch
every tick, drawing either every army's orders (while the append key is held)
or the selected units' own. Skipping its draw would leave the previous tick's
prefabs on screen forever, so instead the mod wraps `DebugDraw` and lets the
game's own draw run with the all-armies view off and nothing selected — it
clears, finds nothing to draw, and stops. `SetOrderDraw` is wrapped alongside
purely to remember what the append key last asked for, so it can be handed
straight back.

**Draw distance is the exception**, and the only part of this mod that is a
Harmony patch rather than Lua. Every renderable entity carries an LOD component
holding up to six levels, each with a render distance; the culling system picks
the first level the camera is still inside, and past the last one the entity
simply isn't drawn. Units and structures are given exactly one level — 100 for
anything mobile, 160 for anything that isn't — so past that they are gone and
only the icon is left. Nothing exposes those numbers at runtime: the culling
system is a Burst job, and Lua has no setter for them. They exist in a
patchable, managed form in exactly one place, the point where a match's Lua
templates are turned into render prefabs, so that is where the mod raises them.

Two things follow. The distances are baked into the prefabs as a match loads,
so a change only takes effect when the next match or replay starts — the panel
says "next match" when what you've set isn't what the current one got. And only
single-level chains are touched: that's what a unit or structure has, and its
one distance is purely a cull distance with nothing cheaper to fall back to.
Props keep a real chain with an impostor on the end, so raising theirs would
hold full-detail meshes on screen at range; they're left as the game built them.
Worth knowing that units have no cheaper level either, so a wide shot of a big
battle now draws every model at full detail.

Unloading the mod, or switching it off on the Mods page, takes the wrapper back
off and puts every flag back. A fresh match brings a fresh Lua state, so the
agent reinstalls itself; a hot reload of the DLL finds it already there and
re-wraps without stacking (the version global carries a hash of the install
chunk, so an edit to it forces a reinstall rather than leaving the last build's
agent running).
## ModManager

> **Making a mod for the Mod Manager?** Start with
> [docs/your-first-mod.md](docs/your-first-mod.md), a step-by-step tutorial
> (a gameplay mod with an append, options and a host script, played and
> shared, with only a text editor). Then use
> [docs/writing-mods.md](docs/writing-mods.md), the full reference (every
> `mod.json` field, match events, recipes, factions, C# mods and the API),
> and the working mods in [examples/](examples/).
> [docs/modding-field-notes.md](docs/modding-field-notes.md) collects what we
> learnt about the game itself, and the traps that cost us the most time.

A **Mods** entry in the front menu's sidebar (the cube icon, just below
Settings; **F8** opens it too) leading to a full page with two tabs, UI Mods
and Gameplay Mods, and a **Mods** button in the lobby. The page is the game's own Settings screen, cloned and refilled:
the tab bar, the switch rows, the sliders (UI Scale's row), the left/right
selectors (Window Mode's row), the text fields (a slider's input box,
widened), the headings and the buttons are all the game's Beam UI widgets, so
it looks like the rest of the menu and follows any restyling the game does.

Each UI mod's settings are listed in the order the mod bound them, so an
author's grouping is what the player reads: sections in first-appearance
order under their own heading, entries as bound within each. A bool is a
switch, a setting with an `AcceptableValueRange` a slider, one with an
`AcceptableValueList` a selector, anything else a text box. Key names show
as words (`HideGameEconomyBars` reads "Hide game economy bars"; `Eta`,
`Ui`, `Url` and `Pos` are expanded). A section that is nothing but hotkeys
(KeyCode settings, or strings whose description says "hotkey") is laid out
two to a row, which is what keeps BuildHotkeys' structure and unit lists
short.
**F8** also opens the same page full-screen during a match (over the menu
background, the way the pause menu's Settings does); closing it returns to
the game. UI mod toggles and settings changes apply immediately.

It manages two kinds of mods (see [docs/your-first-mod.md](docs/your-first-mod.md)
to make one, and [docs/writing-mods.md](docs/writing-mods.md) for everything):

**Gameplay mods** are a mod folder's `*.lua`/`*.santp` files, laid out mirroring
`LJ\lua` (and, with `"kind": "gameplay"` in its `mod.json`, its DLL). They are
the lobby host's pick, not the player's: outside a lobby the game always runs
vanilla, so nobody is ever kept out of a vanilla lobby by their own mods. In
the lobby, the **Mods** button beside Settings (it reads `Mods (2)`, with a `!`
while someone is missing something) opens a panel over the lobby screen: the
host switches gameplay mods on and off there, and everyone sees the pick and,
for each player, whether they have identical copies ("has Faster Tanks 1.1
(host 1.2)", "no mod support"). A picked mod's options appear under it as
switches, selectors and sliders for the host and as values for everyone else.
Every pick, options included, also goes into the lobby chat.
Start stays greyed out until everyone matches; with nothing picked there's no
check and vanilla players play as usual. The panel is an overlay rather than a
screen of its own because leaving the game's lobby window drops its chat and
roster listeners and clears the chat. The [Mod API](#modapi) does the actual
work; the Gameplay Mods tab here lists what's installed (manifest details and
problems on hover). **Every lobby starts with no gameplay mods**: the host
switches them on, and nothing can pick one by default. The option values a
host last used come back when they switch a mod on again. Ladder lobbies
(named `Ladder:`) are always vanilla: their Mods panel says so and nothing
can be picked. Lua mods switched on under 0.6 are simply dropped.

**UI mods** — the DLLs, every mod in this repo — each get a section headed
by the mod's name with its on/off switch inline. Sections start folded, one
row per mod; clicking a header unfolds that mod's settings beneath it. Off
destroys the plugin component (its `OnDestroy` unpatches Harmony) and on
creates it again. That is a component teardown, not an unload: .NET can't
unload an assembly from a running game, so the mod's code stays in memory
until the game exits, along with anything its `OnDestroy` doesn't undo. The
switched-off set persists across restarts, and with ModLoader 1.3 a
switched-off mod is never started at all: the loader checks the list before
it creates the plugin, so none of its code runs. A mod whose DLL is deleted
leaves the list. UI mods never enter the Lua hash, so they are safe to flip
any time, even mid-match. A mod held back since start-up has no settings to
show until it is switched on, because a mod binds its settings as it starts.

Each loaded mod's settings sit in its section — panel positions, the commander
zoom factor, `AssistStartsUpgrade`, hotkeys, anything a mod binds. The list
is read from the mod's BepInEx `ConfigFile`, so a mod's settings appear here
simply by being bound, with no work in the manager. Booleans get the game's
on/off switch, a setting with a list of allowed strings gets a left/right
selector, one with an allowed range gets a slider, and everything else is
edited in a text field and committed
through the entry's own serializer (the same one that writes the config
file), so floats, enums and `KeyCode`s all work and a half-typed value just
doesn't take until it parses (it snaps back to the last good value when the
field loses focus). Each mod has a "Reset to defaults". Changes save to
`BepInEx\config\<guid>.cfg` immediately.

## ZoneControl

A **gameplay mod**: the lobby host picks it, and every player needs the same
copy. It ports johnie102's Zone Control for Forged Alliance (8P V2) to the
converted [Zone Control for FAF 8P V2](https://github.com/Remmyboy/sanctuary-map-converter/releases/tag/map-zone-control-for-faf-8p-v2)
map. It is Lua only, so its zip is just the `SanctuaryMods\ZoneControl` folder.
It needs Mod Manager 0.13.0 or later (Mod API 1.8.0, for its host helpers),
for every player.

- **No commanders, no building.** 53 zones on the diamond, each with a T2
  point defence. Every zone you hold sends you a unit every few seconds.
- **Capture:** destroy a zone's turret, then hold it with 5 or more units, more
  than anyone else. The owner retakes it with 10 and no enemies there. Lose
  every zone and you're out; the last team standing wins.
- **Kills earn money and levels.** Levels bring better units, T4 heroes at 4-6
  and invulnerable base artillery at 7-9. Each base has an upgrader: move it
  onto a shop, or have it assist one, to buy attack or defence upgrades or a
  kamikaze. The gun sells attack, the radar sells defence and the generator
  sells kamikazes; the banner lists the prices.
- **Lobby options:** seconds between spawns, unit cap per player, insanity
  mode (levels after a handful of kills), and the zone count banner.
- **It only runs on the Zone Control map.** On any other map it says so, and
  the match is a normal one.

Every number is in `lua\zonecontrol\balance.lua`, each with the original's
value beside it. Problems show in the match's message log as
`Zone Control problem: ...`, because the game's Lua warnings reach no log.

## UnitRestrictions

A **gameplay mod**: the lobby host picks it, and every player needs the same
copy. It is Lua only, and its unit list is a Mod API `units` option, so it
needs a Mod Manager with Mod API 1.7.0 or later, for every player.

- **Sections:** no land units, no air units, no naval units, no experimentals.
  Sections never take engineers or commanders, and leave factories standing
  (they still make engineers). Experimentals are every tier 4 unit and
  structure.
- **Restricted units:** the lobby's Mods panel shows every unit the match can
  build as a grid per section (land, air, naval, structures): a column per
  faction, a row per kind of unit under its tech level, each cell the unit's
  strategic symbol and name. Click a unit to restrict it alone, or a kind, a
  faction or a tech level to restrict all of it; restricted cells turn red.
  Structures, factories and their upgrades are there too. Other players can
  open the grid to see the pick.
- Restricted units leave every build menu, and the host refuses them if
  anything queues or places one anyway, AI armies included.

## BalancePatch

A **gameplay mod**: the lobby host picks it, and every player needs the same
copy. It is Lua only. It changes unit and projectile numbers as the game loads
them, so the build menus, the unit card and the AI all see the new values, and
it doesn't replace any of the game's files.

Each section is a lobby option, all on by default:

- **Fixes:** the EDA T3 anti-air fighter no longer shoots the ground; bombs
  live long enough to land (the EDA T3 bomber fired all game and never hit);
  the Guardian TALEN gunship is labelled tier 3.
- **Economy:** commanders make 2 alloys and 30 energy a second (were 5 and 50)
  and T1 extractors 1.5 alloys (were 1), so expanding matters and going
  straight to T2 off the commander takes longer. T2 and T3 extractors and
  generators give more per cost than T1, so teching your economy pays; T1
  generators have less health.
- **Unit costs:** land and naval units cost more alloys and less energy for
  the same total. Chosen aircraft cost as much energy as everyone else's.
- **Engineers:** less than half their health, so raids on them work.
- **Commanders:** the EDA and Guardian commanders' missiles fly at once and
  steer at where the target is going, in smaller, faster volleys.
- **Artillery:** leads moving targets; the T1 artillery of the three factions
  are brought closer together.
- **Air:** bombers lead their targets and their bombs splash; T3 fighters
  lead their shots; the Guardian fighters catch up.
- **Defences:** point defences see as far as they shoot, and T1 point defences
  are tougher: they beat their cost in T1 tanks, and T1 artillery still
  outranges them.
- **Unit tuning:** the Chosen Jager toned down and the other T2 raiders
  brought up, so T2 beats its cost in T1 without crushing it; the T1 tanks
  brought level.

Every change, with the reason, is in
[`lua/balancepatch/changes.lua`](BalancePatch/lua/balancepatch/changes.lua);
`node BalancePatch/tools/preview.mjs` prints each one as before and after
against the installed game. A change made against a number the game has since
changed is skipped, so a game update can't stack with it.
[`FINDINGS.md`](BalancePatch/FINDINGS.md) lists what the tests found that only
the game can fix.

## PhantomX

A **gameplay mod**: Supreme Commander's Phantom-X (faf-phantomx v268, by
Novaprim3, Duck_42, mead, SpikeyNoob and Fichom), ported. It is Lua only, and
its panel is drawn by the Mod API from Lua, so it needs Mod Manager 0.13.0 or
later (Mod API 1.8.0), for every player.

- **Everyone starts allied**, whatever the lobby's teams, and nobody shares
  resources. A few minutes in (8 by default) some players secretly become
  **phantoms**: by vote, or a set number, with volunteers more likely to be
  picked or the pair chosen to balance the teams.
- **Phantoms** get extra storage and a share of the innocents' combined income,
  more the fewer innocents they're still allied with. They win by being the last
  one standing. **Innocents** win by killing every phantom. **Paladins** are
  innocents with a smaller share of the bonus, which a phantom can take away by
  paying alloys to **mark** them.
- **Reveals:** phantoms (or paladins, or both) are named at set times, to
  everyone or only to phantoms or paladins. A player's role can be shown when
  they die.
- **Phantom war:** when only phantoms are left they become enemies, and each
  gets back a share of the cost of what it kills.
- **The Phantom-X panel** shows your role, your bonus, the timers and every
  player, with buttons to break or offer alliances (both sides have to offer
  peace; an AI always accepts), mark a paladin, vote and volunteer. Notices go
  across the top of the screen and into the message log.

Each player is only sent what their role lets them know. The host checks
every request against the client it came from. Every number is in
`lua\phantomxalance.lua`, beside the original's.

## MapLocalFiles

Lets Lua's `Engine.GetFileContent` see files inside the loaded map's folder,
so a converted map can carry its own decal blueprints under `map/...`. The
game's `EM.Lua.FilesCache` is built once at startup and never includes map
folders; this patches a lazy fallback on the miss path only, serving `map/`
paths from the loaded map's folder on disk. The hit path is untouched, so
shipped content behaves exactly as before. Served files live in native memory
that is freed when a different map loads or the mod unloads, and a file over
8 MB is refused.

## ModLoader

The one piece that lives inside the BepInEx tree (`BepInEx\plugins\ModLoader.dll`),
because BepInEx is what loads it. Everything else is a folder under
`engine\SanctuaryMods\`: the loader loads every `*.dll` it finds there at
start-up, watches them, and reloads any that change about a second after
the file is written (F6 forces a reload of everything; a deleted DLL has its
plugins torn down). A mod is installed by dropping its folder in and removed
by deleting it, with no restart either way, and `dotnet build` of a project
is the whole iteration loop while developing.

Reloading is not unloading, though. Mono can't remove a single assembly from
the running game, so each reload destroys the old plugin components and loads
a fresh copy of the assembly beside the old one. Every copy stays in memory
until the game exits, along with its static state and anything its
`OnDestroy` failed to undo, such as static event handlers or background
threads. The log counts the copies on each reload; after a long run of
rebuilds, restart the game.

Since 1.3 it is also the registry the [Mod Manager](#modmanager) reads. A
plugin switched off on the Mods page is held back before it is created, so
its `Awake` never runs, and the manager starts and stops plugins through the
loader, which refuses one whose DLL has since been deleted. An older Mod
Manager can't list a held-back plugin, so the loader only holds plugins back
when the manager installed says it can.

Since 1.4 it knows the two kinds of mod. A folder whose `mod.json` says
`"kind": "gameplay"` has its DLLs held back like a switched-off plugin, and
started only while the lobby host has picked that mod (the [Mod API](#modapi)
tells it which folders through `SetActiveGameplayFolders`); they stop when the
lobby or match ends, and a rebuild of one waits until the match is over, so
the simulation never changes under a running game.
Libraries an author ships by accident beside their DLL (`0Harmony.dll`,
`Newtonsoft.Json.dll`, `BepInEx*.dll`, `Sanctuary.ModApi.dll`) are skipped with
a warning: a second, renamed copy would break the real one.

Two things it has to do that a plain BepInEx plugin would not: it rewrites
each assembly's identity per load, because Mono returns the cached assembly
for a byte-load with an identical name and a rebuild would silently keep
running the old code; and it attaches the plugins it loads to BepInEx's own
hidden manager object rather than a GameObject of its own, because Sanctuary
destroys foreign root GameObjects after start-up (the same reason BepInEx
needs `HideManagerGameObject = true` here, see Setup).

## ModApi

> Writing a mod against this API? See [docs/your-first-mod.md](docs/your-first-mod.md)
> (tutorial) and [docs/writing-mods.md](docs/writing-mods.md) (reference).

`BepInEx\plugins\Sanctuary.ModApi.dll`: the stable core of the mod framework,
and the one assembly third-party mods compile against. Like the loader it is
loaded by BepInEx under a fixed identity (1.0.0.0 for all of 1.x), so it never
hot-reloads — which is exactly why it holds the parts that must outlive every
other mod's reloads, the Mod Manager's included:

- **The catalog.** Every folder under `SanctuaryMods` with its `mod.json`
  (id, name, version, author, kind, `luaRoot`, url, requires, options) and a SHA-256
  **content hash** over its overlaid files and, for gameplay mods, its DLLs.
  Rescanned every two seconds; only changed folders are hashed again.
- **The overlay.** Gameplay mods' files go into the game's in-memory
  `FilesCache`, which every Lua VM and the lobby's Lua hash read, lazily, for
  the whole match; nothing on disk changes. Files under `append\` are added to
  the game file of that path in the same chunk — just before a closing
  top-level `return`, which 123 of the game's 731 Lua files end in — and
  several mods' appends stack in pick order; other files replace or add.
  Every overlaid Lua file is compiled with the game's own `lua51.dll` as it
  goes on, so a syntax error is logged against its mod and file.
- **Vanilla outside lobbies.** Prefixes on `LobbyManager.CreateLobby` and
  `JoinLobby` clear the overlay, so the hash the host records for its join
  check, and every joiner's, is the vanilla one; postfixes on `LeaveLobby`
  and `EngineLoader.CleanUpGame` clear it again afterwards. A prefix on
  `SteamManager.AdvertiseServer` advertises the vanilla hash too (and adds
  " [mods]" to the lobby's name while mods are picked), so the lobby browser
  never greys a modded lobby out.
- **The lobby protocol.** The game's lobby runs on its own channel-4 messages,
  whose handlers switch on a type byte with no default case, so a type they
  don't know is dropped silently. Four new types ride on that: a joiner says
  hello, the host sends its pick (id, name, version, content hash and the
  resulting Lua hash, in apply order), each client applies it when all its
  copies are identical and reports back, and the host broadcasts everyone's
  state. Vanilla players neither see nor send any of it.
- **The Start gate.** The game only compares Lua hashes at join, so the API
  holds Start itself while any player is missing a mod, has a different copy,
  or has no mod support: `LobbyInterface.UpdateData` (the button),
  `InterfaceManager.OnLobbyStartGamePressed` (the reason, in the chat),
  `LobbyManager.CanStartGame` (anything calling it directly, LadderReporter
  included), and authoritatively the host's handling of its own `StartGame`,
  which also freezes the pick for the match. A client whose files don't match
  the host's when the match loads leaves it rather than desync.
- **Mod options.** A gameplay mod's `mod.json` can declare `options`
  (toggle, choice or number, with a default, range and step). The host sets
  them in the lobby's Mods panel, remembered for next time. The values travel
  with the pick, and each player's API writes them as
  `modoptions/<id>.lua` into the overlay, where the mod's Lua imports them.
  Because that file is part of the Lua hash, the Start gate already covers
  them, with no second check. The options' keys, types and ranges are part of
  the content hash; their labels aren't. Changes wait until the host has
  stopped moving a slider for 0.4 s, then go out as one revision.
- **Match events and mod scripts.** Whenever any gameplay mod is picked, the
  overlay also carries `modapi/events.lua` (embedded in the DLL) and a short
  append to `host/hostMain.lua` and `client/clientMain.lua`. The append wraps
  the game's `_G.OnSimulationTickUpdate` so the helper can run
  `OnMatchStart`, `After`, `Every` and `OnTick` handlers after each tick (10
  a second). It replaces `host/winCondition.lua`'s `CheckWinCondition` on
  the module to fire `OnArmyDefeated`. It also imports each picked mod's
  `hostScript`/`clientScript`. Handler errors are logged with a traceback,
  once per handler, and never stop the others.
- **Kill credit and damage.** The game's `HostUnit:TakeDamage` has no
  source, so when a mod asks (`OnUnitKilled`, `ModifyDamage`,
  `OnUnitDamaged`, `Kills`), the helper wraps the places damage comes from
  and notes the source around each:
  - `ProcessRayCollisionEvent` and `ProcessAreaDamage` in `collisionUpdate`;
  - `HostBeam:Fire`, `CheckDashCollisionsWithUnits` and
    `CreateDeathExplosions`;
  - a muzzle→unit map from `HostMuzzle.__init`, for projectiles.

  The hit itself is `HostUnit.TakeDamage`/`ProcessDamage`/`Destroy`,
  assigned on the class so every subclass gets them; `HostCommander:Destroy`
  calls `HostUnit.Destroy`. The hooks go in right after the first
  simulation tick's own update, which is where the game sets the match up;
  importing the unit and weapon code any earlier caches the targeting set-up
  before it exists, and units get built without muzzles. Units spawned by
  that set-up are credited through a muzzle backfill.
  `Delete` (captures, upgrades) never reports a kill.
- **Art packs.** A gameplay mod's `packs\*.sanpack` files go into the game's
  asset table (`EM.Gamedata.Data.SanPackLoadedFiles`, keyed by path) at the
  host's Start, as each client loads, and for a replay. They come out once
  the match is cleaned up. Anything the game had already built from an entry
  a pack replaces is dropped from its caches, both on the way in and on the
  way out. The host reads meshes and skeletons too, so packs are part of the
  content hash. Sounds can't come from packs: the game loads its sound banks
  as separate files.
- **Factions.** `mod.json` `factions` declares a faction: its name, tag, unit
  prefix, icon, commanders, AI folder and the stock faction it looks like.
  - The API numbers the factions after the game's three, in pick order, one
    lobby value per commander.
  - It fills every lobby row's faction dropdown, with icons. The game's C#
    only ever has three, and past the dropdown the value is a byte nobody
    checks.
  - It moves seats when the pick changes.
  - It writes `modapi/factions.lua` and `FactionsData` into the overlay.
  - It appends hooks that send an AI army to its faction's AI, including on
    takeover.
  - It points the stock AI's hardcoded three-faction tables at the full list.
  - It lends the looked-like faction's tag while a unit's prefab is built, so
    its shields, build beams, factory spawn bones and shield impacts follow.
  - It adds mods' units to `AvailableUnits`, the list the AI builds from.
- **AIs.** A folder with `AIPlatoonFunctions.lua` at its root is an AI mod,
  with or without a `mod.json` (whose `ais` can also name AI folders inside a
  bigger mod).
  - Its files go under `AI\mods\<folder>`, beside the game's AIs and never
    over them.
  - Some authors share the game's whole `AI` folder. Dropped in as it is, it
    yields only that author's AIs; its shared files and its copies of the
    stock AIs are left out.
  - The host picks an AI per seat in the lobby row's Player/AI dropdown
    (`AI: <name>`). Picking one also picks the mod.
  - The picks go out like options and follow a seat's army number.
  - The API writes them to `modapi/ai.lua`, and a `CreateArmies` hook applies
    each pick after the seat's faction AI.
  - A shim adds shared AI functions that newer AIs call, only where the game
    lacks them.
- **Borrowed models.** A unit template with `general.modelTpId` gets that
  model's placement ghost (under its own prefab name), portrait (unless its
  pack has one), wreck and hierarchy maps.
- **Install slips.** A top-level folder with no `mod.json` is searched up to
  three levels down for folders that have one: a zip extracted into its own
  folder, or a pack of mods. The loader gives each DLL to the nearest folder
  above it with a `mod.json`, so both agree on which mod a DLL belongs to.
  Unextracted archives and loose DLLs in `SanctuaryMods` are listed at the
  top of the Mods page. A `gameVersion` in `mod.json` that isn't the running
  game's shows as "made for game 0.0.1.20".
- **Modded replays.** A replay recorded with gameplay mods gets a
  `<replay>.mods.json` beside it (moved and deleted with it), option values
  included. The replay list
  shows it as playable when those mods are installed and identical — or
  "needs Faster Tanks 1.0" when not — and playing it puts them back on first.
- **The API** for mod authors: `Modding` (your mod's folder and manifest,
  lobby and match state, the live gameplay mods), `ModEvents` (lobby entered
  and left, pick changed, match starting and ended — owner-scoped, so a
  hot-reloaded mod's old copy never runs), `ModLua` (run and read Lua in the
  client VM), `Lobby` and `ModCatalog`. See
  [docs/writing-mods.md](docs/writing-mods.md).

It has no release of its own. It ships with the Mod Manager, in both of its
zips.

## Development

Layout: one folder per mod, each a tiny csproj — the shared settings (game
references, target framework, deploy step) live in
[Directory.Build.props](Directory.Build.props) /
[Directory.Build.targets](Directory.Build.targets), and the shared runtime
plumbing in [shared/](shared/) is compiled into the mods that reference it.

Every mod deploys to its own folder under `engine\SanctuaryMods\` — outside
the BepInEx tree, alongside the Lua mods, so one folder is the whole of a mod
whether it ships a DLL, Lua files, or both. The loader is the exception: it
deploys to `BepInEx\plugins`, because BepInEx is what loads *it* (see
[ModLoader](#modloader)), and so does the [Mod API](#modapi). So
`dotnet build` — of one project or the whole `SanctuaryMods.sln` — is the
entire iteration loop, no game restart, except for those two, which need one.
While the game is running a build compiles but does not deploy (a hot load
mid-match can break it); add `-p:LiveDeploy=true` when you want it live.

### Developer tools

[CLAUDE.md](CLAUDE.md) lists them with when to use each; in short:

- [tools/probe.ps1](tools/probe.ps1) + [tools/Probe](tools/Probe/) — a
  dev-only plugin that drives the running game from a command file: Lua with
  results, screenshots, UI hierarchy dumps, replays, a skirmish vs AI.
  Never released.
- [tools/game-ref.ps1](tools/game-ref.ps1) — a per-build local snapshot of the
  game (decompiled C#, Lua, an API listing), a diff between builds, and a
  check of every name the mods use against it. Run it after each game patch.
- [tools/lua-check.ps1](tools/lua-check.ps1) — compiles every Lua file and
  every Lua chunk inside C# strings with the game's own LuaJIT.
- [tools/gamelog.ps1](tools/gamelog.ps1) — both game logs without the noise,
  and which build of each mod is deployed.
- [tools/worktrees.ps1](tools/worktrees.ps1) — uncommitted and unmerged work
  across every worktree.

[examples/](examples/) and [templates/](templates/) are deliberately outside
the solution and outside `Directory.Build.props` (each has a stub that stops
the import): they build exactly as a third-party mod would, against the
installed `BepInEx\plugins\Sanctuary.ModApi.dll`.

### Cutting a release

[tools/pack-release.ps1](tools/pack-release.ps1) builds a mod's two zips and,
with `-Publish`, creates the GitHub release:

```powershell
pwsh -NoProfile -File tools/pack-release.ps1 -Mod EcoManager -Body body.txt
```

Version and display name come from the mod's `[BepInPlugin]` attribute, so that
attribute is the only place a version is written and a release cannot disagree
with what the game reports. A Lua-only gameplay mod has no attribute; its
`mod.json` name and version are used, and the zip's files are checked against
their committed blobs byte for byte. `-Body` is plain text that lands in the `README.txt`
of both zips verbatim. Output goes to `release/` (gitignored), staged through a
temp folder so packing never disturbs a running game.

The BepInEx tree is copied from your own install by allowlist — never
`BepInEx\config\` wholesale, which holds your per-mod settings — plus the
vendored [tools/BepInEx.cfg](tools/BepInEx.cfg), which carries the
`HideManagerGameObject = true` that Sanctuary requires. Both zips are then
checked for the paths they must contain and for stray config before the script
will publish, and publishing refuses if the tag already exists.

The full checklist, including what *not* to re-release, is in
[.claude/skills/release-mod](.claude/skills/release-mod/SKILL.md).

## Setup

1. Install the .NET SDK (8+).
2. Install [BepInEx 5.4.x win-x64](https://github.com/BepInEx/BepInEx/releases)
   into the game's `engine` folder (the one with `Sanctuary.exe`) — extract so
   `winhttp.dll` and `BepInEx\` sit next to the exe. Run the game once to let
   BepInEx generate its folders.
   Then set `HideManagerGameObject = true` under `[Chainloader]` in
   `BepInEx\config\BepInEx.cfg`: Sanctuary destroys foreign root GameObjects
   after start-up, and without it the BepInEx manager object (and every
   plugin on it) dies right after `Awake`, so plugins load but never update.
   The release zips ship this setting.
3. `dotnet build SanctuaryMods.sln` — the projects reference game assemblies
   from the install (override with `-p:GamePath=...`, default is the playtest)
   and copy each built mod into `engine\SanctuaryMods\` automatically.
4. Launch the game; check `BepInEx\LogOutput.log` for the load lines, and
   open **Mods** from the menu's sidebar (or press **F8**).

## Removal

Delete `engine\winhttp.dll` and the `engine\BepInEx\` folder (and
`engine\SanctuaryMods\` if you used Lua mods). Steam's "verify integrity"
never sees any of them — they are not game files.
