using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using EM.Core;
using EM.Network;
using EM.Network.Lobby;
using EM.UI;
using HarmonyLib;
using Sanctuary.ModApi;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// Development-only test driver: lets a script (tools\probe.ps1) drive the
// running game with nobody at the keyboard. Never packed or released.
//
// Protocol, all under %LOCALAPPDATA%\SanctuaryProbe:
//   in\<id>.cmd   a batch of commands, one per line, written by the client
//   out\<id>.txt  that batch's output, written once every line has run
//   alive.txt     rewritten every 2 s while the probe is loaded
//   shots\        screenshots
//   probe.log     every batch and its output, appended
//
// Lines run in order, at most one batch at a time. `wait`, `waitlua` and
// `waitmatch` hold the batch without blocking the game. `help` lists verbs.
[BepInPlugin("com.sanctuarydb.devprobe", "Sanctuary Probe (dev)", "1.0.0")]
public class Probe : BaseUnityPlugin
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SanctuaryProbe");

    private const string Help = @"verbs (one per line; # starts a comment):
  state                       window, Lua VM, replay, lobby
  plugins                     loaded plugins (stale hot-reload copies marked)
  objects [Type...]           live Unity objects per type, Type=count (default Texture2D Sprite Font
                              RenderTexture Material GameObject; other types by name or full name)
  lua <statements>            run in the client VM; prints whatever it returns
  eval <expr>                 lua 'return <expr>'
  luaf <file>                 lua from a file (absolute, or relative to the probe dir)
  get <global>                a _G global as ModLua reads it
  depth <n>                   how deep lua/eval print tables (default 2)
  wait <seconds>              hold the batch
  waitlua <seconds> <expr>    hold until <expr> is truthy, or fail after <seconds>
  waitmatch [seconds]         hold until the client Lua VM exists (default 180)
  shot <name>                 screenshot to shots\<name>.png
  (a path is a full path, its tail, or a name; path#n is the nth shown match from 0, in hierarchy order)
  tree [path] [depth]         UI hierarchy (default: the InterfaceManager canvas, depth 4)
  find <text> [max]           transforms whose name contains <text>, with paths
  texts [path]                every TMP text under path
  click <path>                Button.onClick, or a synthetic pointer click
  tap <path>                  a synthetic pointer down/up/click, never Button.onClick
  rtap <path>                 the same with the right button
  pointer <enter|exit|down|up|click> <path>   one pointer event
  active <0|1> <path>         SetActive (hide a stuck overlay for a shot)
  scroll <0..1> <path>        the ScrollRect on or around path (1 = top)
  scroll to <path>            scroll path's list so path is at the top
  window <Name>               InterfaceManager.TransitionTo(Window.<Name>)
  modspage open|close         the Mod Manager page
  cfg <plugin>                list a plugin's settings (plugin = GUID, type or assembly name)
  cfg <plugin> <Section> <Key> [value]   read, or write (saves to the .cfg - restore it after)
  call <Type.Member> [args]   static method/property/field by reflection (newest copy of the type)
  pfield <plugin> <member> [value]   an instance field/property of the live plugin object
  replay <path|latest>        play a .sanreplay
  seek <tick> | speed <x> | pause | resume | replaystate   ReplayManager's player
  lobby [maxPlayers] [map]    private Steam lobby (default 2 players, The Forge)
  lan [port]                  later lobbies use the LAN backend (no Steam needed; default port 7777)
  maps [text|*]               stock maps; installed map folders matching <text> (* = all)
  ai <slot> | army <slot> <id> | team <slot> <id> | faction <slot> <n>
  ready | start | lobbystate | select <modId;modId> | seat <slot> <mod> <key>|default
  skirmish [map]              lobby + AI opponent + start + waitmatch, in one
  leave                       quit the match to the menu (the game's own QuitGame)
  quit                        close the game (Application.Quit)
  echo <text>";

    private sealed class Batch
    {
        public string Id;
        public Queue<string> Lines;
        public StringBuilder Out = new StringBuilder();
    }

    private Batch _batch;
    private float _waitUntil;
    private Func<bool> _waitCond;
    private string _waitWhat;
    private float _poll, _alive;
    private int _depth = 2;
    private string _instance;

    private void Awake()
    {
        Directory.CreateDirectory(Path.Combine(Dir, "in"));
        Directory.CreateDirectory(Path.Combine(Dir, "out"));
        Directory.CreateDirectory(Path.Combine(Dir, "shots"));
        // A hot reload leaves the previous copy's object running too; only the
        // newest copy may take batches.
        _instance = Guid.NewGuid().ToString("N");
        AppDomain.CurrentDomain.SetData("SanctuaryProbe.owner", _instance);
        Logger.LogInfo("Sanctuary Probe loaded; commands in " + Dir);
    }

    private bool Owner => (AppDomain.CurrentDomain.GetData("SanctuaryProbe.owner") as string) == _instance;

    private void Update()
    {
        if (!Owner) return;
        var now = Time.realtimeSinceStartup;
        if (now >= _alive)
        {
            _alive = now + 2f;
            try { File.WriteAllText(Path.Combine(Dir, "alive.txt"), $"{DateTime.Now:O}\n{StateLine()}\n"); } catch { }
        }

        if (_batch == null)
        {
            if (now < _poll) return;
            _poll = now + 0.25f;
            var next = Directory.GetFiles(Path.Combine(Dir, "in"), "*.cmd").OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault();
            if (next == null) return;
            string[] lines;
            try { lines = File.ReadAllLines(next); File.Delete(next); } catch { return; }
            _batch = new Batch { Id = Path.GetFileNameWithoutExtension(next), Lines = new Queue<string>(lines) };
        }

        // Run lines until one waits or the batch is done.
        for (var guard = 0; guard < 200; guard++)
        {
            if (_waitCond != null)
            {
                bool met;
                try { met = _waitCond(); } catch { met = false; }
                if (met) { Out($"  ({_waitWhat}: ok)"); _waitCond = null; _waitUntil = 0f; }
                else if (now >= _waitUntil) { Out($"  ({_waitWhat}: TIMED OUT, rest of batch skipped)"); _waitCond = null; _waitUntil = 0f; _batch.Lines.Clear(); }
                else return;
            }
            else if (now < _waitUntil) return;

            if (_batch.Lines.Count == 0) { Finish(); return; }
            var line = _batch.Lines.Dequeue().Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            Out("> " + line);
            var sp = line.IndexOf(' ');
            var verb = (sp < 0 ? line : line.Substring(0, sp)).ToLowerInvariant();
            var arg = sp < 0 ? "" : line.Substring(sp + 1).Trim();
            try { Run(verb, arg); }
            catch (Exception e)
            {
                var inner = e is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : e;
                Out("ERROR " + inner.GetType().Name + ": " + inner.Message + "\n" + inner.StackTrace);
            }
            if (_batch == null) return;   // quit finished it early
        }
    }

    private void Finish()
    {
        var text = _batch.Out.ToString();
        var outPath = Path.Combine(Dir, "out", _batch.Id + ".txt");
        try
        {
            File.WriteAllText(outPath + ".tmp", text);
            if (File.Exists(outPath)) File.Delete(outPath);
            File.Move(outPath + ".tmp", outPath);
            File.AppendAllText(Path.Combine(Dir, "probe.log"), $"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} {_batch.Id}\n{text}");
        }
        catch (Exception e) { Logger.LogError("probe: could not write output: " + e.Message); }
        _batch = null;
    }

    private void Out(string s) => _batch?.Out.AppendLine(s);

    private void Wait(string what, float seconds, Func<bool> cond)
    {
        _waitWhat = what;
        _waitUntil = Time.realtimeSinceStartup + seconds;
        _waitCond = cond;
    }

    // ---------------------------------------------------------------- verbs

    private void Run(string verb, string arg)
    {
        var a = arg.Length == 0 ? new string[0] : arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        switch (verb)
        {
            case "help": Out(Help); break;
            case "echo": Out(arg); break;
            case "state": Out(StateLine()); break;
            case "plugins": Plugins(); break;
            case "objects": Objects(a); break;

            case "lua": Out(Lua(arg)); break;
            case "eval": Out(Lua("return " + arg)); break;
            case "luaf":
                var file = Path.IsPathRooted(arg) ? arg : Path.Combine(Dir, arg);
                Out(Lua(File.ReadAllText(file)));
                break;
            case "get": Out(ModLua.GetGlobal(arg) ?? "(nil)"); break;
            case "depth": _depth = int.Parse(arg); Out("depth " + _depth); break;

            case "wait": Wait("wait " + arg, float.Parse(arg, System.Globalization.CultureInfo.InvariantCulture), null); break;
            case "waitlua":
            {
                var secs = float.Parse(a[0]);
                var expr = arg.Substring(a[0].Length).Trim();
                Wait("waitlua " + expr, secs, () =>
                {
                    var r = Lua("return " + expr);
                    return !(r == "nil" || r == "false" || r.StartsWith("ERROR") || r.StartsWith("SYNTAX") || r.StartsWith("(no"));
                });
                break;
            }
            case "waitmatch":
            {
                var secs = a.Length > 0 ? float.Parse(a[0]) : 180f;
                Wait("waitmatch", secs, () => ModLua.Ready && Lua("return __Entities ~= nil") == "true");
                break;
            }

            case "shot":
            {
                var name = arg.Length > 0 ? arg : DateTime.Now.ToString("HHmmss");
                if (!name.EndsWith(".png")) name += ".png";
                var path = Path.Combine(Dir, "shots", name);
                if (File.Exists(path)) File.Delete(path);
                var sc = Type.GetType("UnityEngine.ScreenCapture, UnityEngine.ScreenCaptureModule");
                sc.GetMethod("CaptureScreenshot", new[] { typeof(string) }).Invoke(null, new object[] { path });
                Out(path);
                Wait("shot written", 10f, () => File.Exists(path));
                break;
            }
            case "tree":
            {
                // The path is every word but a trailing depth: paths have spaces.
                var depth = a.Length > 0 && int.TryParse(a[a.Length - 1], out var d) ? d : 4;
                var words = a.Length > 0 && int.TryParse(a[a.Length - 1], out _) ? a.Take(a.Length - 1).ToArray() : a;
                var root = words.Length > 0 ? Find(string.Join(" ", words)) : UiRoot();
                if (root == null) { Out("not found: " + arg); break; }
                var sb = new StringBuilder();
                sb.AppendLine(PathOf(root));
                Walk(root, 0, depth, sb);
                Out(sb.ToString().TrimEnd());
                break;
            }
            case "find":
            {
                var max = a.Length > 1 && int.TryParse(a[a.Length - 1], out var m) ? m : 30;
                var text = a.Length > 1 && int.TryParse(a[a.Length - 1], out _) ? string.Join(" ", a.Take(a.Length - 1)) : arg;
                var hits = SceneTransforms().Where(t => t.name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                foreach (var t in hits.Take(max)) Out((t.gameObject.activeInHierarchy ? "  " : "  (off) ") + PathOf(t));
                Out($"{hits.Count} match(es)");
                break;
            }
            case "texts":
            {
                var root = arg.Length > 0 ? Find(arg) : UiRoot();
                if (root == null) { Out("not found: " + arg); break; }
                foreach (var t in root.GetComponentsInChildren<TMPro.TMP_Text>(true))
                    Out($"{(t.gameObject.activeInHierarchy ? "  " : "  (off) ")}{PathOf(t.transform)} = \"{t.text}\"");
                break;
            }
            case "click":
            case "tap":
            case "rtap":
            {
                var t = Find(arg);
                if (t == null) { Out("not found: " + arg); break; }
                // tap skips Button.onClick: rows whose own pointer handler
                // does something else (the Mods page's folding sections).
                // rtap is tap with the right button (unit and queue buttons).
                var button = verb == "click" ? t.GetComponent<Button>() : null;
                if (button != null) { button.onClick.Invoke(); Out("Button.onClick on " + PathOf(t)); break; }
                var ev = new PointerEventData(EventSystem.current)
                {
                    button = verb == "rtap" ? PointerEventData.InputButton.Right : PointerEventData.InputButton.Left
                };
                ExecuteEvents.Execute(t.gameObject, ev, ExecuteEvents.pointerDownHandler);
                ExecuteEvents.Execute(t.gameObject, ev, ExecuteEvents.pointerUpHandler);
                ExecuteEvents.Execute(t.gameObject, ev, ExecuteEvents.pointerClickHandler);
                Out("pointer down/up/click on " + PathOf(t));
                break;
            }
            case "pointer":
            {
                // pointer <enter|exit|down|up|click> <path>: one pointer event.
                var t = a.Length > 1 ? Find(string.Join(" ", a.Skip(1))) : null;
                if (t == null) { Out("usage: pointer <enter|exit|down|up|click> <path> (not found)"); break; }
                var ev = new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left };
                switch (a[0].ToLowerInvariant())
                {
                    case "enter": ExecuteEvents.Execute(t.gameObject, ev, ExecuteEvents.pointerEnterHandler); break;
                    case "exit": ExecuteEvents.Execute(t.gameObject, ev, ExecuteEvents.pointerExitHandler); break;
                    case "down": ExecuteEvents.Execute(t.gameObject, ev, ExecuteEvents.pointerDownHandler); break;
                    case "up": ExecuteEvents.Execute(t.gameObject, ev, ExecuteEvents.pointerUpHandler); break;
                    case "click": ExecuteEvents.Execute(t.gameObject, ev, ExecuteEvents.pointerClickHandler); break;
                    default: Out("unknown pointer event: " + a[0]); return;
                }
                Out($"pointer {a[0]} on {PathOf(t)}");
                break;
            }
            case "active":
            {
                // active <0|1> <path>: SetActive, for hiding a stuck overlay in a shot.
                var t = a.Length > 1 ? Find(string.Join(" ", a.Skip(1))) : null;
                if (t == null) { Out("usage: active <0|1> <path> (not found)"); break; }
                t.gameObject.SetActive(a[0] == "1" || a[0].Equals("true", StringComparison.OrdinalIgnoreCase));
                Out($"{PathOf(t)} active={t.gameObject.activeSelf}");
                break;
            }
            case "scroll":
            {
                // scroll to <path>: the ScrollRect above path, scrolled so path's top is the view's top.
                if (a.Length >= 2 && a[0] == "to")
                {
                    var target = Find(string.Join(" ", a.Skip(1))) as RectTransform;
                    var sr = target != null ? target.GetComponentInParent<ScrollRect>() : null;
                    if (sr == null || sr.content == null) { Out("no ScrollRect above: " + string.Join(" ", a.Skip(1))); break; }
                    Canvas.ForceUpdateCanvases();
                    var corners = new Vector3[4];
                    target.GetWorldCorners(corners);
                    var content = sr.content;
                    var below = content.rect.yMax - content.InverseTransformPoint(corners[1]).y;
                    var view = sr.viewport != null ? sr.viewport.rect.height : ((RectTransform)sr.transform).rect.height;
                    var y = Mathf.Clamp(below, 0f, Mathf.Max(0f, content.rect.height - view));
                    content.anchoredPosition = new Vector2(content.anchoredPosition.x, y);
                    Out($"scrolled {PathOf(sr.transform)} to {PathOf(target)} ({y:0} of {content.rect.height - view:0})");
                    break;
                }
                // scroll <0..1> <path>: the ScrollRect on or above path, 1 = top.
                if (a.Length < 2 || !float.TryParse(a[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var to))
                {
                    Out("usage: scroll <0..1> <path>");
                    break;
                }
                var t = Find(string.Join(" ", a.Skip(1)));
                var rect = t != null ? t.GetComponentInParent<ScrollRect>() ?? t.GetComponentInChildren<ScrollRect>() : null;
                if (rect == null) { Out("no ScrollRect at or around: " + string.Join(" ", a.Skip(1))); break; }
                rect.verticalNormalizedPosition = Mathf.Clamp01(to);
                Out($"scrolled {PathOf(rect.transform)} to {rect.verticalNormalizedPosition:0.##}");
                break;
            }
            case "window":
                InterfaceManager.Instance.TransitionTo((InterfaceManager.Window)Enum.Parse(typeof(InterfaceManager.Window), arg, true));
                Out("window " + arg);
                break;
            case "modspage":
                ModsPage(arg.Equals("close", StringComparison.OrdinalIgnoreCase) ? "Close" : "Open");
                break;

            case "cfg": Cfg(a); break;
            case "call": Out(Call(a[0], a.Skip(1).ToArray())); break;

            case "replay":
            {
                var path = arg;
                if (arg.Equals("latest", StringComparison.OrdinalIgnoreCase))
                    path = new DirectoryInfo(Path.Combine(Application.persistentDataPath, "Replays"))
                        .GetFiles("*.sanreplay").OrderByDescending(f => f.LastWriteTimeUtc).First().FullName;
                InterfaceManager.Instance.TransitionTo(InterfaceManager.Window.GameLoading);
                Out($"replay {path} -> {NetworkManager.StartReplayPlayback(path, out var err)} {err}");
                break;
            }
            case "seek": Out(Call("SanctuaryHud.Replays.ReplayPlayer.SeekTo", a)); break;
            case "speed": Out(Call("SanctuaryHud.Replays.ReplayPlayer.Speed", a)); break;
            case "pause": Out(Call("SanctuaryHud.Replays.ReplayPlayer.Paused", new[] { "true" })); break;
            case "resume": Out(Call("SanctuaryHud.Replays.ReplayPlayer.Paused", new[] { "false" })); break;
            case "replaystate": Out(ReplayState()); break;

            case "lobby":
            {
                // lobby [maxPlayers] [map]: map as in `maps` (stock name, folder name or Maps/... path).
                var players = a.Length > 0 && int.TryParse(a[0], out var n) ? n : 2;
                var mapArg = string.Join(" ", a.SkipWhile(x => int.TryParse(x, out _)));
                var map = mapArg.Length > 0 ? ResolveMap(mapArg) : LobbyManager.MapTheForge;
                if (map == null) { Out("no map matching '" + mapArg + "' (try maps)"); break; }
                LobbyManager.OnLobbyCreated -= Created;
                LobbyManager.OnLobbyCreated += Created;
                LobbyManager.CreateLobby(new LobbyManager.LobbyProperties
                {
                    name = "Probe lobby",
                    mapPath = map,
                    maxPlayerCount = players,
                    ownerName = LobbyManager.CurrentUserName,
                    type = LobbyManager.LobbyType.Private,
                });
                Out($"create lobby requested on {map} for {players}");
                Wait("lobby created", 20f, () => LobbyManager.CurrentState != null);
                break;
            }
            case "lan":
                // lan [port]: lobbies from now on use the game's LAN (TCP) backend, which needs
                // no Steam - for when the game was started without Steam's session.
                EM.Network.TcpLobbyBackend.Instance.SetListenPort(a.Length > 0 ? ushort.Parse(a[0]) : (ushort)7777);
                LobbyManager.Backend = EM.Network.TcpLobbyBackend.Instance;
                Out("lobby backend: LAN as " + LobbyManager.CurrentUserName);
                break;
            case "maps":
            {
                // maps [text]: the stock four, plus installed map folders whose name
                // contains <text> (all ~140 of them only when asked with `maps *`).
                foreach (var f in typeof(LobbyManager).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.Name.StartsWith("Map") && f.FieldType == typeof(string)))
                    Out($"  {f.Name.Substring(3),-18} {f.GetValue(null)}  (stock)");
                var dirs = Directory.GetDirectories(Path.Combine(Application.dataPath, "Maps")).OrderBy(x => x).ToList();
                if (arg.Length == 0) { Out($"  ({dirs.Count} installed map folders: maps <text> to search, maps * for all)"); break; }
                string Norm(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
                foreach (var d in dirs.Where(d => arg == "*" || Norm(Path.GetFileName(d)).Contains(Norm(arg))))
                    Out("  " + MapPath(d));
                break;
            }
            case "ai":
            {
                var slot = a.Length > 0 ? int.Parse(a[0]) : 1;
                LobbyManager.SetMemberType(LobbyManager.CurrentState.players[slot], (byte)slot, PlayerType.AI);
                Out("AI at slot " + slot);
                break;
            }
            case "army": LobbyManager.SetMemberArmyID(Player(a[0]), int.Parse(a[1])); Out("ok"); break;
            case "team": LobbyManager.SetMemberTeam(Player(a[0]), int.Parse(a[1])); Out("ok"); break;
            case "faction": LobbyManager.SetMemberFaction(Player(a[0]), (Faction)int.Parse(a[1])); Out("ok"); break;
            case "ready":
                LobbyManager.SetMemberIsReady(LobbyManager.CurrentState.players.First(LobbyManager.IsLocalPlayer), true);
                Out("ready");
                break;
            case "start":
                typeof(InterfaceManager).GetMethod("OnLobbyStartGamePressed", BindingFlags.NonPublic | BindingFlags.Instance)
                    .Invoke(InterfaceManager.Instance, null);
                Out("start pressed");
                break;
            case "lobbystate": LobbyState(); break;
            case "select":
                Out("SetSelection -> " + Lobby.SetSelection(arg.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)));
                break;
            case "seat":
                Out("SetSeatAi -> " + (a[1] == "default" ? Lobby.SetSeatAi(int.Parse(a[0]), null, null) : Lobby.SetSeatAi(int.Parse(a[0]), a[1], a[2])));
                break;
            case "skirmish":
                // Each AI setter resends the cached army/team, so the team must
                // land before the army (sanctuary-probe-skirmish memory).
                Prepend(("lobby 2 " + arg).Trim(), "wait 3", "ai 1", "wait 3", "army 0 1", "wait 2", "team 1 2", "wait 3",
                        "army 1 2", "wait 3", "ready", "wait 3", "lobbystate", "start", "waitmatch 240", "wait 5", "state");
                break;

            case "leave":
                // The game's own quit-to-menu: CleanUpGame, then reload the scene.
                EM.DOTS.Engine.Loader.EngineLoader.Instance.QuitGame();
                Out("left the match");
                break;
            case "quit":
                Out("closing the game");
                Finish();
                Application.Quit();
                break;
            case "pfield": PluginField(a); break;
            default:
                Out("unknown verb '" + verb + "' (try help)");
                break;
        }
    }

    private void Prepend(params string[] lines)
    {
        var rest = _batch.Lines.ToList();
        _batch.Lines = new Queue<string>(lines.Concat(rest));
    }

    // ------------------------------------------------------------------ Lua

    // The body is compiled with loadstring so syntax errors come back too,
    // and runs under pcall: ModLua.Run itself swallows Lua errors.
    private string Lua(string body)
    {
        if (!ModLua.Ready) return "(no Lua VM: not in a match or replay)";
        var level = 1;
        while (body.Contains("]" + new string('=', level) + "]")) level++;
        var eq = new string('=', level);
        // lua-check: ignore (the long-bracket level is interpolated)
        var chunk = $@"_G.__probe_out = 'ERROR chunk did not run'
local f, e = loadstring([{eq}[
{body}
]{eq}], '=probe')
if not f then _G.__probe_out = 'SYNTAX ' .. tostring(e) return end
local function dump(v, d)
  local t = type(v)
  if t == 'string' then return string.format('%q', v) end
  if t ~= 'table' or d <= 0 then return tostring(v) end
  local parts, n = {{}}, 0
  for k, x in pairs(v) do
    n = n + 1
    if n > 40 then parts[#parts + 1] = '...'; break end
    parts[#parts + 1] = tostring(k) .. '=' .. dump(x, d - 1)
  end
  return '{{' .. table.concat(parts, ', ') .. '}}'
end
local function cap(...) return select('#', ...), {{...}} end
local n, r = cap(pcall(f))
if not r[1] then _G.__probe_out = 'ERROR ' .. tostring(r[2]) return end
local outs = {{}}
for i = 2, n do outs[#outs + 1] = dump(r[i], {_depth}) end
_G.__probe_out = n > 1 and table.concat(outs, '\t') or '(no value)'";
        _ = ModLua.Run(chunk);
        return ModLua.GetGlobal("__probe_out") ?? "(chunk failed before it could report; see LogOutput.log)";
    }

    // ------------------------------------------------------------- reflection

    // The newest loaded copy of a type: a hot reload leaves every older copy
    // in the AppDomain, and assemblies enumerate in load order.
    private static Type NewestType(string fullName) => AppDomain.CurrentDomain.GetAssemblies()
        .Where(x => !x.IsDynamic).Select(x => { try { return x.GetType(fullName, false); } catch { return null; } })
        .LastOrDefault(t => t != null);

    private static string Call(string target, string[] args)
    {
        var dot = target.LastIndexOf('.');
        var type = NewestType(target.Substring(0, dot)) ?? throw new Exception("type not loaded: " + target.Substring(0, dot));
        var name = target.Substring(dot + 1);
        const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        var prop = type.GetProperty(name, F);
        if (prop != null)
        {
            if (args.Length > 0) prop.SetValue(null, Convert(args[0], prop.PropertyType));
            return Show(prop.GetValue(null));
        }
        var field = type.GetField(name, F);
        if (field != null)
        {
            if (args.Length > 0) field.SetValue(null, Convert(args[0], field.FieldType));
            return Show(field.GetValue(null));
        }
        var method = type.GetMethods(F).FirstOrDefault(m => m.Name == name && m.GetParameters().Length == args.Length)
                     ?? throw new Exception($"no static {name} with {args.Length} parameter(s) on {type.FullName} ({type.Assembly.GetName().Name})");
        var ps = method.GetParameters();
        var result = method.Invoke(null, args.Select((s, i) => Convert(s, ps[i].ParameterType)).ToArray());
        return method.ReturnType == typeof(void) ? "(void)" : Show(result);
    }

    private static object Convert(string s, Type t)
    {
        if (t == typeof(string)) return s;
        if (t.IsEnum) return Enum.Parse(t, s, true);
        return System.Convert.ChangeType(s, t, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string Show(object o)
    {
        if (o == null) return "(null)";
        if (o is string s) return "\"" + s + "\"";
        if (o is System.Collections.IEnumerable e && !(o is string))
            return "[" + string.Join(", ", e.Cast<object>().Take(50).Select(x => x?.ToString() ?? "null")) + "]";
        return o.ToString();
    }

    // ------------------------------------------------------------------ state

    private static string StateLine()
    {
        var parts = new List<string>();
        try
        {
            var im = InterfaceManager.Instance;
            var cw = im == null ? null : typeof(InterfaceManager).GetField("currentWindow", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(im);
            parts.Add("window=" + (cw ?? "?"));
        }
        catch { parts.Add("window=?"); }
        parts.Add("luaVM=" + ModLua.Ready);
        try { parts.Add("lobby=" + (LobbyManager.CurrentState != null ? LobbyManager.CurrentState.players.Count + " players" : "none")); } catch { }
        try { parts.Add("replay=" + ReplayState()); } catch { }
        return string.Join("  ", parts);
    }

    private static string ReplayState()
    {
        var t = NewestType("SanctuaryHud.Replays.ReplayPlayer");
        if (t == null) return "(ReplayManager not loaded)";
        object P(string n) => t.GetProperty(n, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                              ?? t.GetField(n, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        return $"{P("Current")} tick={P("CurrentTick")}/{P("TotalTicks")} speed={P("Speed")} paused={P("Paused")}";
    }

    private void Plugins()
    {
        var all = Resources.FindObjectsOfTypeAll<BaseUnityPlugin>().Where(p => p != null).ToList();
        foreach (var g in all.GroupBy(p => p.Info?.Metadata?.GUID ?? p.GetType().FullName).OrderBy(g => g.Key))
        {
            var copies = g.ToList();
            foreach (var p in copies)
            {
                var meta = p.Info?.Metadata;
                var asm = p.GetType().Assembly.GetName();
                Out($"  {g.Key}  {meta?.Name} {meta?.Version}  [{asm.Name} {asm.Version}]  enabled={p.enabled}" + (copies.Count > 1 ? "  (one of " + copies.Count + " copies)" : ""));
            }
        }
    }

    // Live object counts, for hot-reload leak checks (probe.ps1 leakcheck):
    // what a reload's OnDestroy fails to Destroy stays alive and counted.
    private static readonly string[] LeakTypes = { "Texture2D", "Sprite", "Font", "RenderTexture", "Material", "GameObject" };

    private void Objects(string[] names)
    {
        foreach (var name in names.Length > 0 ? names : LeakTypes)
        {
            var full = name.Contains(".") ? name : "UnityEngine." + name;
            var t = AppDomain.CurrentDomain.GetAssemblies().Where(x => !x.IsDynamic)
                .Select(x => { try { return x.GetType(full, false); } catch { return null; } })
                .FirstOrDefault(x => x != null && typeof(UnityEngine.Object).IsAssignableFrom(x));
            Out(t == null ? $"{name}=? (no such UnityEngine.Object type)" : $"{name}={Resources.FindObjectsOfTypeAll(t).Length}");
        }
    }

    private void Cfg(string[] a)
    {
        var plugins = Resources.FindObjectsOfTypeAll<BaseUnityPlugin>().Where(p => p != null && (
            string.Equals(p.Info?.Metadata?.GUID, a[0], StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p.GetType().Name, a[0], StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p.GetType().Assembly.GetName().Name, a[0], StringComparison.OrdinalIgnoreCase))).ToList();
        if (plugins.Count == 0) { Out("no plugin " + a[0]); return; }
        var live = plugins.Where(p => p.enabled).DefaultIfEmpty(plugins.Last()).Last();
        if (a.Length == 1)
        {
            foreach (var kv in live.Config.OrderBy(k => k.Key.Section).ThenBy(k => k.Key.Key))
                Out($"  [{kv.Key.Section}] {kv.Key.Key} = {kv.Value.GetSerializedValue()}");
            return;
        }
        var def = new ConfigDefinition(a[1], a[2]);
        if (a.Length == 3)
        {
            Out(live.Config.ContainsKey(def) ? live.Config[def].GetSerializedValue() : "no setting [" + a[1] + "] " + a[2]);
            return;
        }
        var value = string.Join(" ", a.Skip(3));
        // Every copy: a stale hot-reload copy still reads its own ConfigFile.
        var n = 0;
        foreach (var p in plugins.Where(p => p.Config.ContainsKey(def))) { p.Config[def].SetSerializedValue(value); n++; }
        Out($"[{a[1]}] {a[2]} = {value} on {n} plugin cop{(n == 1 ? "y" : "ies")} (saved to the .cfg; restore it afterwards)");
    }

    // pfield <plugin> <field-or-property> [value]: an instance member of the
    // live plugin object (the newest enabled copy), e.g. a private _visible.
    private void PluginField(string[] a)
    {
        var p = Resources.FindObjectsOfTypeAll<BaseUnityPlugin>().Where(x => x != null && x.enabled && (
            string.Equals(x.Info?.Metadata?.GUID, a[0], StringComparison.OrdinalIgnoreCase) ||
            string.Equals(x.GetType().Name, a[0], StringComparison.OrdinalIgnoreCase) ||
            string.Equals(x.GetType().Assembly.GetName().Name, a[0], StringComparison.OrdinalIgnoreCase))).LastOrDefault();
        if (p == null) { Out("no enabled plugin " + a[0]); return; }
        const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var t = p.GetType();
        var f = t.GetField(a[1], F);
        var pr = f == null ? t.GetProperty(a[1], F) : null;
        if (f == null && pr == null) { Out($"no field or property {a[1]} on {t.FullName}"); return; }
        if (a.Length > 2)
        {
            var value = string.Join(" ", a.Skip(2));
            if (f != null) f.SetValue(p, Convert(value, f.FieldType)); else pr.SetValue(p, Convert(value, pr.PropertyType));
        }
        Out(Show(f != null ? f.GetValue(p) : pr.GetValue(p)));
    }

    private void LobbyState()
    {
        var state = LobbyManager.CurrentState;
        if (state == null) { Out("not in a lobby"); return; }
        Out("players: " + string.Join(", ", state.players.Select((p, i) => $"[{i}] {p.name}/{p.type} army={p.armyID} team={p.team} faction={(int)p.faction} ready={p.isReady}")));
        try { Out("mods selected: " + string.Join(", ", Lobby.Selection.Select(s => s.Id))); } catch { }
        try { Out("start blocked: " + (Lobby.StartBlockedReason ?? "no")); } catch { }
        Out($"canStart={LobbyManager.CanStartGame()} status={LobbyManager.lobbyGameStatus}");
    }

    // A stock map by its LobbyManager.Map* name ("forge", "WhiteDesert"), a map
    // folder under Sanctuary_Data\Maps by part of its name ("zone control"),
    // or a "Maps/<dir>/<file>.sanmap" path as is.
    private static string ResolveMap(string arg)
    {
        if (arg.Contains("/") || arg.EndsWith(".sanmap", StringComparison.OrdinalIgnoreCase)) return arg;
        string Norm(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        var key = Norm(arg);
        var stock = typeof(LobbyManager).GetFields(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(f => f.Name.StartsWith("Map") && f.FieldType == typeof(string) && Norm(f.Name.Substring(3)).Contains(key));
        if (stock != null) return (string)stock.GetValue(null);
        var dir = Directory.GetDirectories(Path.Combine(Application.dataPath, "Maps"))
            .OrderBy(d => d.Length).FirstOrDefault(d => Norm(Path.GetFileName(d)).Contains(key));
        return dir == null ? null : MapPath(dir);
    }

    private static string MapPath(string dir)
    {
        var file = Directory.GetFiles(dir, "*.sanmap").FirstOrDefault();
        return "Maps/" + Path.GetFileName(dir) + "/" + (file != null ? Path.GetFileName(file) : Path.GetFileName(dir) + ".sanmap");
    }

    private static LobbyPlayer Player(string slot) => LobbyManager.CurrentState.players[int.Parse(slot)];

    private void Created(bool ok, LobbyState state, string reason)
    {
        LobbyManager.OnLobbyCreated -= Created;
        Logger.LogInfo($"probe: lobby created ok={ok} reason={reason}");
        if (!ok) return;
        var m = typeof(InterfaceManager).GetMethod("CreateLobby", BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(LobbyState) }, null);
        if (m != null) m.Invoke(InterfaceManager.Instance, new object[] { state });
        else InterfaceManager.Instance.TransitionTo(InterfaceManager.Window.Lobby);
    }

    private void ModsPage(string method)
    {
        var n = 0;
        foreach (var p in Resources.FindObjectsOfTypeAll<BaseUnityPlugin>())
        {
            if (p == null || p.GetType().Name != "ModManagerPlugin" || !p.enabled) continue;
            var page = p.GetType().GetField("_page", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(p);
            if (page == null) continue;
            page.GetType().GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Invoke(page, null);
            n++;
        }
        Out(n > 0 ? method + " on the Mods page" : "Mod Manager page not found");
    }

    // ------------------------------------------------------------------- UI

    private static IEnumerable<Transform> SceneTransforms() =>
        Resources.FindObjectsOfTypeAll<Transform>().Where(t => t != null && t.gameObject.scene.IsValid());

    private static Transform UiRoot()
    {
        var im = InterfaceManager.Instance;
        if (im == null) return null;
        var root = im.transform;
        while (root.parent != null) root = root.parent;
        return root;
    }

    // An exact path, else a path suffix, else a name; active objects first.
    private static Transform Find(string path)
    {
        // path#n: the nth (from 0) shown match in hierarchy order, for
        // siblings that share a name. FindObjectsOfTypeAll has no order of
        // its own, and the game pools buttons by scaling them to nothing.
        var nth = -1;
        var hash = path.LastIndexOf('#');
        if (hash > 0 && int.TryParse(path.Substring(hash + 1), out var n))
        {
            nth = n;
            path = path.Substring(0, hash);
        }
        var all = SceneTransforms().ToList();
        if (nth >= 0)
            return all.Where(t => t.gameObject.activeInHierarchy && t.localScale.x > 0.01f &&
                                  (PathOf(t) == path || PathOf(t).EndsWith("/" + path) || t.name == path))
                .OrderBy(HierarchyKey, StringComparer.Ordinal)
                .Skip(nth).FirstOrDefault();
        var hit = all.Where(t => PathOf(t) == path)
            .Concat(all.Where(t => PathOf(t).EndsWith("/" + path)))
            .Concat(all.Where(t => t.name == path))
            .ToList();
        return hit.FirstOrDefault(t => t.gameObject.activeInHierarchy) ?? hit.FirstOrDefault();
    }

    private static string PathOf(Transform t) => t.parent == null ? t.name : PathOf(t.parent) + "/" + t.name;

    // Sorts as the hierarchy reads, top to bottom: sibling indices from the root.
    private static string HierarchyKey(Transform t) =>
        (t.parent == null ? "" : HierarchyKey(t.parent) + "/") + t.GetSiblingIndex().ToString("D5");

    private static void Walk(Transform t, int depth, int max, StringBuilder sb)
    {
        for (var i = 0; i < t.childCount; i++)
        {
            var c = t.GetChild(i);
            var extra = "";
            var cg = c.GetComponent<CanvasGroup>();
            if (cg) extra += $" cg={cg.alpha:0.00}";
            var cv = c.GetComponent<Canvas>();
            if (cv) extra += $" canvas(sort={cv.sortingOrder})";
            var img = c.GetComponent<Image>();
            if (img) extra += $" img({(img.sprite ? img.sprite.name : "-")},a={img.color.a:0.00}{(img.enabled ? "" : ",disabled")})";
            if (c.GetComponent<Button>()) extra += " button";
            var txt = c.GetComponent<TMPro.TMP_Text>();
            if (txt) extra += $" \"{(txt.text.Length > 40 ? txt.text.Substring(0, 40) + "…" : txt.text)}\"";
            if (c is RectTransform rt) extra += $" {rt.rect.width:0}x{rt.rect.height:0}";
            sb.AppendLine(new string(' ', (depth + 1) * 2) + $"[{i}] {c.name}{(c.gameObject.activeSelf ? "" : " (off)")}{extra}");
            if (depth + 1 < max) Walk(c, depth + 1, max, sb);
        }
    }
}
