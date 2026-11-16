using System;

namespace BDArmory.UI
{
    public readonly struct ScopePoint
    {
        public readonly float x, y;
        public ScopePoint(float x, float y) { this.x = x; this.y = y; }
    }

    /// <summary>A rectangle in display pixels, y down (IMGUI's frame).</summary>
    public readonly struct ScopeRect
    {
        public readonly float x, y, w, h;
        public ScopeRect(float x, float y, float w, float h) { this.x = x; this.y = y; this.w = w; this.h = h; }
        public static ScopeRect Around(ScopePoint p, float w, float h) => new ScopeRect(p.x - w * 0.5f, p.y - h * 0.5f, w, h);
        public ScopePoint Centre => new ScopePoint(x + w * 0.5f, y + h * 0.5f);
        public ScopeRect Offset(float dx, float dy) => new ScopeRect(x + dx, y + dy, w, h);
    }

    public enum ScopeAlign { Left, Centre, Right }

    /// <summary>What the displays draw with: IMGUI in the game, an image library in bench/ScopePreview.</summary>
    public interface IScopeCanvas
    {
        /// <summary>An image stretched over rect, tinted, turned clockwise by rotationDeg about pivot.</summary>
        void Image(ScopeImage image, ScopeRect rect, Rgba tint, float rotationDeg, ScopePoint pivot);
        void Fill(ScopeRect rect, Rgba color, float rotationDeg, ScopePoint pivot);
        /// <summary>One line of text, vertically centred in rect.</summary>
        void Text(ScopeRect rect, string text, int fontSize, bool bold, Rgba color, ScopeAlign align);
    }

    public enum ContactKind { Raw, Track, Missile, Jammed }
    /// <summary>What the radar's identification (IFF) makes of a contact.</summary>
    public enum Iff { Hostile, Friend, Unknown }
    public enum EmitterState { Search, Lock, Launch }

    /// <summary>
    /// The radar/IRST display and the radar warning receiver, element by element: which symbol, colour and size each state gets, and
    /// where the legends sit. Drawing goes through IScopeCanvas, so the game and the offline preview (bench/ScopePreview) draw the
    /// same thing. Positions are in the display's own pixels; scale is the window's scale setting.
    /// </summary>
    public static class ScopeComposer
    {
        static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
        public static int FontSize(float basePx, float scale) => (int)Math.Round(Clamp(basePx * scale, basePx * 0.85f, basePx * 1.6f));
        /// <summary>The glass inside a display's bezel.</summary>
        public static float Inset(float displaySize) => Math.Max(6f, displaySize * 0.018f);

        static void Label(IScopeCanvas c, ScopeRect rect, string text, int size, bool bold, Rgba color, ScopeAlign align)
        {
            c.Text(rect.Offset(1f, 1f), text, size, bold, ScopePalette.Shadow.WithAlpha(ScopePalette.Shadow.a * color.a), align);
            c.Text(rect, text, size, bold, color, align);
        }

        static void Sprite(IScopeCanvas c, ScopeImage image, ScopePoint p, float w, float h, Rgba tint, float rotationDeg = 0f) =>
            c.Image(image, ScopeRect.Around(p, w, h), tint, rotationDeg, p);

        #region Radar and IRST
        public static void Background(IScopeCanvas c, ScopeImage background, ScopeRect display) => c.Image(background, display, Rgba.White, 0f, display.Centre);

        /// <summary>The omni display's sweep: the beam with its afterglow, or a plain line when the fill is off.</summary>
        public static void Sweep(IScopeCanvas c, ScopeRect display, float angleDeg, bool infrared, bool filled)
        {
            var centre = display.Centre;
            if (filled) c.Image(infrared ? ScopeImage.IRSweep : ScopeImage.RadarSweep, display, Rgba.White, angleDeg, centre);
            else c.Fill(new ScopeRect(centre.x - 0.75f, display.y + Inset(display.h), 1.5f, display.h * 0.5f - Inset(display.h)), (infrared ? ScopePalette.Infrared : ScopePalette.Phosphor).WithAlpha(0.6f), angleDeg, centre);
        }

        /// <summary>One edge of a sensor's field of view on the omni display.</summary>
        public static void FieldOfViewEdge(IScopeCanvas c, ScopeRect display, float angleDeg, bool infrared)
        {
            var centre = display.Centre;
            float inset = Inset(display.h);
            c.Fill(new ScopeRect(centre.x - 0.6f, display.y + inset, 1.2f, display.h * 0.5f - inset), (infrared ? ScopePalette.Infrared : ScopePalette.Phosphor).WithAlpha(0.45f), angleDeg, centre);
        }

        public static void OwnShip(IScopeCanvas c, ScopeRect display, float headingDeg, float scale) =>
            Sprite(c, ScopeImage.OwnShip, display.Centre, 24f * scale, 24f * scale, ScopePalette.Text.WithAlpha(0.95f), headingDeg);

        public static void Horizon(IScopeCanvas c, ScopeRect display, float rollDeg) => c.Image(ScopeImage.Horizon, display, Rgba.White, rollDeg, display.Centre);

        public static Rgba IffTint(Iff iff) => iff == Iff.Friend ? ScopePalette.Friend : iff == Iff.Unknown ? ScopePalette.Unknown : ScopePalette.Hostile;

        /// <summary>
        /// A radar contact, coloured and shaped by identification as on colour displays: a track is a diamond (hostile), a circle (friend)
        /// or a square (unknown) with a line along its heading; a bare return a brick (a ring for a friend); a missile a white dot; a
        /// jammer's false target a dim hollow brick. fade runs 1 (fresh) to 0 (gone). A brick is turned by headingDeg (the omni display
        /// lays it across its bearing). Returns the clickable area.
        /// </summary>
        public static ScopeRect Contact(IScopeCanvas c, ContactKind kind, Iff iff, ScopePoint p, float headingDeg, float fade, string altitude, float scale)
        {
            var tint = (kind == ContactKind.Missile ? ScopePalette.LockWhite.WithAlpha(0.9f) : kind == ContactKind.Jammed ? IffTint(iff).WithAlpha(0.5f) : IffTint(iff)).Fade(Clamp(fade, 0f, 1f));
            float s;
            switch (kind)
            {
                case ContactKind.Raw when iff == Iff.Friend:
                    Sprite(c, ScopeImage.Ring, p, 12f * scale, 12f * scale, tint);
                    s = 12f * scale;
                    break;
                case ContactKind.Raw:
                    Sprite(c, ScopeImage.Brick, p, 14f * scale, 7f * scale, tint, headingDeg);
                    s = 14f * scale;
                    break;
                case ContactKind.Jammed:
                    Sprite(c, ScopeImage.BrickHollow, p, 14f * scale, 7f * scale, tint, headingDeg);
                    return ScopeRect.Around(p, 14f * scale, 10f * scale);
                case ContactKind.Missile:
                    Sprite(c, ScopeImage.MissileDot, p, 10f * scale, 10f * scale, tint);
                    return ScopeRect.Around(p, 14f * scale, 14f * scale);
                default:
                    Sprite(c, iff == Iff.Friend ? ScopeImage.TrackFriendly : iff == Iff.Unknown ? ScopeImage.TrackUnknown : ScopeImage.TrackHostile, p, 26f * scale, 26f * scale, tint);
                    Sprite(c, ScopeImage.HeadingLine, p, 26f * scale, 26f * scale, tint, headingDeg);
                    s = 18f * scale;
                    break;
            }
            if (!string.IsNullOrEmpty(altitude)) Label(c, new ScopeRect(p.x + s * 0.5f + 2f, p.y - 1f, 40f, 12f * scale), altitude, FontSize(10f, scale), false, tint, ScopeAlign.Left);
            return ScopeRect.Around(p, s + 4f, s + 4f);
        }

        /// <summary>A locked target: the bugged one filled and boxed in brackets, other locks hollow; both show heading and altitude.</summary>
        public static ScopeRect Lock(IScopeCanvas c, ScopePoint p, float headingDeg, bool active, string altitude, float scale)
        {
            float s = 30f * scale;
            var tint = active ? ScopePalette.LockWhite : ScopePalette.LockWhite.WithAlpha(0.75f);
            Sprite(c, active ? ScopeImage.LockActive : ScopeImage.LockSecondary, p, s, s, tint, headingDeg);
            if (active) Sprite(c, ScopeImage.LockBox, p, s * 1.25f, s * 1.25f, tint.WithAlpha(0.9f));
            if (!string.IsNullOrEmpty(altitude)) Label(c, new ScopeRect(p.x + s * 0.6f + 2f, p.y - 1f, 40f, 12f * scale), altitude, FontSize(10f, scale), true, tint, ScopeAlign.Left);
            return ScopeRect.Around(p, s, s);
        }

        /// <summary>
        /// An infrared contact: without range (omni display) a strobe at the rim on its bearing, its width and brightness growing with
        /// the heat; with range a triangle at its position; a missile a dot.
        /// </summary>
        public static void InfraredContact(IScopeCanvas c, ScopeRect display, ScopePoint p, bool onRim, float bearingDeg, float magnitude, bool missile, float fade, float scale)
        {
            var tint = ScopePalette.Infrared.Fade(Clamp(fade, 0f, 1f));
            if (onRim)
            {
                var centre = display.Centre;
                float heat = Clamp(magnitude / 400f, 0f, 1f), inset = Inset(display.h);
                float w = (missile ? 7f : 9f + 9f * heat) * scale, len = (missile ? 18f : 30f) * scale;
                c.Image(ScopeImage.IrStrobe, new ScopeRect(centre.x - w * 0.5f, display.y + inset + 1f, w, len), (missile ? ScopePalette.LockWhite : tint).Fade(0.7f + 0.3f * heat), bearingDeg, centre);
            }
            else if (missile) Sprite(c, ScopeImage.MissileDot, p, 10f * scale, 10f * scale, tint);
            else Sprite(c, ScopeImage.IrTriangle, p, 19f * scale, 19f * scale, tint);
        }

        /// <summary>The cursor: two short bars either side of the point.</summary>
        public static void Cursor(IScopeCanvas c, ScopePoint p, float scale)
        {
            float h = 18f * scale, gap = 7f * scale, w = 2f;
            c.Fill(new ScopeRect(p.x - gap - w, p.y - h * 0.5f, w, h), ScopePalette.LockWhite, 0f, p);
            c.Fill(new ScopeRect(p.x + gap, p.y - h * 0.5f, w, h), ScopePalette.LockWhite, 0f, p);
        }

        /// <summary>Sector view: where the antenna points (a caret on the bottom edge) and the limits of its scan (bars).</summary>
        public static void ScanMarker(IScopeCanvas c, ScopeRect display, ScopePoint antenna, ScopePoint? left, ScopePoint? right, bool infrared, float scale)
        {
            var tint = infrared ? ScopePalette.Infrared : ScopePalette.Phosphor;
            float inset = Inset(display.h);
            Sprite(c, ScopeImage.Caret, new ScopePoint(antenna.x, display.y + display.h - inset - 6f * scale), 11f * scale, 11f * scale, tint);
            float barH = 14f * scale;
            if (left.HasValue) c.Fill(new ScopeRect(left.Value.x - 2f, left.Value.y - barH - inset, 2f, barH), tint.WithAlpha(0.85f), 0f, left.Value);
            if (right.HasValue) c.Fill(new ScopeRect(right.Value.x, right.Value.y - barH - inset, 2f, barH), tint.WithAlpha(0.85f), 0f, right.Value);
        }

        /// <summary>The range scale readout (top left) between its two buttons: the rects to put the buttons in.</summary>
        public static void RangeLayout(ScopeRect display, float scale, out ScopeRect minus, out ScopeRect label, out ScopeRect plus)
        {
            float inset = Inset(display.h) + 3f, b = 16f;
            minus = new ScopeRect(display.x + inset, display.y + inset, b, b);
            label = new ScopeRect(minus.x + b + 3f, minus.y - 2f, 46f * Math.Max(1f, scale), b + 4f);
            plus = new ScopeRect(label.x + label.w + 3f, minus.y, b, b);
        }

        public static void RangeReadout(IScopeCanvas c, ScopeRect label, float rangeKm, float scale) =>
            Label(c, label, $"{rangeKm:0} km", FontSize(12f, scale), true, ScopePalette.Phosphor, ScopeAlign.Centre);

        /// <summary>The mode legend across the top: SCAN, BORE or LOCK n/m, and what is slaved under it.</summary>
        public static void Mode(IScopeCanvas c, ScopeRect display, string mode, string second, float scale)
        {
            float inset = Inset(display.h) + 2f, line = 15f * scale;
            Label(c, new ScopeRect(display.x, display.y + inset, display.w, line), mode, FontSize(12f, scale), true, ScopePalette.Phosphor, ScopeAlign.Centre);
            if (!string.IsNullOrEmpty(second)) Label(c, new ScopeRect(display.x, display.y + inset + line, display.w, line), second, FontSize(10f, scale), true, ScopePalette.Amber, ScopeAlign.Centre);
        }

        /// <summary>Width of the strip between the scope and the buttons that holds the launch zone scale.</summary>
        public const float LaunchZoneStripWidth = 64f;
        /// <summary>Height of a row in the data panel under the buttons.</summary>
        const float PanelRow = 16f;

        /// <summary>The launch zone strip's glass, beside the scope and as tall as it.</summary>
        public static void LaunchZoneStrip(IScopeCanvas c, ScopeRect strip)
        {
            var centre = strip.Centre;
            c.Fill(strip, ScopePalette.BezelBottom, 0f, centre);
            var glass = new ScopeRect(strip.x + 3f, strip.y + 3f, strip.w - 6f, strip.h - 6f);
            c.Fill(glass, ScopePalette.GlassEdge, 0f, centre);
            var rim = ScopePalette.Phosphor.WithAlpha(0.45f);
            c.Fill(new ScopeRect(glass.x, glass.y, glass.w, 1f), rim, 0f, centre);
            c.Fill(new ScopeRect(glass.x, glass.y + glass.h - 1f, glass.w, 1f), rim, 0f, centre);
            c.Fill(new ScopeRect(glass.x, glass.y, 1f, glass.h), rim, 0f, centre);
            c.Fill(new ScopeRect(glass.x + glass.w - 1f, glass.y, 1f, glass.h), rim, 0f, centre);
        }

        /// <summary>
        /// The launch zone of the selected missile against the bugged target, in the strip beside the scope and on the scope's range scale
        /// (bottom is us, top the range scale's end): maximum range, the no-escape zone as a thick bar down to minimum range, each tick
        /// labelled, and a caret with the target's range (level with the target's symbol when targetY is given, as on a sector display
        /// whose range scale needn't be linear).
        /// </summary>
        public static void LaunchZone(IScopeCanvas c, ScopeRect strip, ScopeRect display, float rangeScale, float maxRange, float noEscapeRange, float minRange, float targetRange, float? targetY, float scale)
        {
            scale = Clamp(scale, 0.85f, 1.2f); // The strip has a fixed width.
            float inset = Inset(display.h), w = 12f * scale;
            float top = display.y + inset + 2f, bottom = display.y + display.h - inset - 2f, x = strip.x + 7f;
            float Y(float range) => Clamp(bottom - range / rangeScale * (bottom - top), top, bottom);
            var line = ScopePalette.Phosphor;
            var pivot = strip.Centre;
            float yMax = Y(maxRange), yNe = Y(noEscapeRange), yMin = Y(minRange);
            c.Fill(new ScopeRect(x, yMax, 1.5f, bottom - yMax), line.WithAlpha(0.8f), 0f, pivot);
            c.Fill(new ScopeRect(x, yMax - 1f, w, 2f), line, 0f, pivot);
            c.Fill(new ScopeRect(x, yNe, 5f, Math.Max(2f, yMin - yNe)), line.WithAlpha(0.7f), 0f, pivot);
            c.Fill(new ScopeRect(x, yNe - 1f, w * 0.6f, 2f), line.WithAlpha(0.8f), 0f, pivot);
            c.Fill(new ScopeRect(x, yMin - 1f, w, 2f), line, 0f, pivot);
            // The target's caret points at the scale from the right, its range beside it.
            float yT = targetY.HasValue ? Clamp(targetY.Value, top, bottom) : Y(targetRange), s = 10f * scale;
            var caret = new ScopePoint(x + w + s * 0.5f, yT);
            Sprite(c, ScopeImage.Caret, caret, s, s, ScopePalette.LockWhite, -90f);
            Label(c, new ScopeRect(caret.x + s * 0.5f + 1f, yT - 7f, 40f, 14f), $"{targetRange / 1000f:0.0}", FontSize(10f, scale), true, ScopePalette.LockWhite, ScopeAlign.Left);
            // Tick labels right of the scale, top to bottom, kept clear of each other and of the caret's (one in the caret's way moves below it).
            float lineH = 11f * scale, caretBand = 7f + lineH * 0.5f;
            float next = top + lineH * 0.5f;
            int legendSize = FontSize(9.5f, scale);
            foreach (var (name, range, y) in new[] { ("MAX", maxRange, yMax), ("NE", noEscapeRange, yNe), ("MIN", minRange, yMin) })
            {
                float ly = Math.Max(y, next);
                if (Math.Abs(ly - yT) < caretBand) ly = yT + caretBand;
                if (ly > bottom - lineH * 0.5f) break;
                Label(c, new ScopeRect(x + w + 3f, ly - lineH * 0.5f, strip.x + strip.w - (x + w + 3f) - 4f, lineH), $"{name} {range / 1000f:0.#}", legendSize, false, line.WithAlpha(0.85f), ScopeAlign.Left);
                next = ly + lineH;
            }
        }

        /// <summary>A block of the data panel under the radar's buttons: a heading and rows of label and value. Returns the y below it.</summary>
        static float PanelBlock(IScopeCanvas c, ScopeRect panel, string heading, (string label, string value, Rgba color, bool bold)[] rows)
        {
            float h = PanelRow * (rows.Length + 1) + 6f;
            var pivot = panel.Centre;
            c.Fill(new ScopeRect(panel.x, panel.y, panel.w, h), ScopePalette.GlassEdge, 0f, pivot);
            c.Fill(new ScopeRect(panel.x, panel.y, 2f, h), ScopePalette.Phosphor.WithAlpha(0.6f), 0f, pivot);
            Label(c, new ScopeRect(panel.x + 8f, panel.y + 3f, panel.w - 14f, PanelRow), heading, 10, true, ScopePalette.Phosphor.WithAlpha(0.85f), ScopeAlign.Left);
            for (int i = 0; i < rows.Length; ++i)
            {
                var row = new ScopeRect(panel.x + 8f, panel.y + 3f + PanelRow * (i + 1), panel.w - 14f, PanelRow);
                if (!string.IsNullOrEmpty(rows[i].label)) Label(c, row, rows[i].label, 11, false, ScopePalette.TextDim, ScopeAlign.Left);
                Label(c, row, rows[i].value, 12, rows[i].bold, rows[i].color, string.IsNullOrEmpty(rows[i].label) ? ScopeAlign.Left : ScopeAlign.Right);
            }
            return panel.y + h + 6f;
        }

        /// <summary>
        /// The bugged target in the data panel: range, closure, altitude and, once the radar has recognised it (non-cooperative target
        /// recognition; null until then), its type. Returns the y below the block.
        /// </summary>
        public static float TargetData(IScopeCanvas c, ScopeRect panel, float rangeKm, float closure, float altitudeKm, string craftType)
        {
            var white = ScopePalette.LockWhite;
            return PanelBlock(c, panel, "TARGET", new[]
            {
                ("RNG", $"{rangeKm:0.0} km", white, true),
                ("VC", $"{closure:0} m/s", white, false),
                ("ALT", $"{altitudeKm:0.0} km", white, false),
                ("TYPE", string.IsNullOrEmpty(craftType) ? "-" : craftType, white, true),
            });
        }

        /// <summary>The selected missile in flight in the data panel: its name, time to impact and whether its seeker has gone active.</summary>
        public static float MissileData(IScopeCanvas c, ScopeRect panel, string name, string timeToImpact, string seeker)
        {
            var white = ScopePalette.LockWhite;
            var rows = string.IsNullOrEmpty(seeker)
                ? new[] { ((string)null, name, white, true), ("TTI", timeToImpact, white, false) }
                : new[] { ((string)null, name, white, true), ("TTI", timeToImpact, white, false), ("SEEKER", seeker, ScopePalette.Amber, true) };
            return PanelBlock(c, panel, "MISSILE", rows);
        }

        public static void NoData(IScopeCanvas c, ScopeRect display, float scale) =>
            Label(c, display, "NO DATA", FontSize(15f, scale), true, ScopePalette.Phosphor.WithAlpha(0.6f), ScopeAlign.Centre);
        #endregion

        #region Identification
        /// <summary>
        /// The warning receiver's threat library code for a fighter's radar, from the craft's type: the number in its designation (F-15Z2 →
        /// 15, Su-35S → 35, MiG-29M → 29, FA-18E → 18), else the letters before a long number (EF2000 → EF), else its initial and first
        /// consonant after it (Rafale → RF). Only the name's first word counts, so team and serial suffixes don't.
        /// </summary>
        public static string ThreatCode(string vesselName)
        {
            if (string.IsNullOrEmpty(vesselName)) return "U";
            string word = vesselName.Trim().Split(' ')[0];
            int u = word.IndexOf('_'); if (u > 0) word = word.Substring(0, u); // A spawned copy's "_1".
            int start = -1, end = -1;
            for (int i = 0; i < word.Length; ++i)
            {
                if (char.IsDigit(word[i])) { if (start < 0) start = i; end = i + 1; }
                else if (start >= 0) break;
            }
            if (start >= 0 && end - start <= 2) return word.Substring(start, end - start);
            string letters = start > 0 ? word.Substring(0, start) : word;
            var code = new System.Text.StringBuilder();
            foreach (char ch in letters)
            {
                if (!char.IsLetter(ch)) continue;
                if (code.Length == 0) code.Append(char.ToUpperInvariant(ch));
                else if (start > 0 || "aeiouAEIOU".IndexOf(ch) < 0) { code.Append(char.ToUpperInvariant(ch)); break; }
            }
            return code.Length > 0 ? code.ToString() : "U";
        }

        /// <summary>A craft's type as target recognition names it: its name without a spawned copy's "_n", serial numbers or its team's name.</summary>
        public static string CraftType(string vesselName, string teamName)
        {
            if (string.IsNullOrEmpty(vesselName)) return null;
            var words = new System.Collections.Generic.List<string>(vesselName.Trim().Split(' '));
            int u = words[words.Count - 1].LastIndexOf('_');
            if (u > 0 && int.TryParse(words[words.Count - 1].Substring(u + 1), out _)) words[words.Count - 1] = words[words.Count - 1].Substring(0, u);
            while (words.Count > 1 && (int.TryParse(words[words.Count - 1], out _) || (!string.IsNullOrEmpty(teamName) && string.Equals(words[words.Count - 1], teamName, StringComparison.OrdinalIgnoreCase))))
                words.RemoveAt(words.Count - 1);
            return string.Join(" ", words).ToUpperInvariant();
        }
        #endregion

        #region Radar warning receiver
        public const float RwrStatusHeight = 20f;

        /// <summary>A symbol's position moved out along its bearing until it clears the own-craft mark at the centre.</summary>
        public static ScopePoint ClearOfOwnShip(ScopeRect display, ScopePoint p, float scale)
        {
            var centre = display.Centre;
            float dx = p.x - centre.x, dy = p.y - centre.y, r = (float)Math.Sqrt(dx * dx + dy * dy);
            float min = display.w * 0.5f * 0.11f + 17f * scale; // The mark's half-size plus a symbol's.
            if (r >= min) return p;
            if (r < 0.5f) { dx = 0f; dy = -1f; r = 1f; } // Dead centre: put it ahead.
            return new ScopePoint(centre.x + dx / r * min, centre.y + dy / r * min);
        }

        public static Rgba EmitterTint(EmitterState state, bool friendly) =>
            friendly ? ScopePalette.Friendly : state == EmitterState.Launch ? ScopePalette.Red : state == EmitterState.Lock ? ScopePalette.Amber : ScopePalette.Phosphor;

        /// <summary>
        /// An emitter on the warning receiver: its type letter, a hat over it when it is airborne, a diamond round it while it holds a lock
        /// on us, and a flashing ring once it has launched.
        /// </summary>
        public static void Emitter(IScopeCanvas c, ScopePoint p, string letter, EmitterState state, bool friendly, bool airborne, bool blink, float scale)
        {
            var tint = EmitterTint(state, friendly);
            float s = 34f * scale;
            if (!friendly && state != EmitterState.Search) Sprite(c, ScopeImage.Diamond, p, s, s, tint);
            if (!friendly && state == EmitterState.Launch && blink) Sprite(c, ScopeImage.Ring, p, s * 1.4f, s * 1.4f, tint);
            // The letter sits a little low under its hat, so the pair stays centred on the bearing.
            float letterY = p.y + (airborne ? 3f : 1f) * scale;
            if (airborne) Sprite(c, ScopeImage.Hat, new ScopePoint(p.x, letterY - 10f * scale), 12f * scale, 12f * scale, tint);
            Label(c, ScopeRect.Around(new ScopePoint(p.x, letterY), s, 16f * scale), letter, FontSize(14f, scale), true, state == EmitterState.Search && !friendly ? tint.WithAlpha(0.9f) : tint, ScopeAlign.Centre);
        }

        /// <summary>A missile coming at us: a red arrowhead on its bearing pointing in at the centre, flashing.</summary>
        public static void Missile(IScopeCanvas c, ScopeRect display, ScopePoint p, bool blink, float scale)
        {
            var centre = display.Centre;
            float dx = centre.x - p.x, dy = centre.y - p.y;
            float angle = dx * dx + dy * dy > 1f ? (float)(Math.Atan2(dx, -dy) * 180.0 / Math.PI) : 0f;
            float s = 20f * scale;
            Sprite(c, ScopeImage.MissileArrow, p, s, s, ScopePalette.Red.WithAlpha(blink ? 1f : 0.5f), angle);
        }

        /// <summary>The strip under the scope: the top threat once it locks or launches, else the number of emitters heard.</summary>
        public static void RwrStatus(IScopeCanvas c, ScopeRect strip, string text, EmitterState state, float scale)
        {
            if (string.IsNullOrEmpty(text)) return;
            var color = state == EmitterState.Launch ? ScopePalette.Red : state == EmitterState.Lock ? ScopePalette.Amber : ScopePalette.TextDim;
            Label(c, strip, text, FontSize(11.5f, scale), state != EmitterState.Search, color, ScopeAlign.Centre);
        }
        #endregion
    }
}
