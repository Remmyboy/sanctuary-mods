using System;
using System.Collections.Generic;
using UnityEngine;

namespace SanctuaryHud
{
    // The HUD's own icons for the orders row, drawn into small textures once
    // at first use: plain white shapes on transparent, tinted at draw time.
    // The game's own order art can't be borrowed convincingly — the glyph is
    // a small part of its sprite and the colour comes from a glow shader —
    // so these are made to read at 24 pixels in the HUD's flat style.
    //
    // Each shape is a point test in a -1..1 square, sampled 3x3 per pixel
    // for soft edges.
    internal static class Glyphs
    {
        private const int Size = 64;
        private static readonly Dictionary<string, Texture2D> _cache = new Dictionary<string, Texture2D>();

        /// The texture for an order key (the icon's name past "icon_order_"),
        /// or null where there is no shape for it.
        internal static Texture2D Get(string key)
        {
            if (_cache.TryGetValue(key, out var cached)) return cached;
            var shape = ShapeFor(key);
            var texture = shape == null ? null : Rasterise(shape);
            _cache[key] = texture;
            return texture;
        }

        private static Func<float, float, bool> ShapeFor(string key)
        {
            switch (key)
            {
                case "stop": return Stop;
                case "pause": return Pause;
                case "repeat_build": return Repeat;
                case "shield": return Shield;
                case "spr": return Intel;
                case "alloys": return Production;
                case "move": return Move;
                case "attack": return Attack;
                case "attack_move": return AttackMove;
                case "patrol": return Patrol;
                case "assist": return Assist;
                case "repair": return Repair;
                case "harvest": return Harvest;
                case "capture": return Capture;
                default: return null;
            }
        }

        private static Texture2D Rasterise(Func<float, float, bool> inside)
        {
            var texture = new Texture2D(Size, Size, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            var pixels = new Color32[Size * Size];
            const int sub = 3;
            for (var py = 0; py < Size; py++)
            {
                for (var px = 0; px < Size; px++)
                {
                    var hits = 0;
                    for (var sy = 0; sy < sub; sy++)
                    {
                        for (var sx = 0; sx < sub; sx++)
                        {
                            var x = ((px + (sx + 0.5f) / sub) / Size) * 2f - 1f;
                            // Texture rows run bottom-up; shapes are written y-up.
                            var y = ((py + (sy + 0.5f) / sub) / Size) * 2f - 1f;
                            if (inside(x, y)) hits++;
                        }
                    }
                    var a = (byte)Mathf.RoundToInt(255f * hits / (sub * sub));
                    pixels[py * Size + px] = new Color32(255, 255, 255, a);
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return texture;
        }

        // ---- shapes -------------------------------------------------------------

        private static bool RoundedBox(float x, float y, float half, float radius)
        {
            var qx = Mathf.Abs(x) - (half - radius);
            var qy = Mathf.Abs(y) - (half - radius);
            if (qx <= 0f && qy <= 0f) return true;
            if (qx > 0f && qy > 0f) return qx * qx + qy * qy <= radius * radius;
            return qx <= radius && qy <= radius;
        }

        private static float Angle(float x, float y) => Mathf.Atan2(y, x) * Mathf.Rad2Deg;   // -180..180, 0 = right, 90 = up

        private static bool InSector(float x, float y, float from, float to)
        {
            var a = Angle(x, y);
            if (a < 0f) a += 360f;
            from = (from % 360f + 360f) % 360f;
            to = (to % 360f + 360f) % 360f;
            return from <= to ? a >= from && a <= to : a >= from || a <= to;
        }

        private static bool InTriangle(float x, float y, Vector2 a, Vector2 b, Vector2 c)
        {
            float Sign(Vector2 p1, Vector2 p2, Vector2 p3) => (p1.x - p3.x) * (p2.y - p3.y) - (p2.x - p3.x) * (p1.y - p3.y);
            var p = new Vector2(x, y);
            var d1 = Sign(p, a, b);
            var d2 = Sign(p, b, c);
            var d3 = Sign(p, c, a);
            var neg = d1 < 0f || d2 < 0f || d3 < 0f;
            var pos = d1 > 0f || d2 > 0f || d3 > 0f;
            return !(neg && pos);
        }

        private static bool InPolygon(float x, float y, Vector2[] poly)
        {
            var inside = false;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            {
                if (poly[i].y > y != poly[j].y > y &&
                    x < (poly[j].x - poly[i].x) * (y - poly[i].y) / (poly[j].y - poly[i].y) + poly[i].x)
                    inside = !inside;
            }
            return inside;
        }

        private static Vector2 Polar(float degrees, float radius) =>
            new Vector2(Mathf.Cos(degrees * Mathf.Deg2Rad) * radius, Mathf.Sin(degrees * Mathf.Deg2Rad) * radius);

        private static bool Stop(float x, float y) => RoundedBox(x, y, 0.5f, 0.1f);

        private static bool Pause(float x, float y) =>
            Mathf.Abs(y) <= 0.52f && Mathf.Abs(x) >= 0.14f && Mathf.Abs(x) <= 0.46f;

        /// A ring with a gap, an arrowhead at the gap's end.
        private static bool Repeat(float x, float y)
        {
            var r = Mathf.Sqrt(x * x + y * y);
            if (r >= 0.42f && r <= 0.62f && !InSector(x, y, 20f, 80f)) return true;
            return InTriangle(x, y, Polar(88f, 0.52f), Polar(58f, 0.24f), Polar(58f, 0.82f));
        }

        private static readonly Vector2[] ShieldOutline =
        {
            new Vector2(0f, 0.72f), new Vector2(0.6f, 0.48f), new Vector2(0.56f, -0.1f), new Vector2(0f, -0.72f),
            new Vector2(-0.56f, -0.1f), new Vector2(-0.6f, 0.48f),
        };

        private static bool Shield(float x, float y)
        {
            if (!InPolygon(x, y, ShieldOutline)) return false;
            // Hollow, with a solid lower half so it reads as a shield and not a badge.
            return !InPolygon(x / 0.68f, y / 0.68f, ShieldOutline) || y < -0.02f;
        }

        /// Outer ring, a wedge sweeping from the centre, a dot.
        private static bool Intel(float x, float y)
        {
            var r = Mathf.Sqrt(x * x + y * y);
            if (r >= 0.54f && r <= 0.66f) return true;
            if (r <= 0.1f) return true;
            return r <= 0.48f && InSector(x, y, 15f, 75f);
        }

        /// A gear: disc with eight teeth and a hole.
        private static bool Production(float x, float y)
        {
            var r = Mathf.Sqrt(x * x + y * y);
            if (r <= 0.22f) return false;
            if (r <= 0.46f) return true;
            var a = Angle(x, y);
            var tooth = ((a % 45f) + 45f) % 45f;
            return r <= 0.66f && tooth >= 9f && tooth <= 36f;
        }

        private static bool Arrow(float x, float y, float degrees, float length)
        {
            // Rotate so the arrow points right, then test a shaft and a head.
            var c = Mathf.Cos(-degrees * Mathf.Deg2Rad);
            var s = Mathf.Sin(-degrees * Mathf.Deg2Rad);
            var rx = x * c - y * s;
            var ry = x * s + y * c;
            if (rx >= -length && rx <= length - 0.34f && Mathf.Abs(ry) <= 0.1f) return true;
            return InTriangle(rx, ry, new Vector2(length, 0f), new Vector2(length - 0.4f, 0.32f), new Vector2(length - 0.4f, -0.32f));
        }

        private static bool Move(float x, float y) => Arrow(x, y, 45f, 0.66f);

        private static bool Crosshair(float x, float y, float radius)
        {
            var r = Mathf.Sqrt(x * x + y * y);
            if (r >= radius - 0.1f && r <= radius) return true;
            if (r <= 0.08f) return true;
            return (Mathf.Abs(x) <= 0.07f || Mathf.Abs(y) <= 0.07f) && r >= radius - 0.32f && r <= radius + 0.18f;
        }

        private static bool Attack(float x, float y) => Crosshair(x, y, 0.62f);

        private static bool AttackMove(float x, float y) =>
            Crosshair(x + 0.2f, y - 0.2f, 0.42f) || Arrow(x - 0.28f, y + 0.28f, -45f, 0.38f);

        private static bool Patrol(float x, float y) =>
            Arrow(x, y - 0.25f, 0f, 0.6f) || Arrow(x, y + 0.25f, 180f, 0.6f);

        private static bool Assist(float x, float y)
        {
            // A plus in a ring.
            var r = Mathf.Sqrt(x * x + y * y);
            if (r >= 0.54f && r <= 0.66f) return true;
            return (Mathf.Abs(x) <= 0.09f || Mathf.Abs(y) <= 0.09f) && r <= 0.36f;
        }

        private static bool Repair(float x, float y)
        {
            // A spanner: a diagonal bar with a jaw at one end.
            var rx = (x + y) / Mathf.Sqrt(2f);
            var ry = (y - x) / Mathf.Sqrt(2f);
            if (Mathf.Abs(ry) <= 0.12f && rx >= -0.7f && rx <= 0.3f) return true;
            var hx = rx - 0.45f;
            var r = Mathf.Sqrt(hx * hx + ry * ry);
            return r <= 0.32f && !(ry > 0.06f && hx > 0.05f);
        }

        private static bool Harvest(float x, float y)
        {
            // An arrow down into a tray.
            if (Arrow(x, y + 0.12f, -90f, 0.5f)) return true;
            return y <= -0.5f && y >= -0.66f && Mathf.Abs(x) <= 0.56f || Mathf.Abs(x) >= 0.44f && Mathf.Abs(x) <= 0.56f && y <= -0.28f && y >= -0.66f;
        }

        private static bool Capture(float x, float y)
        {
            // A flag on a pole.
            if (x >= -0.5f && x <= -0.38f && Mathf.Abs(y) <= 0.66f) return true;
            return InTriangle(x, y, new Vector2(-0.38f, 0.66f), new Vector2(0.56f, 0.3f), new Vector2(-0.38f, -0.06f));
        }
    }
}
