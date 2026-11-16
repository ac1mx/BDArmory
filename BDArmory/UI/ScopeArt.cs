using System.Collections.Generic;
using UnityEngine;

namespace BDArmory.UI
{
    /// <summary>
    /// The sensor displays' textures and BDArmory's window skin, from ScopePainter's pixel art. Backgrounds are made at the size they are
    /// shown (ScopePainter.TextureSize) on first use and kept, two sizes per image at most; symbols and skin pieces once.
    /// </summary>
    public static class ScopeArt
    {
        public static Color ToColor(Rgba c) => new Color(c.r, c.g, c.b, c.a);
        public static readonly Color Line = ToColor(ScopePalette.Phosphor);
        public static Color Phosphor(float alpha) => ToColor(ScopePalette.Phosphor.WithAlpha(alpha));

        static readonly Dictionary<(ScopeImage, int), Texture2D> textures = new Dictionary<(ScopeImage, int), Texture2D>();
        static readonly Dictionary<ScopeImage, List<int>> sizesMade = new Dictionary<ScopeImage, List<int>>();

        /// <summary>The image's texture for drawing at shownPx screen pixels.</summary>
        public static Texture2D Get(ScopeImage image, float shownPx)
        {
            int size = ScopePainter.TextureSize(image, shownPx);
            if (textures.TryGetValue((image, size), out var texture) && texture != null) return texture;
            if (!sizesMade.TryGetValue(image, out var sizes)) sizesMade[image] = sizes = new List<int>();
            while (sizes.Count >= 2) // A resized window: drop the oldest size.
            {
                if (textures.TryGetValue((image, sizes[0]), out var old) && old != null) Object.Destroy(old);
                textures.Remove((image, sizes[0]));
                sizes.RemoveAt(0);
            }
            var pixels = ScopePainter.Raster(image, size);
            var colors = new Color32[pixels.Length];
            for (int i = 0; i < pixels.Length; ++i) colors[i] = ToColor(pixels[i]);
            texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.DontUnloadUnusedAsset,
                name = $"BDArmory.{image}.{size}",
            };
            texture.SetPixels32(colors);
            texture.Apply(false, true);
            textures[(image, size)] = texture;
            sizes.Add(size);
            return texture;
        }

        public const int WindowBorder = 8, ButtonBorder = 6;

        /// <summary>
        /// BDArmory's window skin: KSP's own metrics and fonts on gunmetal panels with a fine rim, multi-function-display style buttons
        /// (a lit bar along the bottom when on) and dark fields. Toggles, sliders and scrollbars keep KSP's art (their images carry their
        /// own layout).
        /// </summary>
        public static GUISkin BuildSkin(GUISkin baseSkin)
        {
            var skin = Object.Instantiate(baseSkin);
            skin.name = "BDArmory";
            Texture2D panel = Get(ScopeImage.Panel, 0), button = Get(ScopeImage.Button, 0), hover = Get(ScopeImage.ButtonHover, 0),
                active = Get(ScopeImage.ButtonActive, 0), on = Get(ScopeImage.ButtonOn, 0), field = Get(ScopeImage.Field, 0);
            Color text = ToColor(ScopePalette.Text), title = ToColor(ScopePalette.Title), lit = ToColor(ScopePalette.Phosphor);
            void Set(GUIStyle style, Texture2D normal, Texture2D hovered, Texture2D pressed, Texture2D toggled, int border)
            {
                style.normal.background = normal; style.hover.background = hovered; style.active.background = pressed; style.focused.background = hovered;
                style.onNormal.background = toggled; style.onHover.background = toggled; style.onActive.background = pressed; style.onFocused.background = toggled;
                style.border = new RectOffset(border, border, border, border);
                style.normal.textColor = style.focused.textColor = text;
                style.hover.textColor = style.active.textColor = Color.white;
                style.onNormal.textColor = style.onHover.textColor = style.onActive.textColor = style.onFocused.textColor = lit;
            }
            Set(skin.window, panel, panel, panel, panel, WindowBorder);
            skin.window.normal.textColor = skin.window.onNormal.textColor = title;
            skin.window.fontStyle = FontStyle.Bold;
            Set(skin.box, field, field, field, field, ButtonBorder);
            Set(skin.button, button, hover, active, on, ButtonBorder);
            Set(skin.textField, field, field, field, field, ButtonBorder);
            Set(skin.textArea, field, field, field, field, ButtonBorder);
            skin.label.normal.textColor = text;
            return skin;
        }
    }

    /// <summary>IScopeCanvas on IMGUI, for drawing inside a window's OnGUI.</summary>
    public sealed class ImguiScopeCanvas : IScopeCanvas
    {
        public static readonly ImguiScopeCanvas Instance = new ImguiScopeCanvas();
        readonly Dictionary<(int, bool, ScopeAlign), GUIStyle> styles = new Dictionary<(int, bool, ScopeAlign), GUIStyle>();

        static Rect R(ScopeRect r) => new Rect(r.x, r.y, r.w, r.h);

        static void Draw(Texture texture, ScopeRect rect, Rgba tint, float rotationDeg, ScopePoint pivot)
        {
            Matrix4x4 matrix = GUI.matrix;
            Color color = GUI.color;
            if (rotationDeg != 0f) GUIUtility.RotateAroundPivot(rotationDeg, matrix * new Vector4(pivot.x, pivot.y, 0f, 0f)); // Pivot scaled as the UI is (as elsewhere in BDArmory).
            GUI.color = new Color(color.r * tint.r, color.g * tint.g, color.b * tint.b, color.a * tint.a);
            GUI.DrawTexture(R(rect), texture, ScaleMode.StretchToFill, true);
            GUI.matrix = matrix;
            GUI.color = color;
        }

        public void Image(ScopeImage image, ScopeRect rect, Rgba tint, float rotationDeg, ScopePoint pivot) =>
            Draw(ScopeArt.Get(image, Mathf.Max(rect.w, rect.h) * GUI.matrix.m00), rect, tint, rotationDeg, pivot);

        public void Fill(ScopeRect rect, Rgba color, float rotationDeg, ScopePoint pivot) => Draw(Texture2D.whiteTexture, rect, color, rotationDeg, pivot);

        public void Text(ScopeRect rect, string text, int fontSize, bool bold, Rgba color, ScopeAlign align)
        {
            if (!styles.TryGetValue((fontSize, bold, align), out var style))
            {
                style = new GUIStyle(GUI.skin.label)
                {
                    fontSize = fontSize,
                    fontStyle = bold ? FontStyle.Bold : FontStyle.Normal,
                    alignment = align == ScopeAlign.Left ? TextAnchor.MiddleLeft : align == ScopeAlign.Right ? TextAnchor.MiddleRight : TextAnchor.MiddleCenter,
                    wordWrap = false,
                    clipping = TextClipping.Overflow,
                    padding = new RectOffset(0, 0, 0, 0),
                    margin = new RectOffset(0, 0, 0, 0),
                    richText = false,
                };
                styles[(fontSize, bold, align)] = style;
            }
            style.normal.textColor = ScopeArt.ToColor(color);
            GUI.Label(R(rect), text, style);
        }
    }
}
