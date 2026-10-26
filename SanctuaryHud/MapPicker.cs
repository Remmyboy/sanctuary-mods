using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using BepInEx.Configuration;
using EM.Gamedata;
using EM.UI;
using HarmonyLib;
using Michsky.UI.Beam;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using static SanctuaryHud.HudCore;
using Object = UnityEngine.Object;

namespace SanctuaryHud
{
    // Filters for the lobby settings screen's map list: player count, size,
    // and whether custom maps show. The game lists every .sanmap under Maps\
    // in one alphabetical column, which with converted and generated maps
    // installed runs to ~190 rows.
    //
    // Players is the number of ARMY_n spawn markers in the .sanmap, not the
    // game's GetMapMemberLimit (a hardcoded switch that says 8 for anything
    // it doesn't know). Size is the playable area, the rect named exactly
    // "PlayableArea" that the game fences play into, or the terrain when a map
    // has none; snapped to a power of two, since one shipped map is 1023 wide.
    //
    // Custom is any map Steam didn't install: the game's depot manifest in
    // Steam's depotcache lists every file of the build in plain text. Without
    // it (no Steam, a pruned cache) nothing can be told apart, and the custom
    // switch is left out rather than guessing.
    internal static class MapPicker
    {
        internal static ConfigEntry<bool> Enabled;
        private static ConfigEntry<bool> _cfgShowCustom;

        internal static void Bind(ConfigFile config)
        {
            Enabled = config.Bind("QoL", "MapListFilters", false,
                "Filters above the lobby's map list: players, size, and a switch for custom maps (any map Steam didn't install).");
            _cfgShowCustom = config.Bind("QoL", "MapListShowsCustomMaps", true,
                "The map list's custom maps switch, remembered between launches.");
        }

        internal static void ApplyPatch(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(LobbySettingsInterface), nameof(LobbySettingsInterface.UpdateMapList)),
                postfix: new HarmonyMethod(typeof(MapPicker), nameof(UpdateMapListPostfix)));
        }

        private static readonly AccessTools.FieldRef<LobbySettingsInterface, ScrollRect> MapList =
            AccessTools.FieldRefAccess<LobbySettingsInterface, ScrollRect>("mapList");
        private static readonly AccessTools.FieldRef<LobbySettingsInterface, Michsky.UI.Beam.Dropdown> ObserversList =
            AccessTools.FieldRefAccess<LobbySettingsInterface, Michsky.UI.Beam.Dropdown>("observersList");

        private sealed class MapInfo
        {
            public int Players;
            public int Size;
            public bool Custom;
        }

        private struct Row
        {
            public GameObject Go;
            public MapInfo Info;
        }

        // Read once per file version: (full path) -> (length, write time, info).
        private static readonly Dictionary<string, (long, DateTime, MapInfo)> _infoCache =
            new Dictionary<string, (long, DateTime, MapInfo)>(StringComparer.OrdinalIgnoreCase);
        private static HashSet<string> _stock;
        private static bool _stockRead;

        private static readonly List<Row> _rows = new List<Row>();
        private static ScrollRect _list;
        private static GameObject _bar;
        // The list's area as found, to give back.
        private static RectTransform _area;
        private static Vector2 _areaSize;
        private static LayoutElement _areaLe;
        private static VerticalLayoutGroup _vlg;
        private static bool _vlgExpand;
        private static Michsky.UI.Beam.Dropdown _playersDd, _sizeDd;
        private static ButtonManager _customBtn;
        private static int[] _playerValues = Array.Empty<int>(), _sizeValues = Array.Empty<int>();
        // The picks, for the session: player counts ticked (none = any),
        // and the size (0 = any).
        private static readonly HashSet<int> _players = new HashSet<int>();
        private static int _size;

        private static void UpdateMapListPostfix(LobbySettingsInterface __instance, string[] maps)
        {
            try
            {
                var list = MapList(__instance);
                if (list == null || maps == null) return;
                if (Enabled == null || !Enabled.Value)
                {
                    if (_bar != null) Shutdown();
                    return;
                }

                // The game Destroys the old rows, which only happens at the
                // end of the frame: the new ones are the last maps.Length.
                ReadStock(Directory.GetParent(Application.dataPath)?.FullName);
                var sw = Stopwatch.StartNew();
                var content = list.content;
                var first = content.childCount - maps.Length;
                _rows.Clear();
                for (var i = 0; i < maps.Length && first + i >= 0; i++)
                {
                    var info = Info(maps[i]);
                    var go = content.GetChild(first + i).gameObject;
                    _rows.Add(new Row { Go = go, Info = info });
                    ShowDetails(go, info);
                }
                if (sw.ElapsedMilliseconds > 50)
                    _log.LogInfo($"Map list filters: read {maps.Length} maps in {sw.ElapsedMilliseconds} ms.");

                if (_bar == null || _list != list) BuildBar(__instance, list);
                if (_bar == null) return;
                SetOptions();
                Apply();
            }
            catch (Exception e)
            {
                _log.LogWarning($"Map list filters failed: {e}");
            }
        }

        // ---- the bar --------------------------------------------------------

        private static void BuildBar(LobbySettingsInterface ui, ScrollRect list)
        {
            Shutdown();
            _list = list;
            // Scroll View / Content / Front, and Front is a vertical layout:
            // the bar goes in above the list's Content, which gives up the
            // bar's height.
            var area = (RectTransform)list.transform.parent;
            var front = area.parent;
            var vlg = front != null ? front.GetComponent<VerticalLayoutGroup>() : null;
            var observers = ObserversList(ui);
            if (vlg == null || observers == null)
            {
                _log.LogWarning("Map list filters: the lobby settings screen isn't laid out as expected; no filters.");
                return;
            }

            const float height = 40f, gap = 10f;
            _bar = new GameObject("SanctuaryHud Map Filters", typeof(RectTransform));
            var rt = (RectTransform)_bar.transform;
            rt.SetParent(front, false);
            rt.SetSiblingIndex(area.GetSiblingIndex());
            rt.sizeDelta = new Vector2(area.rect.width, height + gap);
            var le = _bar.AddComponent<LayoutElement>();
            le.minHeight = le.preferredHeight = height + gap;
            le.flexibleHeight = 0f;
            // Front force-expands its children, which would split the height
            // between the bar and the list; only the list should stretch.
            _area = area;
            _vlg = vlg;
            _areaSize = area.sizeDelta;
            _vlgExpand = vlg.childForceExpandHeight;
            vlg.childForceExpandHeight = false;
            area.sizeDelta = new Vector2(_areaSize.x, _areaSize.y - height - gap - vlg.spacing);
            if (vlg.childControlHeight && area.GetComponent<LayoutElement>() == null)
            {
                _areaLe = area.gameObject.AddComponent<LayoutElement>();
                _areaLe.flexibleHeight = 1f;
            }
            var hl = _bar.AddComponent<HorizontalLayoutGroup>();
            hl.spacing = gap;
            hl.childAlignment = TextAnchor.UpperLeft;
            hl.childControlWidth = hl.childControlHeight = false;
            hl.childForceExpandWidth = hl.childForceExpandHeight = false;

            // Players ticks any number of counts (a 2v2 on a 4-spawn map
            // wants 4 too), so its items are rewired in SetPlayerOptions.
            _playersDd = CloneDropdown(observers, "Players", null);
            _sizeDd = CloneDropdown(observers, "Size", i =>
            {
                _size = i > 0 && i <= _sizeValues.Length ? _sizeValues[i - 1] : 0;
                Apply();
            });

            // The Maps header's Filters button, which the game hides: the
            // same chip, as the custom maps switch.
            // Front / MainContent / ListPanel.
            var listPanel = front.parent != null ? front.parent.parent : null;
            var chip = listPanel != null ? listPanel.Find("Header/Actions/Filters") : null;
            if (chip != null && _stock != null)
            {
                var go = Object.Instantiate(chip.gameObject, _bar.transform, false);
                go.name = "Custom";
                ((RectTransform)go.transform).sizeDelta = new Vector2(((RectTransform)go.transform).sizeDelta.x, height);
                go.SetActive(true);
                _customBtn = go.GetComponent<ButtonManager>();
                _customBtn.onClick.RemoveAllListeners();
                for (var i = 0; i < _customBtn.onClick.GetPersistentEventCount(); i++)
                    _customBtn.onClick.SetPersistentListenerState(i, UnityEngine.Events.UnityEventCallState.Off);
                _customBtn.useLocalization = false;
                _customBtn.Interactable(true);
                _customBtn.onClick.AddListener(() =>
                {
                    _cfgShowCustom.Value = !_cfgShowCustom.Value;
                    Apply();
                });
            }
        }

        private static Michsky.UI.Beam.Dropdown CloneDropdown(Michsky.UI.Beam.Dropdown source, string name, Action<int> picked)
        {
            // Cloned under an inactive holder and set up before it wakes: a
            // clone's Awake would add a second Canvas to the item list, which
            // the source's own Awake already did (and the clone copied).
            var holder = new GameObject("SanctuaryHud Dropdown Holder");
            holder.SetActive(false);
            try
            {
                var dd = Object.Instantiate(source.gameObject, holder.transform, false).GetComponent<Michsky.UI.Beam.Dropdown>();
                dd.name = name;
                dd.setHighPriority = false;
                dd.saveSelected = false;
                dd.invokeOnEnable = false;
                // The header is ours to write (a summary, for players), and
                // carries no item icon.
                dd.updateOnEnable = false;
                dd.enableIcon = false;
                dd.items = new List<Michsky.UI.Beam.Dropdown.Item> { new Michsky.UI.Beam.Dropdown.Item { itemName = "Any" } };
                dd.selectedItemIndex = 0;
                dd.onValueChanged = new Michsky.UI.Beam.Dropdown.DropdownEvent();
                if (picked != null) dd.onValueChanged.AddListener(i => picked(i));
                var rt = (RectTransform)dd.transform;
                rt.sizeDelta = new Vector2(260f, rt.sizeDelta.y);
                rt.SetParent(_bar.transform, false);
                dd.gameObject.SetActive(true);
                return dd;
            }
            finally { Object.DestroyImmediate(holder); }
        }

        // The dropdowns offer only the values some installed map has.
        private static void SetOptions()
        {
            var players = _rows.Select(r => r.Info.Players).Where(n => n > 0).Distinct().OrderBy(n => n).ToArray();
            var sizes = _rows.Select(r => r.Info.Size).Where(n => n > 0).Distinct().OrderBy(n => n).ToArray();
            SetPlayerOptions(players);
            SetOptions(_sizeDd, ref _sizeValues, sizes, ref _size, "Any size", n => n.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        private static void SetPlayerOptions(int[] values)
        {
            var dd = _playersDd;
            if (dd == null) return;
            _players.IntersectWith(values);
            if (!_playerValues.SequenceEqual(values) || dd.items.Count != values.Length + 1)
            {
                _playerValues = values;
                dd.items.Clear();
                dd.items.Add(new Michsky.UI.Beam.Dropdown.Item { itemName = "Any players", itemIcon = HudStyle.Icon("box") });
                foreach (var v in values)
                    dd.items.Add(new Michsky.UI.Beam.Dropdown.Item { itemName = v + " players", itemIcon = HudStyle.Icon("box") });
                dd.selectedItemIndex = 0;
                dd.Initialize();
                // A tick leaves the list open for the next one; only "Any"
                // closes it. Initialize wired each item to pick and close.
                for (var i = 0; i < dd.items.Count; i++)
                {
                    var index = i;
                    var button = dd.items[i].itemButton;
                    if (button == null) continue;
                    button.onClick.RemoveAllListeners();
                    button.onClick.AddListener(() => TogglePlayers(index));
                }
            }
            ShowPlayerPicks();
        }

        private static void TogglePlayers(int index)
        {
            if (index <= 0 || index > _playerValues.Length)
            {
                _players.Clear();
                if (_playersDd != null && _playersDd.isOn) _playersDd.Animate();
            }
            else if (!_players.Remove(_playerValues[index - 1]))
                _players.Add(_playerValues[index - 1]);
            ShowPlayerPicks();
            Apply();
        }

        /// Ticks on the items, and the header as a summary: "2-4, 8 players".
        private static void ShowPlayerPicks()
        {
            var dd = _playersDd;
            if (dd == null) return;
            for (var i = 0; i < dd.items.Count; i++)
            {
                var button = dd.items[i].itemButton;
                if (button == null) continue;
                var ticked = i == 0 ? _players.Count == 0 : _players.Contains(_playerValues[i - 1]);
                var icon = HudStyle.Icon(ticked ? "box-ticked" : "box");
                if (button.buttonIcon == icon) continue;
                button.buttonIcon = icon;
                if (button.gameObject.activeInHierarchy) button.UpdateUI();
            }
            if (dd.headerText != null) dd.headerText.text = PlayersSummary();
        }

        private static string PlayersSummary()
        {
            if (_players.Count == 0) return "Any players";
            var picked = _players.OrderBy(n => n).ToList();
            var parts = new List<string>();
            for (var i = 0; i < picked.Count;)
            {
                // A run of counts that are all on offer, with none missing
                // between them, reads as a range.
                var j = i;
                while (j + 1 < picked.Count && Array.IndexOf(_playerValues, picked[j + 1]) == Array.IndexOf(_playerValues, picked[j]) + 1) j++;
                parts.Add(j - i >= 2 ? $"{picked[i]}-{picked[j]}" : string.Join(", ", picked.Skip(i).Take(j - i + 1)));
                i = j + 1;
            }
            return string.Join(", ", parts) + " players";
        }

        private static void SetOptions(Michsky.UI.Beam.Dropdown dd, ref int[] current, int[] values, ref int pick, string any, Func<int, string> label)
        {
            if (dd == null) return;
            var pickNow = pick;
            if (!values.Contains(pickNow)) pick = 0;
            if (!current.SequenceEqual(values) || dd.items.Count != values.Length + 1)
            {
                current = values;
                dd.items.Clear();
                dd.items.Add(new Michsky.UI.Beam.Dropdown.Item { itemName = any });
                foreach (var v in values) dd.items.Add(new Michsky.UI.Beam.Dropdown.Item { itemName = label(v) });
                dd.selectedItemIndex = 0;
                dd.Initialize();
            }
            var index = pick == 0 ? 0 : Array.IndexOf(values, pick) + 1;
            if (dd.selectedItemIndex != index || dd.headerText != null && dd.headerText.text != dd.items[index].itemName)
            {
                dd.selectedItemIndex = index;
                if (dd.headerText != null) dd.headerText.text = dd.items[index].itemName;
            }
        }

        private static void Apply()
        {
            var showCustom = _stock == null || _cfgShowCustom.Value;
            foreach (var row in _rows)
            {
                if (row.Go == null) continue;
                var info = row.Info;
                var show = (_players.Count == 0 || _players.Contains(info.Players))
                    && (_size == 0 || info.Size == _size)
                    && (showCustom || !info.Custom);
                if (row.Go.activeSelf != show) row.Go.SetActive(show);
            }
            if (_customBtn != null)
            {
                _customBtn.buttonText = _cfgShowCustom.Value ? "Custom maps: on" : "Custom maps: off";
                if (_customBtn.gameObject.activeInHierarchy) _customBtn.UpdateUI();
            }
            if (_list != null) _list.verticalNormalizedPosition = 1f;
        }

        // The row prefab carries a players column (from the lobby list's
        // rows) that the map list leaves hidden.
        private static void ShowDetails(GameObject row, MapInfo info)
        {
            var players = row.transform.Find("Content/Players");
            if (players == null || info.Players <= 0) return;
            var text = players.GetComponent<TMP_Text>();
            if (text == null) return;
            text.text = info.Size > 0 ? $"{info.Players}P  {info.Size}" : $"{info.Players}P";
            players.gameObject.SetActive(true);
            var sep = row.transform.Find("Content/Separator (3)");
            if (sep != null) sep.gameObject.SetActive(true);
        }

        /// From the plugin's OnDestroy, and when the filters are switched off:
        /// every row back, the list its full height, the bar gone.
        internal static void Shutdown()
        {
            foreach (var row in _rows)
                if (row.Go != null && !row.Go.activeSelf) row.Go.SetActive(true);
            if (_area != null) _area.sizeDelta = _areaSize;
            if (_vlg != null) _vlg.childForceExpandHeight = _vlgExpand;
            _vlg = null;
            if (_areaLe != null) Object.Destroy(_areaLe);
            if (_bar != null) Object.Destroy(_bar);
            _area = null;
            _areaLe = null;
            _bar = null;
            _list = null;
            _playersDd = _sizeDd = null;
            _customBtn = null;
            _playerValues = _sizeValues = Array.Empty<int>();
        }

        // ---- map facts ------------------------------------------------------

        private static readonly Regex ArmyMarker = new Regex(@"^army_\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // Data.DataPath (Application.dataPath underneath), taken on the main
        // thread for the prefetch.
        private static string _dataPath;
        private static readonly object _lock = new object();

        /// From Awake: reads every installed map's header on a worker thread,
        /// so the lobby screen doesn't stall half a second on first opening
        /// (188 maps measured at ~0.5 s).
        internal static void Prefetch()
        {
            if (Enabled == null || !Enabled.Value) return;
            _dataPath = Data.DataPath;
            var engine = Directory.GetParent(Application.dataPath)?.FullName;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    ReadStock(engine);
                    var sw = Stopwatch.StartNew();
                    var n = 0;
                    foreach (var file in Directory.EnumerateFiles(_dataPath + "Maps/", "*.sanmap", SearchOption.AllDirectories))
                    {
                        Info(file.Replace('\\', '/').Substring(_dataPath.Length));
                        n++;
                    }
                    _log.LogInfo($"Map list filters: read {n} maps in {sw.ElapsedMilliseconds} ms (in the background).");
                }
                catch (Exception e)
                {
                    _log.LogInfo($"Map list filters: background map read failed ({e.Message}); the list reads them when it opens.");
                }
            });
        }

        private static MapInfo Info(string mapPath)
        {
            lock (_lock) return InfoLocked(mapPath);
        }

        private static MapInfo InfoLocked(string mapPath)
        {
            var full = (_dataPath ?? Data.DataPath) + mapPath.Replace('\\', '/');
            var info = new MapInfo();
            try
            {
                var fi = new FileInfo(full);
                if (_infoCache.TryGetValue(full, out var hit) && hit.Item1 == fi.Length && hit.Item2 == fi.LastWriteTimeUtc)
                    info = hit.Item3;
                else
                {
                    Read(full, info);
                    _infoCache[full] = (fi.Length, fi.LastWriteTimeUtc, info);
                }
            }
            catch (Exception e)
            {
                _log.LogDebug($"Map list filters: couldn't read {mapPath}: {e.Message}");
            }
            info.Custom = _stock != null && !_stock.Contains(StockKey(mapPath));
            return info;
        }

        // A .sanmap is JSON with its props (megabytes on some maps) after the
        // markers, so the read stops as soon as it has what it needs.
        private static void Read(string full, MapInfo info)
        {
            float width = 0f, length = 0f, playable = 0f;
            bool haveAreas = false, haveMarkers = false;
            using (var reader = new JsonTextReader(new StreamReader(full)))
            {
                if (!reader.Read() || reader.TokenType != JsonToken.StartObject) return;
                while (reader.Read() && reader.TokenType == JsonToken.PropertyName)
                {
                    var name = (string)reader.Value;
                    if (!reader.Read()) break;
                    switch (name)
                    {
                        case "width": width = ToFloat(reader.Value); break;
                        case "length": length = ToFloat(reader.Value); break;
                        case "areas":
                            haveAreas = true;
                            if (JToken.ReadFrom(reader) is JObject areas && areas["PlayableArea"] is JObject pa)
                                playable = Math.Max(ToFloat((pa["width"] as JValue)?.Value), ToFloat((pa["height"] as JValue)?.Value));
                            break;
                        case "markers":
                            haveMarkers = true;
                            var armies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            CountArmies(JToken.ReadFrom(reader), armies);
                            info.Players = armies.Count;
                            break;
                        default: reader.Skip(); break;
                    }
                    if (haveAreas && haveMarkers && width > 0f && length > 0f) break;
                }
            }
            var size = playable > 0f ? playable : Math.Max(width, length);
            if (size > 0f) info.Size = (int)Math.Pow(2, Math.Round(Math.Log(size, 2)));
        }

        private static float ToFloat(object v)
        {
            try { return v == null ? 0f : Convert.ToSingle(v, System.Globalization.CultureInfo.InvariantCulture); }
            catch { return 0f; }
        }

        private static void CountArmies(JToken token, HashSet<string> armies)
        {
            if (token is JObject o)
            {
                foreach (var p in o.Properties())
                {
                    if (ArmyMarker.IsMatch(p.Name)) armies.Add(p.Name);
                    CountArmies(p.Value, armies);
                }
            }
            else if (token is JArray a)
            {
                foreach (var t in a) CountArmies(t, armies);
            }
        }

        // ---- which maps Steam installed ---------------------------------------

        private const string DefaultAppId = "4511930";
        private static readonly Regex ManifestMap = new Regex(@"Sanctuary_Data\\Maps\\([^\x00-\x1f]+?\.sanmap)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex InstalledDepot = new Regex(@"""(\d+)""\s*\{\s*""manifest""\s*""(\d+)""", RegexOptions.CultureInvariant);

        private static string StockKey(string mapPath)
        {
            var p = mapPath.Replace('\\', '/');
            if (p.StartsWith("Maps/", StringComparison.OrdinalIgnoreCase)) p = p.Substring(5);
            return p.ToLowerInvariant();
        }

        /// The maps in the installed build's depot manifests, from Steam's
        /// depotcache: the app manifest (.acf) beside the game names each
        /// installed depot's manifest id.
        private static void ReadStock(string enginePath)
        {
            lock (_lock) ReadStockLocked(enginePath);
        }

        private static void ReadStockLocked(string enginePath)
        {
            if (_stockRead || enginePath == null) return;
            _stockRead = true;
            try
            {
                // ...\steamapps\common\<game>\engine
                var engine = new DirectoryInfo(enginePath);
                var steamapps = engine.Parent?.Parent?.Parent;
                if (steamapps == null) return;
                var appIdFile = Path.Combine(engine.FullName, "steam_appid.txt");
                var appId = File.Exists(appIdFile) ? File.ReadAllText(appIdFile).Trim() : DefaultAppId;
                var acf = Path.Combine(steamapps.FullName, $"appmanifest_{appId}.acf");
                if (!File.Exists(acf))
                {
                    _log.LogInfo($"Map list filters: no {Path.GetFileName(acf)}, so custom maps can't be told apart.");
                    return;
                }
                var caches = new List<string> { Path.Combine(steamapps.Parent.FullName, "depotcache") };
                if (Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is string steam)
                    caches.Add(Path.Combine(steam, "depotcache"));

                var stock = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var found = 0;
                foreach (Match m in InstalledDepot.Matches(File.ReadAllText(acf)))
                {
                    var file = caches.Select(c => Path.Combine(c, $"{m.Groups[1].Value}_{m.Groups[2].Value}.manifest")).FirstOrDefault(File.Exists);
                    if (file == null) continue;
                    found++;
                    // Binary protobuf with the paths in plain text: one char
                    // per byte keeps them intact for the regex.
                    var bytes = File.ReadAllBytes(file);
                    var chars = new char[bytes.Length];
                    for (var i = 0; i < bytes.Length; i++) chars[i] = (char)bytes[i];
                    var text = new string(chars);
                    foreach (Match map in ManifestMap.Matches(text)) stock.Add(StockKey(map.Groups[1].Value));
                }
                if (found == 0 || stock.Count == 0)
                {
                    _log.LogInfo("Map list filters: Steam's depot manifests for the game weren't found, so custom maps can't be told apart.");
                    return;
                }
                _stock = stock;
                _log.LogInfo($"Map list filters: {stock.Count} maps came with the game (Steam depot manifest).");
            }
            catch (Exception e)
            {
                _log.LogInfo($"Map list filters: couldn't read Steam's depot manifests ({e.Message}), so custom maps can't be told apart.");
            }
        }
    }
}
