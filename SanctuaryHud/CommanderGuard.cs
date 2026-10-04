using System;
using BepInEx.Configuration;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using static SanctuaryHud.HudCore;

namespace SanctuaryHud
{
    // Delete (or Ctrl-Delete) with your commander in the selection asks
    // first: a popup to blow it up or cancel, instead of the commander going
    // at once. Pressing Delete again confirms; it cancels itself after a few
    // seconds. Any selection without the commander deletes as before.
    //
    // The game's Delete is the DestroySelectedUnits input action, Ctrl-Delete
    // DeleteSelectedUnits; each looks its function up on
    // client/simpleEvents.lua at the press, which sends the selected ids to
    // the host's TestUtilities channel. This swaps both functions on that
    // module for ones that, when the selection holds a commander, keep the
    // ids and raise a request the plugin polls for instead; confirming sends
    // the same order for the same ids. Client side only.
    internal static class CommanderGuard
    {
        internal static ConfigEntry<bool> Enabled;

        /// How long the popup waits before it cancels itself.
        private const float Timeout = 8f;

        private static readonly LuaHook Hook = new LuaHook("__SdbCmdrGuard", "commander delete guard", InstallChunk)
        {
            Installed = () => _lastAsk = GetLuaGlobal("__SdbCmdrGuardAsk"),
        };
        private static string _lastAsk;

        internal static void Bind(ConfigFile config)
        {
            Enabled = config.Bind("QoL", "ConfirmCommanderDelete", true,
                "Delete with your commander selected asks before blowing it up: a popup to confirm or cancel " +
                "(Delete again confirms). Other units delete at once as before.");
        }

        internal static void Shutdown()
        {
            Hook.Remove();
            Popup.Close();
        }

        internal static void Tick()
        {
            if (Hook.Live) Poll();
            Popup.Tick();

            Hook.Tick(Enabled != null && Enabled.Value);
            if (!Hook.Live && Popup.Open)
            {
                _lastAsk = null;
                Popup.Close();
            }
        }

        /// A request is a counter the wrapper bumps on each guarded press: a
        /// new value opens the popup, or confirms it when it is already up.
        private static void Poll()
        {
            var ask = GetLuaGlobal("__SdbCmdrGuardAsk");
            if (ask == null || ask == _lastAsk) return;
            _lastAsk = ask;
            if (Popup.Open) Confirm();
            else Popup.Show();
        }

        private static void Confirm()
        {
            Popup.Close();
            Hook.Call("__SdbCmdrGuard.Go()");
        }

        private static void Cancel()
        {
            Popup.Close();
            Hook.Call("__SdbCmdrGuard.ids = nil");
        }

        private const string InstallChunk = @"
do
  local SE = Import('client/simpleEvents.lua')
  local SS = Import('client/input/selectionSystem.lua')
  if type(SE.DestroySelectedUnits) ~= 'function' or type(SE.DeleteSelectedUnits) ~= 'function' then
    error('SanctuaryHud: no delete functions to guard')
  end
  local S = { orig = { Destroy = SE.DestroySelectedUnits, Delete = SE.DeleteSelectedUnits } }

  local function commanderSelected()
    for _, u in pairs(SS.GetSelectedEntities() or {}) do
      local tpId = u.tp and u.tp.general and u.tp.general.tpId
      if tpId and Tags.COMMAND[tpId] then return true end
    end
    return false
  end

  local function guard(kind)
    return function(...)
      local ok, held = pcall(commanderSelected)
      if not (ok and held) then return S.orig[kind](...) end
      S.kind = kind
      S.ids = SS.GetSelectedUnitsIds()
      __SdbCmdrGuardAsk = (__SdbCmdrGuardAsk or 0) + 1
    end
  end

  -- Confirmed: the order the game would have sent, for the ids as they were.
  S.Go = function()
    local ids = S.ids
    S.ids = nil
    if not ids or not ids[1] then return end
    SendToHost({ fn = S.kind == 'Delete' and 'DeleteUnitsByIds' or 'DestroyUnitsByIds', args = { ids } }, 'TestUtilities')
  end

  SE.DestroySelectedUnits = guard('Destroy')
  SE.DeleteSelectedUnits = guard('Delete')
  S.mine = { Destroy = SE.DestroySelectedUnits, Delete = SE.DeleteSelectedUnits }
  S.Remove = function()
    if SE.DestroySelectedUnits == S.mine.Destroy then SE.DestroySelectedUnits = S.orig.Destroy end
    if SE.DeleteSelectedUnits == S.mine.Delete then SE.DeleteSelectedUnits = S.orig.Delete end
    __SdbCmdrGuard = nil
  end
  __SdbCmdrGuard = S
  __SdbCmdrGuardAsk = __SdbCmdrGuardAsk or 0
end";

        // ---- the popup -------------------------------------------------------------

        private static class Popup
        {
            private static RectTransform _plate;
            private static TMP_Text _cancelLabel;
            private static float _openedAt = -1f;

            internal static bool Open => _openedAt >= 0f;

            internal static void Show()
            {
                var root = HudCanvas.Ensure();
                if (root == null) return;
                if (_plate == null) Build(root);
                _openedAt = Time.unscaledTime;
                _plate.SetAsLastSibling();
                _plate.gameObject.SetActive(true);
            }

            internal static void Close()
            {
                _openedAt = -1f;
                if (_plate != null && _plate.gameObject.activeSelf) _plate.gameObject.SetActive(false);
            }

            internal static void Tick()
            {
                if (!Open) return;
                if (_plate == null) { _openedAt = -1f; return; }
                var left = Timeout - (Time.unscaledTime - _openedAt);
                if (left <= 0f)
                {
                    Cancel();
                    return;
                }
                HudCanvas.SetText(_cancelLabel, "CANCEL (" + Mathf.CeilToInt(left) + ")");
                // Arriving: from 94% with a slight overshoot.
                var t = (Time.unscaledTime - _openedAt) / 0.25f;
                var size = t < 1f ? Mathf.LerpUnclamped(0.94f, 1f, HudStyle.Overshoot(t)) : 1f;
                _plate.localScale = new Vector3(size, size, 1f);
            }

            private static void Build(RectTransform root)
            {
                _plate = HudCanvas.Plate(root, "Commander delete");
                _plate.anchorMin = _plate.anchorMax = _plate.pivot = new Vector2(0.5f, 0.5f);
                _plate.anchoredPosition = new Vector2(0f, 120f);
                _plate.sizeDelta = new Vector2(720f, 250f);

                var title = HudCanvas.Text(_plate, "Title", 40f, Color.white, TextAlignmentOptions.Center, FontStyles.Bold);
                Place(title.rectTransform, new Vector2(0f, -34f), new Vector2(680f, 52f));
                HudCanvas.SetText(title, "Blow up your commander?");
                var body = HudCanvas.Text(_plate, "Body", 26f, SanctuaryHudPlugin.MutedText, TextAlignmentOptions.Center);
                Place(body.rectTransform, new Vector2(0f, -88f), new Vector2(680f, 36f));
                HudCanvas.SetText(body, "Losing it loses you the match.  Delete again to confirm.");

                var yes = Button(_plate, "BLOW IT UP", new Color(0.88f, 0.16f, 0.12f, 0.95f), Confirm, out _);
                Place(yes, new Vector2(-150f, -160f), new Vector2(270f, 64f));
                var no = Button(_plate, "CANCEL", new Color(1f, 1f, 1f, 0.1f), Cancel, out _cancelLabel);
                Place(no, new Vector2(150f, -160f), new Vector2(270f, 64f));
                _plate.gameObject.SetActive(false);
            }

            private static void Place(RectTransform rt, Vector2 at, Vector2 size)
            {
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
                rt.pivot = new Vector2(0.5f, 1f);
                rt.anchoredPosition = at;
                rt.sizeDelta = size;
            }

            private static RectTransform Button(Transform parent, string label, Color colour, Action click, out TMP_Text text)
            {
                var back = HudCanvas.Fill(parent, label, colour);
                back.sprite = HudStyle.Shade;
                back.type = Image.Type.Sliced;
                back.raycastTarget = true;
                back.gameObject.AddComponent<Clickable>().OnClick = click;
                HoverGlow.Add(back.gameObject);
                text = HudCanvas.Text(back.transform, "Label", 28f, Color.white, TextAlignmentOptions.Center, FontStyles.Bold);
                var rt = text.rectTransform;
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;
                HudCanvas.SetText(text, label);
                return back.rectTransform;
            }
        }

        private sealed class Clickable : MonoBehaviour, IPointerClickHandler
        {
            internal Action OnClick;

            public void OnPointerClick(PointerEventData eventData)
            {
                if (eventData.button == PointerEventData.InputButton.Left) OnClick?.Invoke();
            }
        }
    }
}
