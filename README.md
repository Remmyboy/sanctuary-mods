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

Lobby-compatible is not the same as safe. Every DLL here, like any BepInEx
plugin, is a full-trust client plugin: it runs inside the game process with
the permissions of the Windows account playing, and an unchanged Lua hash is
no check against cheating or harmful code. Nothing in the game or the loader
enforces good behaviour, so install DLL mods only from a source you trust.

Every release ships two zips. **Standalone** is everything — BepInEx, the mod
loader and the mod — for a clean install; extract it into the game's `engine`
folder. **ModManager** is just the mod, for an install that already has the
[Mod Manager](#modmanager); it appears under UI Mods and can be switched on and
off from there. Each mod builds to `<name>.dll`, and the project link is its
source.

| Project | Download | What it does |
| --- | --- | --- |
| [SanctuaryHud](SanctuaryHud/) | [**0.13.1**](https://github.com/Remmyboy/sanctuary-mods/releases/tag/SanctuaryHud-0.13.1) | The mini-map the game doesn't have; economy strip in the game's own style, optionally replacing the built-in panel; SanctuaryUI: the orders row, unit and build card, selection row and build strip docked into one panel in place of the game's bottom panels, all built on the game's own UI canvas; commander widget and alerts; reclaim values and build countdowns over the map |
| [IdleEngineers](IdleEngineers/) | [**0.5.3**](https://github.com/Remmyboy/sanctuary-mods/releases/tag/IdleEngineers-0.5.3) | Idle engineers and factories as clickable tiles, in the eco panels' shape, on the game's own UI canvas |
| [EcoManager](EcoManager/) | [**0.7.3**](https://github.com/Remmyboy/sanctuary-mods/releases/tag/EcoManager-0.7.3) | BUILD and ALLOY tile panels in FA's shape, on the game's own UI canvas: everything under construction by spend, extractors by tier; an engineer's assist starts an upgrade and holds it paused until an engineer starts building it |
| [BuildHotkeys](BuildHotkeys/) | [**0.3.3**](https://github.com/Remmyboy/sanctuary-mods/releases/tag/BuildHotkeys-0.3.3) | One hotkey per *role*, same key every faction, cycling by tier; pause and repeat-build keys; extractor placement that snaps at screen size |
| [LadderReporter](LadderReporter/) | [**0.3.4**](https://github.com/Remmyboy/sanctuary-mods/releases/tag/LadderReporter-0.3.4) | Reports ranked results; launches matchmade games |
| [ReplayManager](ReplayManager/) | [**0.4.3**](https://github.com/Remmyboy/sanctuary-mods/releases/tag/ReplayManager-0.4.3) | Watch the game's replays fog-free from any seat, with every economy |
| [CameraUtilities](CameraUtilities/) | [**0.1.2**](https://github.com/Remmyboy/sanctuary-mods/releases/tag/CameraUtilities-0.1.2) | Switches off icons, range rings, order lines and the UI, and unlocks how far out units are drawn, for cinematics |
| [ModManager](ModManager/) | [**0.6.1**](https://github.com/Remmyboy/sanctuary-mods/releases/tag/ModManager-0.6.1) | Mods page in the menu's side bar and on F8 in a match: mod toggles, settings (switches, sliders, text) with their descriptions on hover, Lua overlays |
| [MapLocalFiles](MapLocalFiles/) | — | Lets Lua read files from the loaded map's folder |
| [ModLoader](ModLoader/) | [**1.3.1**](https://github.com/Remmyboy/sanctuary-mods/releases/tag/ModLoader-1.3.1) | Loads and hot-reloads every mod above from `SanctuaryMods` |

[All releases](https://github.com/Remmyboy/sanctuary-mods/releases) · MapLocalFiles
has no release of its own yet; build it from source if you need it.

## SanctuaryHud

- **Economy strip** across the top: alloy on the left, energy on the right,
  each showing current storage, gross income, gross spend and net per second,
  over a capacity bar that lengthens with your storage and reddens as the store
  heads for empty. `STALL −N/s` appears when a resource can't pay for what is
  queued. Source: Harmony postfix on `SanctuaryUI.EconomyPanelUI`, the C#
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
- **Hide the game's own bars** (`Overlay · HideGameEconomyBars`, off by
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
  which read small. The mini-map has its own size, by dragging its corner. The gross in and gross out figures sit one
  over the other beside the net, so the two figures being compared line up.
  Each half leads with its resource's mark — an ingot for alloy, a bolt for
  energy — in place of the word; the same marks sit in front of every alloy,
  energy and build-power figure across these mods (a hammer for build
  power, a clock for time), so a figure never needs a label.

### SanctuaryUI

The HUD's own versions of the game's panels along the bottom of the screen,
under one switch (`SanctuaryUI · Enabled`, on by default) with everything
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
thin dividers where pieces meet. `PanelArt` (off by default) dresses the
plates in the game's own dashed panel sprite instead, so they are framed
as its panels are.

- **Orders row** (`OrdersRow`) in place of the game's orders panel
  bottom-left. The game draws all twenty-one order buttons for any selection
  and dims the ones that don't apply; of the bright ones only Stop and the
  toggles (pause, repeat build, shield, intel, production) do anything when
  clicked in the current build — the Lua registers no click function for
  move, attack, patrol and the rest, which are hotkeys and right-clicks
  anyway. The row shows only the buttons the game has enabled for the
  selection, and with `OrdersHideInert` (on by default) only the ones that
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
  `UnitCardTidyGameCard` (on by default) still hides the template id on the
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
  keeping roughly your current zoom.
- **Reclaim values** over the map while **Left Alt** is held (`Reclaim ·
  HoldKey`; `None` keeps them up permanently): the alloys left in every
  wreck and harvestable prop the client knows about, drawn at the spot.
  Values closer together on screen than `ClusterPixels` (110 at 1080p) are
  summed into one figure at their value-weighted centre, so zoomed out a
  battlefield reads as one number the way FAF's overlay groups it, rather
  than a smear of digits. Energy shows as a smaller amber `E` line only where
  it is the point. `MinValue` hides trivia. The prop table
  (`__Entities.Props`) holds wrecks and map props alike with their template's
  `economy.harvest`, and the host streams `reclaimProgress` as they are
  eaten, so what is left is `harvest × (1 − progress / harvestTime)`. A
  decayed wreck stays in that table with no render entity behind it, so each
  is checked with `Engine.IsValidLocalID` before it counts. Positions are
  cached Lua-side per prop; only the values are re-read, once a second.
- **Build countdowns** (`BuildEta · Enabled`): under your structures still
  under construction, upgrades included, a `m:ss` time-to-finish and a thin
  progress bar. The rate is measured from successive progress samples
  (half-second poll, filtered), so it reflects whatever is actually assisting;
  before anything has been measured the template's own build time stands in.
  The colour says what is happening to it: normal while something is
  building it, dark orange while your alloy or energy is stalling (a stall
  throttles every build), and red once nothing is building it and it hasn't
  moved for a few seconds, an abandoned site; the estimate stays either way,
  since there is no better number. What is building a site comes from the
  units whose build routine targets it (`isBuilding` / `buildTarget`, which
  the host reports to every client). An upgrade whose upgrader is paused and
  that nothing is building is left out altogether; an engineer assisting it
  builds straight through the pause, and then it shows. A game pause (the
  economy stream going quiet) freezes the clocks instead. Labels are placed
  soonest-first, one that
  would overlap another is skipped, and `MaxLabels` (12) caps them, so a
  busy base shows the handful nearest completion rather than a wall.
- **Alerts** (`Alerts · …`): toasts top-centre under the strip, with a short
  sound at `Volume` when `Sound` is on (it is off by default; voice packs
  below). *Commander under attack* on
  any health loss, re-sounding at most every eight seconds while it goes on;
  *commander critical* once below `CriticalFraction` (35%), re-armed after
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
  the client's own `GetFocusArmy`, where `-1` is the all-armies view; a
  value it cannot read counts as focused, so a failed read is never what
  makes the HUD vanish. The mini-map stays up regardless: it is the one
  thing here that reads just as well watching everybody.

  Which completions get a toast is one switch each under `CompleteToasts`,
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

**It shows what the game shows, and no more.** The host broadcasts every unit
in the game to every client and leaves fog, intel and economy filtering to the
client, so a mini-map that plotted `__Entities.Units` would be a maphack. The
filter is the client's own `ClientUnit:IsHighlightable()` — vision or radar,
and not an upgrade shell — so the set of contacts on the mini-map is exactly
the set the game is already drawing on the battlefield.

**A radar contact is not told on.** It would be easy to draw every contact
with its real strategic icon, and it would be cheating: `OnIntelRadar`
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
than nothing known (`Map · ShowFog`, `Map · FogDarkness`).

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

Contacts are re-read a configurable 8 times a second (`Map · RefreshHz`, 2 to
20); army colours and deposits every two seconds, since they barely change.
The contact sweep is a single Lua chunk that walks each army's units, applies
the visibility test inside the chunk so invisible units never reach the
payload, and numbers the distinct icon names it saw so a name is sent once
rather than once per unit. Colours are read off the army object rather than
derived, because they are handed out by registration-order colour id and not by
lobby army id — deriving one lands on somebody else's colour.

In a replay the focus army is whatever ReplayManager is showing; the
all-armies view focuses every army, which the client's own intel code treats as
seeing everything, so the mini-map fills in without any special case.

Hotkeys: **F10** toggles the overlay, **F2** the mini-map, **F9** dumps the UI
hierarchy to the log.
Everything the HUD draws steps aside while the game's pause menu or a
front-end screen (settings, the Mods page) is open over the match, as the
game's own HUD does: IMGUI would otherwise draw on top of them.

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
timer, which is what makes its factories cycle too, and `Cycle.Seconds` (0 by
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
`Cancel.ClearFactoryQueue` rebinds or blanks it.

`Menu.PauseMenuKey` moves the pause menu off escape — to **F11**, say; F1 is
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
"upgrade structure" hotkey still works.

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
`Cycle.Seconds` is set: without a cycle window a repeat press queues another of
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
same way. So ten times a second, while the game has focus, any modifier the
record holds but the keyboard reports up is cleared, with a line in the log
when that happens.

Nothing here edits a Lua file, so `ComputeLuaHash` is untouched and a modded
client still joins unmodded lobbies. The binding is a runtime insert into
`inputSystem.lua`'s `LoadedActionMap` (which `CallAction` reads live on every
event, so it takes effect immediately and is restored on unload), and the build
goes through `constructionPanel.lua`'s own `ConstructionClickFunction` — the
same observer check, the same local prediction and the same host-validated
command a button click sends. Returning `false` when nothing matched lets the
key fall through to whatever it normally does, and because chat disables every
action group but `MouseControls`, typing already suppresses these for free.

### Toggles

`Toggles · PauseKey` (**X**) pauses the selected factories and engineers and
again resumes them; `Toggles · RepeatBuildKey` (**Z**) switches repeat build
on the selected factories and again off. Each does what the orders panel's
toggle does — on if any selected unit has it off, else off — through the
game's own `SetToggle` command. Both sit *behind* whatever else the key does
here: a build role on the same key fires first, and the toggle only when
that had nothing to build for the selection. X is the point-defence role by
default, so with engineers selected X builds point defence and with
factories selected, which have nothing under that role, X pauses them.
Blank either to unbind.

### Extractor placement

Placing an extractor snaps it onto a deposit near the cursor. The game's
`FindClosestResourceSpot` fixes that at 8 world units, which zoomed out is a
couple of pixels. `Placement · ExtractorSnapPixels` (40) makes the snap a
fixed size on screen instead: the mod turns that pixel radius into world
units from the camera's height and field of view and pushes it into the
hook a few times a second as you zoom. `Placement · ExtractorSnapDistance`
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

## ReplayManager

Makes the game's own replays watchable properly: any player's point of view
or every army at once, the fog lifted, every army's economy with whole-game
totals, and a transport with pause, speed, forward seek and restart. Since the
playtest update of 2026-09-04 the game records every match to
`%USERPROFILE%\AppData\LocalLow\Enhearten Media PTY\Sanctuary\Replays\*.sanreplay`
and plays them from the main menu's replay list; the panel appears whenever
one is playing, and **F7** shows and hides it.

**What the game does.** Its replay is the host-to-client packet stream,
written by the client as it arrives, behind a small header (map, game
version + Lua hash, recording client). Playback goes through
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

**Seats, fog, economy.** The recording's `InitClient` message only seats the
client that recorded it, so the view buttons call the client's own
`SetFocusArmy` (`-1` is the game's all-armies observer mode) and the fog
post-process is switched with the focus (or by hand). The client is marked
an observer so clicks can't issue orders into the void. Every army's economy
comes from a wrapper on `UpdateEconomyTotals.Receive` in the game's command
registry, which keeps all armies' totals and sums income and spend per tick;
the registry hands the receiver an already-decoded payload, so the wrapper
is a few lines. Player names come from the recorded lobby: a wrapper on the
`ReceiveDataClient` global captures `InitClient`'s roster. Both hooks are
installed from a postfix on `ClientLuaInterface.Startup`, before the first
packet is applied, with the half-second poll as fallback.

**The panel** has the clock, play/pause, a log-scale speed slider, +1
minute, a FOG toggle, a TIMELINE toggle that hides the total length and the
seek bar for watching without knowing when the game ends, QUIT, a
forward-only seek bar with a **RESTART** button beside it, and one row
per army: the name button (in the army's own colour) switches to that army's
view, then alloy and energy as a storage bar, net / in / out per second, and
the amount used so far in the game. ALL shows every army. Armies that never
show an economy — the empty slots of a map bigger than the game played on it,
and the neutral army — are left out of the table; once an army has appeared
it keeps its row, so being wiped out does not remove a player. Drag the
title bar to move the panel and the grip in its bottom-right corner to
resize it; both are remembered (`PanelX`, `PanelY`, `PanelScale`). The panel
stays wholly on screen, and `UI · Locked` stops it being dragged. The
resource columns are headed by the HUD's ingot and bolt marks.

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
- **Game UI** — the whole HUD. This mod's own panel is Unity IMGUI rather than
  the game's UI, so it stays up and F4 still gets everything back.
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

A **Mods** entry in the front menu's sidebar (the cube icon, just below
Settings; **F8** opens it too) leading to a full page with two tabs, UI Mods
and Lua Mods. The page is the game's own Settings screen, cloned and refilled:
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
the game. UI mod toggles and settings changes apply immediately; Lua mod
toggles are locked until you leave the match.

It manages two kinds of mods:

**Lua mods** are a mod folder's `*.lua`/`*.santp` files, laid out mirroring the
`LJ\lua` tree (later mods win conflicts, and new files/folders are registered
so Lua directory listings see them). The manager overlays them into the
game's in-memory `FilesCache` — the
single source both the lobby hash and every match's Lua VMs read from — so
mods toggled in the main menu apply at the next match launch, no restart
needed, and nothing on disk is modified. Enabled mods persist in config and
re-apply on startup.

Multiplayer safety falls out of the game's own design: the lobby host compares
`ComputeLuaHash` of the *cache* (not the disk) against each joiner and refuses
mismatches, so everyone in a lobby provably runs the same Lua. The Lua Mods
tab shows the live hash for comparing with friends. Two caveats: `.santp` files
are loaded but **not** hashed (template mods must be coordinated manually or
they desync mid-game), and toggling is blocked while in a lobby or match. A
sample mod, `SanctuaryMods\ExamplePinkArmy`, turns army slot 1 hot pink as a
smoke test (safe to delete).

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

Two things it has to do that a plain BepInEx plugin would not: it rewrites
each assembly's identity per load, because Mono returns the cached assembly
for a byte-load with an identical name and a rebuild would silently keep
running the old code; and it attaches the plugins it loads to BepInEx's own
hidden manager object rather than a GameObject of its own, because Sanctuary
destroys foreign root GameObjects after start-up (the same reason BepInEx
needs `HideManagerGameObject = true` here, see Setup).

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
[ModLoader](#modloader)). So `dotnet build` — of one project or the whole
`SanctuaryMods.sln` — is the entire iteration loop, no game restart.

### Cutting a release

[tools/pack-release.ps1](tools/pack-release.ps1) builds a mod's two zips and,
with `-Publish`, creates the GitHub release:

```powershell
pwsh -NoProfile -File tools/pack-release.ps1 -Mod EcoManager -Body body.txt
```

Version and display name come from the mod's `[BepInPlugin]` attribute, so that
attribute is the only place a version is written and a release cannot disagree
with what the game reports. `-Body` is plain text that lands in the `README.txt`
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
