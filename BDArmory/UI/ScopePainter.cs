using System;

namespace BDArmory.UI
{
    /// <summary>A straight-alpha colour, free of Unity so the scope art can also be drawn outside the game (bench/ScopePreview).</summary>
    public readonly struct Rgba
    {
        public readonly float r, g, b, a;
        public Rgba(float r, float g, float b, float a = 1f) { this.r = r; this.g = g; this.b = b; this.a = a; }
        public static Rgba Hex(uint rgb, float a = 1f) => new Rgba(((rgb >> 16) & 0xff) / 255f, ((rgb >> 8) & 0xff) / 255f, (rgb & 0xff) / 255f, a);
        public Rgba WithAlpha(float alpha) => new Rgba(r, g, b, alpha);
        public Rgba Fade(float factor) => new Rgba(r, g, b, a * factor);
        public static readonly Rgba Clear = new Rgba(0f, 0f, 0f, 0f);
        public static readonly Rgba White = new Rgba(1f, 1f, 1f, 1f);
        public static Rgba Lerp(Rgba x, Rgba y, float t) => new Rgba(x.r + (y.r - x.r) * t, x.g + (y.g - x.g) * t, x.b + (y.b - x.b) * t, x.a + (y.a - x.a) * t);
        /// <summary>c laid over under with the given coverage.</summary>
        public static Rgba Over(Rgba under, Rgba c, float coverage)
        {
            float a = c.a * coverage;
            if (a <= 0f) return under;
            float outA = a + under.a * (1f - a);
            if (outA <= 0f) return Clear;
            return new Rgba((c.r * a + under.r * under.a * (1f - a)) / outA, (c.g * a + under.g * under.a * (1f - a)) / outA, (c.b * a + under.b * under.a * (1f - a)) / outA, outA);
        }
    }

    /// <summary>
    /// The sensor displays' palette, after a fighter's multi-function displays: near-black glass in a gunmetal bezel, green phosphor
    /// for the scales and legends, radar contacts by identification (hostile red, friend green, unknown yellow), white for a locked
    /// target, amber for cautions (a lock on us), red for warnings (a launch, a missile), violet for infrared, cyan for friendly emitters.
    /// </summary>
    public static class ScopePalette
    {
        public static readonly Rgba GlassCentre = Rgba.Hex(0x0b1a15, 0.97f);
        public static readonly Rgba GlassEdge = Rgba.Hex(0x030907, 0.97f);
        public static readonly Rgba BezelTop = Rgba.Hex(0x2a3236);
        public static readonly Rgba BezelBottom = Rgba.Hex(0x0e1315);
        public static readonly Rgba Phosphor = Rgba.Hex(0x7cf5a4);
        public static readonly Rgba Friendly = Rgba.Hex(0x5ccbff);
        public static readonly Rgba LockWhite = Rgba.Hex(0xf2fff6);
        public static readonly Rgba Amber = Rgba.Hex(0xffb547);
        public static readonly Rgba Red = Rgba.Hex(0xff4a3d);
        public static readonly Rgba Infrared = Rgba.Hex(0xc98cff); // Violet: clear of the identification colours.
        // Identification on the radar (friend or foe), as on colour displays: hostile red, friend green, unknown yellow.
        public static readonly Rgba Hostile = Rgba.Hex(0xff5a4c);
        public static readonly Rgba Friend = Rgba.Hex(0x4dff7c);
        public static readonly Rgba Unknown = Rgba.Hex(0xffd84d);
        public static readonly Rgba Text = Rgba.Hex(0xd7e4e0);
        public static readonly Rgba TextDim = Rgba.Hex(0x8fa29d);
        public static readonly Rgba Shadow = new Rgba(0f, 0f, 0f, 0.85f);
        // Window skin.
        public static readonly Rgba PanelTop = Rgba.Hex(0x1d2427, 0.97f);
        public static readonly Rgba PanelBottom = Rgba.Hex(0x111618, 0.97f);
        public static readonly Rgba PanelRim = Rgba.Hex(0x3b4549);
        public static readonly Rgba PanelHighlight = Rgba.Hex(0x56636a);
        public static readonly Rgba ButtonTop = Rgba.Hex(0x2b3337);
        public static readonly Rgba ButtonBottom = Rgba.Hex(0x1a2023);
        public static readonly Rgba ButtonRim = Rgba.Hex(0x4a565c);
        public static readonly Rgba FieldFill = Rgba.Hex(0x080c0d, 0.96f);
        public static readonly Rgba Title = Rgba.Hex(0xc9d4d1);
    }

    /// <summary>Every image the sensor displays use; backgrounds are drawn at the size they are shown, symbols at a fixed size and tinted.</summary>
    public enum ScopeImage
    {
        // Backgrounds and overlays, drawn at display size.
        OmniScope, SectorScope, RwrScope, RadarSweep, IRSweep, Horizon,
        // Symbols: white, tinted when drawn.
        Brick, BrickHollow, TrackHostile, TrackUnknown, TrackFriendly, HeadingLine, LockActive, LockSecondary, LockBox, MissileDot, IrTriangle, IrStrobe,
        OwnShip, Caret, Diamond, Ring, MissileArrow, Hat,
        // Window skin pieces (9-sliced).
        Panel, Button, ButtonHover, ButtonActive, ButtonOn, Field,
    }

    /// <summary>
    /// Pixel art for the sensor displays, as functions over [-1, 1]² (y up) anti-aliased by distance to each shape. Unity-free: the game
    /// turns the rasters into textures (ScopeArt), bench/ScopePreview into preview images.
    /// </summary>
    public static class ScopePainter
    {
        public const int SymbolSize = 64, SkinSize = 32;

        public static bool IsSymbol(ScopeImage image) => image >= ScopeImage.Brick && image <= ScopeImage.Hat;
        public static bool IsSkin(ScopeImage image) => image >= ScopeImage.Panel;

        /// <summary>
        /// The texture size an image is drawn from: symbols and skin pieces at their fixed size, backgrounds at the size they are shown
        /// (in 16-pixel steps, so resizing a window makes a new one only now and then) to keep their hairlines sharp.
        /// </summary>
        public static int TextureSize(ScopeImage image, float shownPx)
        {
            if (IsSymbol(image)) return SymbolSize;
            if (IsSkin(image)) return SkinSize;
            int size = (int)Math.Round(shownPx / 16f) * 16;
            return size < 64 ? 64 : size > 1536 ? 1536 : size;
        }

        /// <summary>The image as pixels, row 0 at the bottom (Unity's order); size is the display size for backgrounds.</summary>
        public static Rgba[] Raster(ScopeImage image, int size)
        {
            if (IsSymbol(image)) size = SymbolSize;
            else if (IsSkin(image)) size = SkinSize;
            var pixels = new Rgba[size * size];
            float px = 2f / size;
            Func<float, float, float, Rgba> f = Pixel(image);
            for (int j = 0; j < size; ++j)
                for (int i = 0; i < size; ++i)
                    pixels[j * size + i] = f((i + 0.5f) * px - 1f, (j + 0.5f) * px - 1f, px);
            return pixels;
        }

        static Func<float, float, float, Rgba> Pixel(ScopeImage image) => image switch
        {
            ScopeImage.OmniScope => Omni,
            ScopeImage.SectorScope => Sector,
            ScopeImage.RwrScope => Rwr,
            ScopeImage.RadarSweep => (x, y, px) => Sweep(x, y, px, ScopePalette.Phosphor),
            ScopeImage.IRSweep => (x, y, px) => Sweep(x, y, px, ScopePalette.Infrared),
            ScopeImage.Horizon => Horizon,
            ScopeImage.Brick => (x, y, px) => White(Fill(RoundBox(x, y, 0.92f, 0.92f, 0.25f), px)),
            ScopeImage.BrickHollow => (x, y, px) => White(Stroke(Math.Abs(RoundBox(x, y, 0.8f, 0.8f, 0.2f)), 6f, px)),
            // Track shapes stay upright; their heading is a separate line (HeadingLine) turned on its own.
            ScopeImage.TrackHostile => (x, y, px) => White(Stroke(Math.Abs((Math.Abs(x) + Math.Abs(y) - 0.46f) * 0.7071f), 6f, px)),
            ScopeImage.TrackUnknown => (x, y, px) => White(Stroke(Math.Abs(Box(x, y, 0.34f, 0.34f)), 6f, px)),
            ScopeImage.TrackFriendly => (x, y, px) => White(Stroke(Math.Abs(Len(x, y) - 0.4f), 6f, px)),
            ScopeImage.HeadingLine => (x, y, px) => White(Stick(x, y, 0.46f, px, 6f)),
            ScopeImage.LockActive => (x, y, px) => White(Math.Max(Fill(Len(x, y) - 0.34f, px), Stick(x, y, 0.3f, px, 6f))),
            ScopeImage.LockSecondary => (x, y, px) => White(Math.Max(Stroke(Math.Abs(Len(x, y) - 0.32f), 5f, px), Stick(x, y, 0.32f, px))),
            ScopeImage.LockBox => LockBox,
            ScopeImage.MissileDot => (x, y, px) => White(Fill((Math.Abs(x) + Math.Abs(y) - 0.62f) * 0.7071f, px)),
            ScopeImage.IrTriangle => (x, y, px) => White(Stroke(Math.Abs(Triangle(x, y + 0.1f, 0.62f)), 5.5f, px)),
            ScopeImage.IrStrobe => (x, y, px) => White(Fill(Math.Max(Math.Abs(x) - 0.92f * (0.2f + 0.8f * (y + 1f) * 0.5f), Math.Abs(y) - 0.96f), px) * (0.25f + 0.75f * Clamp01((y + 1f) * 0.5f))),
            ScopeImage.OwnShip => (x, y, px) => White(Fill(Aircraft(x / 0.92f, y / 0.92f) * 0.92f, px)),
            ScopeImage.Caret => (x, y, px) => White(Fill(Polygon(x, y, caret), px)),
            ScopeImage.Diamond => (x, y, px) => White(Stroke(Math.Abs((Math.Abs(x) + Math.Abs(y) - 0.84f) * 0.7071f), 5f, px)),
            ScopeImage.Ring => (x, y, px) => White(Stroke(Math.Abs(Len(x, y) - 0.86f), 5f, px)),
            ScopeImage.MissileArrow => MissileArrow,
            ScopeImage.Hat => (x, y, px) => White(Stroke(Polyline(x, y, hat), 7f, px)),
            ScopeImage.Panel => (x, y, px) => Skin(x, y, px, 7f, ScopePalette.PanelTop, ScopePalette.PanelBottom, ScopePalette.PanelRim, ScopePalette.PanelHighlight),
            ScopeImage.Button => (x, y, px) => Skin(x, y, px, 4f, ScopePalette.ButtonTop, ScopePalette.ButtonBottom, ScopePalette.ButtonRim, Rgba.Hex(0x66737a)),
            ScopeImage.ButtonHover => (x, y, px) => Skin(x, y, px, 4f, Rgba.Hex(0x354046), Rgba.Hex(0x20282b), Rgba.Hex(0x6d8c86), Rgba.Hex(0x86a6a0)),
            ScopeImage.ButtonActive => (x, y, px) => Skin(x, y, px, 4f, Rgba.Hex(0x141a1c), Rgba.Hex(0x222b2e), Rgba.Hex(0x7cf5a4, 0.75f), Rgba.Hex(0x141a1c)),
            ScopeImage.ButtonOn => ButtonOn,
            ScopeImage.Field => (x, y, px) => Skin(x, y, px, 4f, ScopePalette.FieldFill, ScopePalette.FieldFill, Rgba.Hex(0x2f383c), Rgba.Hex(0x0c1011)),
            _ => (x, y, px) => Rgba.Clear,
        };

        #region Shapes
        static float Sqrt(float v) => (float)Math.Sqrt(v);
        static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
        static float Len(float x, float y) => Sqrt(x * x + y * y);
        /// <summary>Coverage of a filled shape at signed distance d (negative inside).</summary>
        static float Fill(float d, float px) => Clamp01(0.5f - d / px);
        /// <summary>Coverage of a line widthPx pixels wide at distance d.</summary>
        static float Stroke(float d, float widthPx, float px) => Clamp01(widthPx * 0.5f + 0.5f - d / px);
        static Rgba White(float coverage) => new Rgba(1f, 1f, 1f, coverage);
        static float Box(float x, float y, float hx, float hy)
        {
            float qx = Math.Abs(x) - hx, qy = Math.Abs(y) - hy;
            return Len(Math.Max(qx, 0f), Math.Max(qy, 0f)) + Math.Min(Math.Max(qx, qy), 0f);
        }
        static float RoundBox(float x, float y, float hx, float hy, float radius) => Box(x, y, hx - radius, hy - radius) - radius;
        /// <summary>An upward isosceles triangle of half-width h centred near the origin (signed distance, approximate).</summary>
        static float Triangle(float x, float y, float h)
        {
            const float k = 1.7320508f;
            x = Math.Abs(x) / h; y = y / h;
            float d = Math.Max(x * k * 0.5f + y * 0.5f - 0.5f, -y - 0.5f);
            return d * h;
        }
        /// <summary>A heading line from the symbol's edge (from) up to the sprite's top.</summary>
        static float Stick(float x, float y, float from, float px, float widthPx = 5f) => y < from - px ? 0f : Stroke(Math.Max(Math.Abs(x), y - 0.98f), widthPx, px);

        static float Segment(float x, float y, float ax, float ay, float bx, float by)
        {
            float ex = bx - ax, ey = by - ay, wx = x - ax, wy = y - ay;
            float t = Clamp01((wx * ex + wy * ey) / (ex * ex + ey * ey));
            return Len(wx - ex * t, wy - ey * t);
        }
        /// <summary>Distance to an open polyline through the points (x0, y0, x1, y1, ...).</summary>
        static float Polyline(float x, float y, float[] p)
        {
            float d = float.MaxValue;
            for (int i = 0; i + 3 < p.Length; i += 2) d = Math.Min(d, Segment(x, y, p[i], p[i + 1], p[i + 2], p[i + 3]));
            return d;
        }
        /// <summary>Signed distance to a closed polygon (x0, y0, x1, y1, ...), negative inside.</summary>
        static float Polygon(float x, float y, float[] p)
        {
            float d = float.MaxValue; bool inside = false;
            for (int i = 0, n = p.Length; i < n; i += 2)
            {
                int j = (i + 2) % n;
                float ax = p[i], ay = p[i + 1], bx = p[j], by = p[j + 1];
                d = Math.Min(d, Segment(x, y, ax, ay, bx, by));
                if ((ay > y) != (by > y) && x < (bx - ax) * (y - ay) / (by - ay) + ax) inside = !inside;
            }
            return inside ? -d : d;
        }
        static float[] Mirrored(params float[] half)
        {
            // Points down the right side from the top, mirrored back up the left (x0 = 0 on the axis at both ends).
            var all = new System.Collections.Generic.List<float>(half);
            for (int i = half.Length - 2; i >= 0; i -= 2) if (half[i] != 0f) { all.Add(-half[i]); all.Add(half[i + 1]); }
            return all.ToArray();
        }
        /// <summary>A fighter seen from above, nose up, filling [-1, 1].</summary>
        static readonly float[] aircraft = Mirrored(0f, 0.97f, 0.1f, 0.68f, 0.12f, 0.28f, 0.9f, -0.18f, 0.9f, -0.36f, 0.12f, -0.2f, 0.1f, -0.6f, 0.42f, -0.82f, 0.42f, -0.96f, 0f, -0.86f);
        static float Aircraft(float x, float y) => Polygon(x, y, aircraft);
        static readonly float[] arrow = { 0f, 0.92f, 0.62f, -0.72f, 0f, -0.36f, -0.62f, -0.72f };
        static readonly float[] hat = { -0.62f, -0.3f, 0f, 0.32f, 0.62f, -0.3f };
        static readonly float[] caret = { 0f, 0.62f, 0.62f, -0.5f, -0.62f, -0.5f };
        #endregion

        #region Backgrounds
        /// <summary>The round glass of a scope in its bezel: returns the glass colour, or the bezel's, and the rim radius.</summary>
        static Rgba RoundGlass(float x, float y, float px, out float r, out float rim, out bool glass)
        {
            r = Len(x, y);
            rim = 1f - 5f * px;
            glass = r < rim;
            if (r > 1f + px) return Rgba.Clear;
            Rgba c;
            if (!glass)
            {
                // Bezel: lit from above, with a fine highlight on its inner edge.
                c = Rgba.Lerp(ScopePalette.BezelBottom, ScopePalette.BezelTop, Clamp01(0.5f + 0.5f * y));
                c = Rgba.Over(c, ScopePalette.PanelHighlight.WithAlpha(0.6f * Clamp01(0.3f + 0.7f * y)), Stroke(Math.Abs(r - (1f - 0.8f * px)), 1f, px));
            }
            else c = Rgba.Lerp(ScopePalette.GlassCentre, ScopePalette.GlassEdge, Clamp01(r * r));
            float edge = Clamp01((1f - r) / px + 0.5f);
            return new Rgba(c.r, c.g, c.b, c.a * edge);
        }

        static float AngleDeg(float x, float y) => (float)(Math.Atan2(x, y) * 180.0 / Math.PI); // 0 up, clockwise positive.
        static float DeltaToStep(float angle, float step) => Math.Abs(angle - (float)Math.Round(angle / step) * step);
        static float Dash(float t, float dashPx, float gapPx, float px)
        {
            float period = (dashPx + gapPx) * px, phase = t - (float)Math.Floor(t / period) * period;
            return Clamp01(Math.Min(phase, dashPx * px - phase) / px + 0.5f);
        }

        static Rgba Omni(float x, float y, float px)
        {
            var c = RoundGlass(x, y, px, out float r, out float rim, out bool glass);
            if (!glass) return c;
            var line = ScopePalette.Phosphor;
            for (int k = 1; k <= 3; ++k) c = Rgba.Over(c, line.WithAlpha(0.17f), Stroke(Math.Abs(r - rim * k * 0.25f), 1f, px));
            if (r > 0.06f) c = Rgba.Over(c, line.WithAlpha(0.09f), Math.Max(Stroke(Math.Abs(x), 1f, px), Stroke(Math.Abs(y), 1f, px)));
            float angle = AngleDeg(x, y), rad = (float)(Math.PI / 180.0);
            float lenPx = DeltaToStep(angle, 30f) < 0.5f ? 13f : DeltaToStep(angle, 10f) < 0.5f ? 8f : 4.5f;
            float step = lenPx > 12f ? 30f : lenPx > 7f ? 10f : 5f;
            if (r > rim - (lenPx + 2f) * px && r < rim - 2f * px)
                c = Rgba.Over(c, line.WithAlpha(lenPx > 12f ? 0.85f : lenPx > 7f ? 0.55f : 0.3f), Stroke(DeltaToStep(angle, step) * rad * r, lenPx > 12f ? 1.6f : 1.1f, px));
            c = Rgba.Over(c, line.WithAlpha(0.1f), Stroke(Math.Abs(r - rim + 1f * px), 6f, px)); // Glow inside the rim.
            c = Rgba.Over(c, line.WithAlpha(0.8f), Stroke(Math.Abs(r - rim + 1f * px), 1.5f, px));
            return c;
        }

        static Rgba Sector(float x, float y, float px)
        {
            float corner = 9f * px, bezel = 5f * px;
            float d = RoundBox(x, y, 1f, 1f, corner);
            if (d > px) return Rgba.Clear;
            Rgba c;
            var line = ScopePalette.Phosphor;
            if (d > -bezel)
            {
                c = Rgba.Lerp(ScopePalette.BezelBottom, ScopePalette.BezelTop, Clamp01(0.5f + 0.5f * y));
                c = Rgba.Over(c, ScopePalette.PanelHighlight.WithAlpha(0.6f * Clamp01(0.3f + 0.7f * y)), Stroke(Math.Abs(d + 0.8f * px), 1f, px));
            }
            else
            {
                // Glass, a little lighter towards the bottom centre where own ship sits.
                float glow = Clamp01(Len(x * 0.8f, (y + 1f) * 0.6f));
                c = Rgba.Lerp(ScopePalette.GlassCentre, ScopePalette.GlassEdge, glow * glow);
                float inner = 1f - bezel - 2f * px; // Grid area.
                // Dashed quarter lines in range and azimuth.
                for (int k = -1; k <= 1; ++k)
                {
                    float q = k * 0.5f * inner;
                    c = Rgba.Over(c, line.WithAlpha(k == 0 ? 0.16f : 0.12f), Stroke(Math.Abs(x - q), 1f, px) * Dash(y + 1f, 5f, 6f, px));
                    c = Rgba.Over(c, line.WithAlpha(0.12f), Stroke(Math.Abs(y - q), 1f, px) * Dash(x + 1f, 5f, 6f, px));
                }
                // Range scale along the left edge and azimuth scale along the bottom: eighths, longer at quarters.
                float left = x + inner, bottom = y + inner;
                for (int k = 1; k < 8; ++k)
                {
                    float t = -inner + k * 0.25f * inner, lenPx = k % 2 == 0 ? 11f : 6f;
                    if (left >= 0f && left < lenPx * px) c = Rgba.Over(c, line.WithAlpha(0.75f), Stroke(Math.Abs(y - t), 1.4f, px));
                    if (bottom >= 0f && bottom < lenPx * px) c = Rgba.Over(c, line.WithAlpha(0.75f), Stroke(Math.Abs(x - t), 1.4f, px));
                }
                c = Rgba.Over(c, line.WithAlpha(0.1f), Stroke(Math.Abs(d + bezel + 1f * px), 6f, px));
                c = Rgba.Over(c, line.WithAlpha(0.75f), Stroke(Math.Abs(d + bezel + 1f * px), 1.5f, px));
            }
            return c.Fade(Clamp01(-d / px + 0.5f));
        }

        static Rgba Rwr(float x, float y, float px)
        {
            var c = RoundGlass(x, y, px, out float r, out float rim, out bool glass);
            if (!glass) return c;
            var line = ScopePalette.Phosphor;
            float angle = AngleDeg(x, y), rad = (float)(Math.PI / 180.0);
            c = Rgba.Over(c, line.WithAlpha(0.38f), Stroke(Math.Abs(r - 0.36f * rim), 1.3f, px)); // Lethal ring.
            c = Rgba.Over(c, line.WithAlpha(0.2f), Stroke(Math.Abs(r - 0.7f * rim), 1f, px) * Dash(angle * rad * r + 10f, 4f, 5f, px));
            bool cardinal = DeltaToStep(angle, 90f) < 0.5f;
            float lenPx = cardinal ? 12f : 7f;
            if (r > rim - (lenPx + 2f) * px && r < rim - 2f * px) c = Rgba.Over(c, line.WithAlpha(cardinal ? 0.85f : 0.6f), Stroke(DeltaToStep(angle, 30f) * rad * r, 1.5f, px));
            c = Rgba.Over(c, line.WithAlpha(0.1f), Stroke(Math.Abs(r - rim + 1f * px), 6f, px));
            c = Rgba.Over(c, line.WithAlpha(0.8f), Stroke(Math.Abs(r - rim + 1f * px), 1.5f, px));
            // Own aircraft at the centre.
            const float ship = 0.11f;
            c = Rgba.Over(c, ScopePalette.Text.WithAlpha(0.9f), Fill(Aircraft(x / ship, y / ship) * ship, px));
            return c;
        }

        static Rgba Sweep(float x, float y, float px, Rgba color)
        {
            float r = Len(x, y), rim = 1f - 5f * px;
            if (r > rim || y <= 0f && Math.Abs(x) > 0.5f) return Rgba.Clear;
            // The beam points up and turns clockwise, leaving a fading trail to its left.
            float angle = AngleDeg(x, y);
            float trail = angle <= 0f ? (float)Math.Exp(angle / 16f) * 0.42f : 0f;
            float beam = Stroke(Math.Abs(x) * (y > 0f ? 1f : 10f), 1.6f, px) * (y > 0f ? 1f : 0f);
            float a = Math.Max(trail, beam) * Clamp01(r / (12f * px)) * Clamp01((rim - r) / px + 0.5f);
            return color.WithAlpha(Clamp01(a));
        }

        static Rgba Horizon(float x, float y, float px)
        {
            // The horizon bar, turned with the craft's roll: two arms with a gap at the centre and short ticks down at their ends.
            float ax = Math.Abs(x);
            float arm = ax > 0.09f && ax < 0.56f ? Stroke(Math.Abs(y), 1.6f, px) : 0f;
            float tick = y < 0f && y > -9f * px ? Stroke(Math.Abs(ax - 0.56f), 1.6f, px) : 0f;
            float centre = Stroke(Math.Abs(Len(x, y) - 4f * px), 1.4f, px);
            return ScopePalette.Phosphor.WithAlpha(0.7f * Math.Max(Math.Max(arm, tick), centre));
        }
        #endregion

        #region Symbols
        static Rgba LockBox(float x, float y, float px)
        {
            // Corner brackets round a locked target.
            float ax = Math.Abs(x), ay = Math.Abs(y);
            float d = Math.Abs(Box(x, y, 0.82f, 0.82f));
            bool nearCorner = ax > 0.4f && ay > 0.4f;
            return White(nearCorner ? Stroke(d, 6f, px) : 0f);
        }

        /// <summary>A filled arrowhead pointing up, with a notch in its base.</summary>
        static Rgba MissileArrow(float x, float y, float px) => White(Fill(Polygon(x, y, arrow), px));

        static Rgba ButtonOn(float x, float y, float px)
        {
            // A button that is on: as Button, with a lit bar along its bottom edge.
            var c = Skin(x, y, px, 4f, ScopePalette.ButtonTop, ScopePalette.ButtonBottom, ScopePalette.ButtonRim, Rgba.Hex(0x66737a));
            if (c.a <= 0f) return c;
            float bar = y < -1f + 6f * px && y > -1f + 3f * px && Math.Abs(x) < 1f - 6f * px ? 1f : 0f;
            return Rgba.Over(c, ScopePalette.Phosphor.WithAlpha(0.9f), bar);
        }

        /// <summary>A 9-slice skin piece: rounded box with a vertical gradient, a rim and a highlight along the top inside edge.</summary>
        static Rgba Skin(float x, float y, float px, float radiusPx, Rgba top, Rgba bottom, Rgba rim, Rgba highlight)
        {
            float d = RoundBox(x, y, 1f, 1f, radiusPx * px);
            if (d > px) return Rgba.Clear;
            var c = Rgba.Lerp(bottom, top, Clamp01(0.5f + 0.5f * y));
            c = Rgba.Over(c, highlight.WithAlpha(0.8f), y > 0f ? Stroke(Math.Abs(d + 1.5f * px), 1f, px) * Clamp01(y * 3f) : 0f);
            c = Rgba.Over(c, rim, Stroke(Math.Abs(d + 0.5f * px), 1f, px));
            return c.Fade(Clamp01(-d / px + 0.5f));
        }
        #endregion
    }
}
