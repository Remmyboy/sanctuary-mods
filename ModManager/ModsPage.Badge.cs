using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace SanctuaryHud
{
    // A red count on the side bar's Mods icon: how many mods have a newer
    // release. The check runs as soon as the menu is up (and again every
    // half hour while it is), so the count is there from launch, without
    // the page ever being opened.
    internal sealed partial class ModsPage
    {
        private GameObject _badge;
        private TMP_Text _badgeText;
        private Sprite _dot;
        private int _badgeCount;
        private int _badgeChangesSeen = -1;
        private float _badgeCountedAt = -10f;

        /// Every frame from Tick, once the page and side bar are built.
        private void TickBadge()
        {
            if (_sidebarButton == null || !_sidebarButton.activeInHierarchy) return;
            var u = _owner.Updates;
            if (_owner.CheckUpdatesAutomatically) u.CheckIfStale();

            // The count moves when a check or an install finishes, and when a
            // mod reloads at another version; the second is caught by
            // counting again every couple of seconds.
            if (u.Changes == _badgeChangesSeen && Time.unscaledTime - _badgeCountedAt < 2f && _badge != null) return;
            _badgeChangesSeen = u.Changes;
            _badgeCountedAt = Time.unscaledTime;
            var n = u.CheckResult == "Checked" ? u.AvailableFolders(_owner.InstalledVersion).Count : 0;
            if (_badge == null || _badge.transform.parent != _sidebarButton.transform) BuildBadge();
            if (_badge == null) return;
            if (n == _badgeCount && _badge.activeSelf == n > 0) return;
            _badgeCount = n;
            _badgeText.text = n > 9 ? "9+" : n.ToString();
            _badge.SetActive(n > 0);
            if (n > 0) PlaceBadge();
        }

        /// A red disc with a white number, on the button itself rather than
        /// on one of its state images, which fade out and in on hover.
        private void BuildBadge()
        {
            if (_dot == null) _dot = MakeDot();
            var icon = SidebarIcon();
            var label = _sidebarButton.GetComponentsInChildren<TMP_Text>(true).FirstOrDefault();
            if (icon == null || label == null) return;

            _badge = new GameObject("Update Badge", typeof(RectTransform), typeof(Image));
            var rt = (RectTransform)_badge.transform;
            rt.SetParent(_sidebarButton.transform, false);
            rt.SetAsLastSibling();
            var image = _badge.GetComponent<Image>();
            image.sprite = _dot;
            image.raycastTarget = false; // clicks go to the button under it

            var text = new GameObject("Count", typeof(RectTransform), typeof(TextMeshProUGUI));
            var trt = (RectTransform)text.transform;
            trt.SetParent(rt, false);
            trt.anchorMin = Vector2.zero;
            trt.anchorMax = Vector2.one;
            trt.offsetMin = trt.offsetMax = Vector2.zero;
            _badgeText = text.GetComponent<TextMeshProUGUI>();
            _badgeText.font = label.font;
            _badgeText.fontStyle = FontStyles.Bold;
            _badgeText.alignment = TextAlignmentOptions.Center;
            _badgeText.color = Color.white;
            _badgeText.enableAutoSizing = true;
            _badgeText.fontSizeMin = 6f;
            _badgeText.fontSizeMax = 200f;
            _badgeText.margin = Vector4.zero;
            _badgeText.raycastTarget = false;
            _badgeCount = -1;
            _badge.SetActive(false);
        }

        /// On the icon's top-right corner, half the icon's height across,
        /// wherever the side bar laid the icon out.
        private void PlaceBadge()
        {
            var icon = SidebarIcon();
            if (icon == null) return;
            Canvas.ForceUpdateCanvases();
            var button = (RectTransform)_sidebarButton.transform;
            var corners = new Vector3[4];
            icon.GetWorldCorners(corners); // 2 is top-right
            var size = icon.rect.height * icon.lossyScale.y / button.lossyScale.y * 0.55f;
            var rt = (RectTransform)_badge.transform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(size, size);
            var corner = (Vector2)button.InverseTransformPoint(corners[2]);
            // localPosition is from the button's pivot; anchoredPosition from
            // its centre anchor.
            rt.anchoredPosition = corner - Vector2.Scale(button.rect.size, new Vector2(0.5f, 0.5f) - button.pivot) - new Vector2(size * 0.15f, size * 0.15f);
            _badgeText.fontSizeMax = size * 0.7f;
        }

        /// The Mods icon as drawn in the button's normal state.
        private RectTransform SidebarIcon()
        {
            Image found = null;
            foreach (var image in _sidebarButton.GetComponentsInChildren<Image>(true))
            {
                if (image.sprite != _icon) continue;
                var state = image.GetComponentInParent<CanvasGroup>(true);
                if (state != null && state.name == "Normal") return image.rectTransform;
                if (found == null) found = image;
            }
            return found != null ? found.rectTransform : null;
        }

        private void DestroyBadge()
        {
            if (_badge != null) Object.Destroy(_badge);
            if (_dot != null) { Object.Destroy(_dot.texture); Object.Destroy(_dot); }
            _badge = null;
            _dot = null;
        }

        /// A filled red circle with an anti-aliased edge.
        private static Sprite MakeDot()
        {
            const int size = 64;
            var ink = new Color32(0xE5, 0x39, 0x35, 255);
#pragma warning disable SMOD003 // destroyed in DestroyBadge
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "Mods update badge",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
            };
            var px = new Color32[size * size];
            var c = size / 2f;
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var d = new Vector2(x + 0.5f - c, y + 0.5f - c).magnitude;
                px[y * size + x] = new Color32(ink.r, ink.g, ink.b, (byte)(Mathf.Clamp01(c - 0.5f - d) * 255f));
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            var sprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
            sprite.name = tex.name;
            sprite.hideFlags = HideFlags.HideAndDontSave;
#pragma warning restore SMOD003
            return sprite;
        }
    }
}
