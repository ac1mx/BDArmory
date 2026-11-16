using System;
using System.Collections.Generic;
using System.IO;
using BDArmory.UI;
using SkiaSharp;

// Offline preview of the radar/IRST display and the radar warning receiver: the same pixel art (ScopePainter) and element drawing
// (ScopeComposer) the game uses, drawn with SkiaSharp at the window sizes from the user's settings, over a sky and over a dark
// background. Window chrome and buttons imitate IMGUI's 9-sliced skin; text uses Calibri, KSP's IMGUI font.
// Usage: ScopePreview <out dir> [radar scale] [rwr scale]
static class Program
{
    static int Main(string[] args)
    {
        if (args.Length > 1 && args[0] == "legend") return Legend.Write(args[1]);
        string outDir = args.Length > 0 ? args[0] : "preview";
        float radarScale = args.Length > 1 ? float.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 1f;
        float rwrScale = args.Length > 2 ? float.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 1f;
        Directory.CreateDirectory(outDir);
        var scenes = new List<(string name, int w, int h, Action<SkiaCanvas> draw)>
        {
            ("radar_sector_scan", 0, 0, c => RadarScenes.SectorScan(c, radarScale)),
            ("radar_sector_lock", 0, 0, c => RadarScenes.SectorLock(c, radarScale)),
            ("radar_sector_lock_crowded", 0, 0, c => RadarScenes.SectorLockCrowded(c, radarScale)),
            ("radar_omni", 0, 0, c => RadarScenes.Omni(c, radarScale)),
            ("radar_irst_jammed", 0, 0, c => RadarScenes.IrstJammed(c, radarScale)),
            ("rwr_search", 0, 0, c => RwrScenes.Search(c, rwrScale)),
            ("rwr_threats", 0, 0, c => RwrScenes.Threats(c, rwrScale)),
        };
        var sheetParts = new List<SKImage>();
        foreach (var (name, _, _, draw) in scenes)
        {
            foreach (bool sky in new[] { true, false })
            {
                var size = Measure(draw);
                int w = (int)Math.Ceiling(size.w) + 40, h = (int)Math.Ceiling(size.h) + 40;
                using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
                Backdrop(surface.Canvas, w, h, sky);
                var canvas = new SkiaCanvas(surface.Canvas) { OriginX = 20, OriginY = 20 };
                draw(canvas);
                var image = surface.Snapshot();
                Save(image, Path.Combine(outDir, $"{name}_{(sky ? "sky" : "dark")}.png"));
                sheetParts.Add(image);
            }
        }
        // Contact sheet: one row per scene, sky and dark side by side.
        int sheetW = 0, sheetH = 0;
        for (int i = 0; i < sheetParts.Count; i += 2) { sheetW = Math.Max(sheetW, sheetParts[i].Width + sheetParts[i + 1].Width); sheetH += sheetParts[i].Height; }
        using (var sheet = SKSurface.Create(new SKImageInfo(sheetW, sheetH)))
        {
            sheet.Canvas.Clear(new SKColor(20, 22, 24));
            int y = 0;
            for (int i = 0; i < sheetParts.Count; i += 2)
            {
                sheet.Canvas.DrawImage(sheetParts[i], 0, y);
                sheet.Canvas.DrawImage(sheetParts[i + 1], sheetParts[i].Width, y);
                y += sheetParts[i].Height;
            }
            Save(sheet.Snapshot(), Path.Combine(outDir, "sheet.png"));
        }
        Console.WriteLine($"wrote {scenes.Count * 2} images and sheet.png to {Path.GetFullPath(outDir)}");
        return 0;
    }

    static (float w, float h) Measure(Action<SkiaCanvas> draw)
    {
        var probe = new SkiaCanvas(null);
        draw(probe);
        return (probe.WindowW, probe.WindowH);
    }

    static void Save(SKImage image, string path)
    {
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var f = File.Create(path);
        data.SaveTo(f);
    }

    /// <summary>A daylight sky over the sea, or a dark interior.</summary>
    static void Backdrop(SKCanvas c, int w, int h, bool sky)
    {
        using var paint = new SKPaint();
        if (sky)
        {
            paint.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, h),
                new[] { new SKColor(0x3d, 0x76, 0xb0), new SKColor(0x8f, 0xbf, 0xe3), new SKColor(0xc9, 0xdc, 0xe6), new SKColor(0x2c, 0x5a, 0x80) },
                new[] { 0f, 0.55f, 0.62f, 0.66f }, SKShaderTileMode.Clamp);
        }
        else paint.Color = new SKColor(0x12, 0x15, 0x18);
        c.DrawRect(0, 0, w, h, paint);
    }
}

/// <summary>One tile per symbol on the scope glass, for the pilot's reference card.</summary>
static class Legend
{
    const int Tile = 72;
    const float Scale = 1.7f;

    public static int Write(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var mid = new ScopePoint(Tile / 2f, Tile / 2f);
        var display = new ScopeRect(-Tile * 2.5f, -Tile * 2.5f, Tile * 6f, Tile * 6f); // Only for bearings: centre far below-left.
        var items = new (string name, Action<SkiaCanvas> draw)[]
        {
            ("hostile", c => ScopeComposer.Contact(c, ContactKind.Track, Iff.Hostile, mid, 35f, 1f, "6", Scale)),
            ("unknown", c => ScopeComposer.Contact(c, ContactKind.Track, Iff.Unknown, mid, 35f, 1f, "9", Scale)),
            ("friend", c => ScopeComposer.Contact(c, ContactKind.Track, Iff.Friend, mid, 35f, 1f, "4", Scale)),
            ("brick", c => ScopeComposer.Contact(c, ContactKind.Raw, Iff.Hostile, mid, 0f, 1f, null, Scale)),
            ("brick_unknown", c => ScopeComposer.Contact(c, ContactKind.Raw, Iff.Unknown, mid, 0f, 1f, null, Scale)),
            ("friend_return", c => ScopeComposer.Contact(c, ContactKind.Raw, Iff.Friend, mid, 0f, 1f, null, Scale)),
            ("missile", c => ScopeComposer.Contact(c, ContactKind.Missile, Iff.Hostile, mid, 0f, 1f, null, Scale)),
            ("jammed", c => ScopeComposer.Contact(c, ContactKind.Jammed, Iff.Hostile, mid, 0f, 1f, null, Scale)),
            ("lock_active", c => ScopeComposer.Lock(c, mid, 35f, true, "7", Scale * 0.8f)),
            ("lock_other", c => ScopeComposer.Lock(c, mid, 35f, false, "5", Scale * 0.8f)),
            ("ir_contact", c => ScopeComposer.InfraredContact(c, display, mid, false, 0f, 300f, false, 1f, Scale)),
            ("ir_strobe", c => c.Image(ScopeImage.IrStrobe, new ScopeRect(mid.x - 9f, 10f, 18f, 46f), ScopePalette.Infrared, 0f, mid)),
            ("cursor", c => ScopeComposer.Cursor(c, mid, Scale)),
            ("antenna", c => c.Image(ScopeImage.Caret, ScopeRect.Around(mid, 20f, 20f), ScopePalette.Phosphor, 0f, mid)),
            ("rwr_code", c => ScopeComposer.Emitter(c, mid, "35", EmitterState.Search, false, true, true, Scale)),
            ("rwr_unknown", c => ScopeComposer.Emitter(c, mid, "U", EmitterState.Search, false, true, true, Scale)),
            ("rwr_ground", c => ScopeComposer.Emitter(c, mid, "S", EmitterState.Search, false, false, true, Scale)),
            ("rwr_friendly", c => ScopeComposer.Emitter(c, mid, "15", EmitterState.Search, true, true, true, Scale)),
            ("rwr_lock", c => ScopeComposer.Emitter(c, mid, "29", EmitterState.Lock, false, true, true, Scale * 0.85f)),
            ("rwr_launch", c => ScopeComposer.Emitter(c, mid, "35", EmitterState.Launch, false, true, true, Scale * 0.7f)),
            ("rwr_missile", c => ScopeComposer.Missile(c, new ScopeRect(mid.x - 60f, mid.y + 20f, 120f, 120f), mid, true, Scale)),
        };
        foreach (var (name, draw) in items)
        {
            using var surface = SKSurface.Create(new SKImageInfo(Tile, Tile, SKColorType.Rgba8888, SKAlphaType.Premul));
            surface.Canvas.Clear(new SKColor(0x07, 0x11, 0x0e));
            draw(new SkiaCanvas(surface.Canvas));
            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var f = File.Create(Path.Combine(outDir, name + ".png"));
            data.SaveTo(f);
        }
        Console.WriteLine($"wrote {items.Length} legend tiles to {Path.GetFullPath(outDir)}");
        return 0;
    }
}

/// <summary>IScopeCanvas on SkiaSharp, plus the window chrome IMGUI would draw.</summary>
sealed class SkiaCanvas : IScopeCanvas
{
    readonly SKCanvas canvas; // Null while measuring.
    public float OriginX, OriginY; // Window position on the image.
    float groupX, groupY;          // GUI.BeginGroup offset inside the window.
    public float WindowW, WindowH;
    static readonly Dictionary<(ScopeImage, int), SKImage> images = new();
    static readonly SKTypeface regular = SKTypeface.FromFile(@"C:\Windows\Fonts\calibri.ttf");
    static readonly SKTypeface bold = SKTypeface.FromFile(@"C:\Windows\Fonts\calibrib.ttf");
    static readonly SKSamplingOptions linear = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);

    public SkiaCanvas(SKCanvas canvas) { this.canvas = canvas; }

    static SKImage Get(ScopeImage image, float w, float h)
    {
        int size = ScopePainter.TextureSize(image, Math.Max(w, h));
        if (images.TryGetValue((image, size), out var cached)) return cached;
        var pixels = ScopePainter.Raster(image, size);
        var bitmap = new SKBitmap(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        for (int j = 0; j < size; ++j)
            for (int i = 0; i < size; ++i)
            {
                var p = pixels[j * size + i];
                bitmap.SetPixel(i, size - 1 - j, new SKColor(B(p.r), B(p.g), B(p.b), B(p.a))); // Row 0 is the bottom.
            }
        var result = SKImage.FromBitmap(bitmap);
        images[(image, size)] = result;
        return result;
    }

    static byte B(float v) => (byte)Math.Round(Math.Clamp(v, 0f, 1f) * 255f);
    static SKColor C(Rgba c) => new SKColor(B(c.r), B(c.g), B(c.b), B(c.a));

    static SKPaint Tinted(Rgba tint) => new SKPaint
    {
        IsAntialias = true,
        ColorFilter = SKColorFilter.CreateColorMatrix(new float[] { tint.r, 0, 0, 0, 0, 0, tint.g, 0, 0, 0, 0, 0, tint.b, 0, 0, 0, 0, 0, tint.a, 0 }),
    };

    SKRect R(ScopeRect r) => new SKRect(OriginX + groupX + r.x, OriginY + groupY + r.y, OriginX + groupX + r.x + r.w, OriginY + groupY + r.y + r.h);

    public void BeginGroup(float x, float y) { groupX = x; groupY = y; }
    public void EndGroup() { groupX = groupY = 0f; }

    public void Image(ScopeImage image, ScopeRect rect, Rgba tint, float rotationDeg, ScopePoint pivot)
    {
        if (canvas == null) return;
        var img = Get(image, rect.w, rect.h);
        using var paint = Tinted(tint);
        canvas.Save();
        canvas.RotateDegrees(rotationDeg, OriginX + groupX + pivot.x, OriginY + groupY + pivot.y);
        canvas.DrawImage(img, new SKRect(0, 0, img.Width, img.Height), R(rect), linear, paint);
        canvas.Restore();
    }

    public void Fill(ScopeRect rect, Rgba color, float rotationDeg, ScopePoint pivot)
    {
        if (canvas == null) return;
        using var paint = new SKPaint { Color = C(color), IsAntialias = rotationDeg != 0f };
        canvas.Save();
        canvas.RotateDegrees(rotationDeg, OriginX + groupX + pivot.x, OriginY + groupY + pivot.y);
        canvas.DrawRect(R(rect), paint);
        canvas.Restore();
    }

    public void Text(ScopeRect rect, string text, int fontSize, bool isBold, Rgba color, ScopeAlign align)
    {
        if (canvas == null || string.IsNullOrEmpty(text)) return;
        using var font = new SKFont(isBold ? bold : regular, fontSize) { Edging = SKFontEdging.Antialias, Hinting = SKFontHinting.Normal };
        using var paint = new SKPaint { Color = C(color), IsAntialias = true };
        var r = R(rect);
        font.GetFontMetrics(out var m);
        float baseline = r.MidY - (m.Ascent + m.Descent) * 0.5f;
        float width = font.MeasureText(text);
        float x = align == ScopeAlign.Left ? r.Left : align == ScopeAlign.Right ? r.Right - width : r.MidX - width * 0.5f;
        canvas.DrawText(text, x, baseline, SKTextAlign.Left, font, paint);
    }

    void NineSlice(ScopeImage image, SKRect dst, int border)
    {
        var img = Get(image, 0, 0);
        int s = img.Width; float b = border;
        float[] xs = { dst.Left, dst.Left + b, dst.Right - b, dst.Right }, ys = { dst.Top, dst.Top + b, dst.Bottom - b, dst.Bottom };
        int[] us = { 0, border, s - border, s };
        using var paint = new SKPaint { IsAntialias = false };
        for (int j = 0; j < 3; ++j)
            for (int i = 0; i < 3; ++i)
                canvas.DrawImage(img, new SKRect(us[i], us[j], us[i + 1], us[j + 1]), new SKRect(xs[i], ys[j], xs[i + 1], ys[j + 1]), linear, paint);
    }

    /// <summary>GUI.Window with the BDArmory skin: the panel and its title.</summary>
    public void Window(float w, float h, string title)
    {
        WindowW = w; WindowH = h;
        if (canvas == null) return;
        NineSlice(ScopeImage.Panel, new SKRect(OriginX, OriginY, OriginX + w, OriginY + h), ScopeArtMetrics.WindowBorder);
        EndGroup();
        Text(new ScopeRect(0, 1, w, 15), title, ScopeArtMetrics.TitleFontSize, true, ScopePalette.Title, ScopeAlign.Centre);
    }

    /// <summary>GUI.Button with the BDArmory skin (on: a toggled button).</summary>
    public void Button(ScopeRect rect, string label, bool on = false, bool disabled = false)
    {
        if (canvas == null) return;
        NineSlice(on ? ScopeImage.ButtonOn : ScopeImage.Button, R(rect), ScopeArtMetrics.ButtonBorder);
        Text(rect, label, ScopeArtMetrics.ButtonFontSize, false, disabled ? ScopePalette.TextDim.WithAlpha(0.6f) : on ? ScopePalette.Phosphor : ScopePalette.Text, ScopeAlign.Centre);
    }
}

/// <summary>The window and control metrics BDArmory's radar and RWR windows use (VesselRadarData, RadarWarningReceiver).</summary>
static class ScopeArtMetrics
{
    public const int WindowBorder = 8, ButtonBorder = 6, TitleFontSize = 13, ButtonFontSize = 12;
    public const float RadarScreenSize = 360, BorderSize = 10, HeaderSize = 15, ControlsWidth = 125, Gap = 2;
    public const float RwrSize = 256;
}

static class RadarScenes
{
    static ScopeRect Window(SkiaCanvas c, float scale, out float displaySize)
    {
        displaySize = ScopeArtMetrics.RadarScreenSize * scale;
        float w = displaySize + ScopeArtMetrics.BorderSize + ScopeComposer.LaunchZoneStripWidth + ScopeArtMetrics.Gap + ScopeArtMetrics.ControlsWidth + ScopeArtMetrics.Gap * 3;
        float h = displaySize + ScopeArtMetrics.BorderSize + ScopeArtMetrics.HeaderSize;
        c.Window(w, h, "Radar");
        c.BeginGroup(ScopeArtMetrics.BorderSize / 2, ScopeArtMetrics.BorderSize / 2 + ScopeArtMetrics.HeaderSize);
        return new ScopeRect(0, 0, displaySize, displaySize);
    }

    public record struct Dlz(float RangeScale, float Max, float NoEscape, float Min, float Target);
    public record struct Target(float RangeKm, float Closure, float AltitudeKm, string Type);
    public record struct Missile(string Name, string TimeToImpact, string Seeker);

    /// <summary>What VesselRadarData draws outside the scope: launch zone strip, buttons, data panel (DrawScopeData, DisplayRadarControls).</summary>
    static void Controls(SkiaCanvas c, float displaySize, float s, bool locked, bool slaved, bool omni, Dlz? dlz = null, Target? target = null, Missile? missile = null)
    {
        c.EndGroup();
        var display = new ScopeRect(ScopeArtMetrics.BorderSize / 2, ScopeArtMetrics.BorderSize / 2 + ScopeArtMetrics.HeaderSize, displaySize, displaySize);
        var strip = new ScopeRect(display.x + display.w + ScopeArtMetrics.Gap, display.y, ScopeComposer.LaunchZoneStripWidth, display.h);
        ScopeComposer.LaunchZoneStrip(c, strip);
        if (dlz is Dlz z) ScopeComposer.LaunchZone(c, strip, display, z.RangeScale, z.Max, z.NoEscape, z.Min, z.Target, null, s);
        float x = ScopeArtMetrics.BorderSize + displaySize + ScopeArtMetrics.Gap + ScopeComposer.LaunchZoneStripWidth + ScopeArtMetrics.Gap;
        float y = ScopeArtMetrics.HeaderSize + ScopeArtMetrics.BorderSize / 2, bh = 25;
        string[] labels = { "Data Link", locked ? "Cycle Lock" : omni ? "" : "Boresight", slaved ? "Unslave Turrets" : "Slave Turrets", locked ? "Unlock" : "", locked ? "Unlock All" : "" };
        for (int i = 0; i < labels.Length; ++i)
        {
            if (labels[i].Length > 0) c.Button(new ScopeRect(x, y, ScopeArtMetrics.ControlsWidth, bh), labels[i], on: i == 2 && slaved);
            y += bh + ScopeArtMetrics.Gap;
        }
        float top = ScopeArtMetrics.HeaderSize + ScopeArtMetrics.BorderSize / 2 + 5 * (25 + ScopeArtMetrics.Gap) + 6f;
        var panel = new ScopeRect(x, top, ScopeArtMetrics.ControlsWidth, display.y + display.h - top);
        if (target is Target t) panel = new ScopeRect(x, ScopeComposer.TargetData(c, panel, t.RangeKm, t.Closure, t.AltitudeKm, t.Type), ScopeArtMetrics.ControlsWidth, panel.h);
        if (missile is Missile m) ScopeComposer.MissileData(c, panel, m.Name, m.TimeToImpact, m.Seeker);
    }

    static void Range(SkiaCanvas c, ScopeRect d, float km, float scale)
    {
        ScopeComposer.RangeLayout(d, scale, out var minus, out var label, out var plus);
        c.Button(minus, "-"); c.Button(plus, "+");
        ScopeComposer.RangeReadout(c, label, km, scale);
    }

    static ScopePoint P(ScopeRect d, float fx, float fy) => new ScopePoint(d.x + d.w * fx, d.y + d.h * fy);

    public static void SectorScan(SkiaCanvas c, float s)
    {
        var d = Window(c, s, out float size);
        ScopeComposer.Background(c, ScopeImage.SectorScope, d);
        Range(c, d, 40, s);
        ScopeComposer.ScanMarker(c, d, P(d, 0.6f, 1f), P(d, 0.18f, 1f), P(d, 0.82f, 1f), false, s);
        ScopeComposer.Horizon(c, d, 12f);
        ScopeComposer.Contact(c, ContactKind.Track, Iff.Hostile, P(d, 0.33f, 0.3f), 200f, 1f, "6", s);
        ScopeComposer.Contact(c, ContactKind.Track, Iff.Unknown, P(d, 0.58f, 0.18f), 165f, 0.9f, "9", s);
        ScopeComposer.Contact(c, ContactKind.Raw, Iff.Hostile, P(d, 0.74f, 0.52f), 0f, 0.65f, null, s);
        ScopeComposer.Contact(c, ContactKind.Raw, Iff.Unknown, P(d, 0.24f, 0.62f), 0f, 0.95f, null, s);
        ScopeComposer.Contact(c, ContactKind.Track, Iff.Friend, P(d, 0.5f, 0.76f), 15f, 1f, "4", s);
        ScopeComposer.Contact(c, ContactKind.Missile, Iff.Hostile, P(d, 0.45f, 0.47f), 0f, 1f, null, s);
        ScopeComposer.InfraredContact(c, d, P(d, 0.66f, 0.33f), false, 0f, 200f, false, 1f, s);
        ScopeComposer.Cursor(c, P(d, 0.52f, 0.39f), s);
        ScopeComposer.Mode(c, d, "SCAN", null, s);
        Controls(c, size, s, false, false, false);
    }

    public static void SectorLock(SkiaCanvas c, float s)
    {
        var d = Window(c, s, out float size);
        ScopeComposer.Background(c, ScopeImage.SectorScope, d);
        Range(c, d, 40, s);
        ScopeComposer.ScanMarker(c, d, P(d, 0.45f, 1f), null, null, false, s);
        ScopeComposer.Horizon(c, d, -18f);
        ScopeComposer.Contact(c, ContactKind.Track, Iff.Hostile, P(d, 0.25f, 0.45f), 120f, 0.8f, "5", s);
        ScopeComposer.Lock(c, P(d, 0.45f, 0.415f), 195f, true, "7", s);
        ScopeComposer.Lock(c, P(d, 0.68f, 0.56f), 230f, false, "5", s);
        ScopeComposer.Contact(c, ContactKind.Missile, Iff.Hostile, P(d, 0.46f, 0.7f), 0f, 1f, null, s);
        ScopeComposer.Mode(c, d, "LOCK 1/2", "TURRETS", s);
        Controls(c, size, s, true, true, false, new Dlz(40000f, 32500f, 18000f, 2500f, 23400f),
            new Target(23.4f, 412f, 7.1f, ScopeComposer.CraftType("Su-35S Red 2", "Red")), new Missile("AIM-120D", "14 s", "ACTIVE"));
    }

    /// <summary>The launch zone's legend under pressure: ticks close together, the target's caret beside the MAX tick.</summary>
    public static void SectorLockCrowded(SkiaCanvas c, float s)
    {
        var d = Window(c, s, out float size);
        ScopeComposer.Background(c, ScopeImage.SectorScope, d);
        Range(c, d, 20, s);
        ScopeComposer.ScanMarker(c, d, P(d, 0.5f, 1f), null, null, false, s);
        ScopeComposer.Horizon(c, d, 5f);
        ScopeComposer.Lock(c, P(d, 0.55f, 0.1f), 180f, true, "4", s);
        ScopeComposer.Mode(c, d, "LOCK 1/1", null, s);
        Controls(c, size, s, true, false, false, new Dlz(20000f, 19000f, 4000f, 2500f, 18600f), new Target(18.6f, 655f, 4.2f, null));
    }

    public static void Omni(SkiaCanvas c, float s)
    {
        var d = Window(c, s, out float size);
        ScopeComposer.Background(c, ScopeImage.OmniScope, d);
        Range(c, d, 80, s);
        ScopeComposer.Sweep(c, d, 38f, false, true);
        ScopeComposer.OwnShip(c, d, 0f, s);
        ScopeComposer.Contact(c, ContactKind.Track, Iff.Hostile, P(d, 0.36f, 0.24f), 140f, 1f, "8", s);
        ScopeComposer.Contact(c, ContactKind.Track, Iff.Unknown, P(d, 0.7f, 0.3f), 250f, 0.75f, "6", s);
        ScopeComposer.Contact(c, ContactKind.Raw, Iff.Hostile, P(d, 0.78f, 0.66f), 0f, 0.5f, null, s);
        ScopeComposer.Contact(c, ContactKind.Track, Iff.Friend, P(d, 0.42f, 0.62f), 350f, 1f, "5", s);
        ScopeComposer.InfraredContact(c, d, default, true, -62f, 320f, false, 1f, s);
        ScopeComposer.InfraredContact(c, d, default, true, 152f, 90f, false, 0.8f, s);
        ScopeComposer.InfraredContact(c, d, default, true, 24f, 0f, true, 1f, s);
        ScopeComposer.Mode(c, d, "SCAN", null, s);
        Controls(c, size, s, false, false, true);
    }

    public static void IrstJammed(SkiaCanvas c, float s)
    {
        var d = Window(c, s, out float size);
        ScopeComposer.Background(c, ScopeImage.SectorScope, d);
        Range(c, d, 20, s);
        ScopeComposer.ScanMarker(c, d, P(d, 0.35f, 1f), P(d, 0.25f, 1f), P(d, 0.75f, 1f), true, s);
        ScopeComposer.Horizon(c, d, 4f);
        foreach (var (fx, fy) in new[] { (0.52f, 0.3f), (0.56f, 0.38f), (0.5f, 0.46f), (0.55f, 0.55f) })
            ScopeComposer.Contact(c, ContactKind.Jammed, Iff.Hostile, P(d, fx, fy), 0f, 1f, null, s);
        ScopeComposer.InfraredContact(c, d, P(d, 0.3f, 0.35f), false, 0f, 350f, false, 1f, s);
        ScopeComposer.InfraredContact(c, d, P(d, 0.72f, 0.62f), false, 0f, 120f, false, 0.6f, s);
        ScopeComposer.InfraredContact(c, d, P(d, 0.4f, 0.75f), false, 0f, 0f, true, 1f, s);
        ScopeComposer.Mode(c, d, "IRST SCAN", null, s);
        Controls(c, size, s, false, false, false);
    }
}

static class RwrScenes
{
    static ScopeRect Window(SkiaCanvas c, float scale, out ScopeRect strip)
    {
        float size = ScopeArtMetrics.RwrSize * scale, statusH = ScopeComposer.RwrStatusHeight * Math.Max(1f, scale);
        c.Window(size + ScopeArtMetrics.BorderSize, size + ScopeArtMetrics.BorderSize + ScopeArtMetrics.HeaderSize + statusH, "Radar Warning Receiver");
        c.BeginGroup(ScopeArtMetrics.BorderSize / 2, ScopeArtMetrics.HeaderSize + ScopeArtMetrics.BorderSize / 2);
        strip = new ScopeRect(0, size, size, statusH);
        return new ScopeRect(0, 0, size, size);
    }

    static ScopePoint Bearing(ScopeRect d, float bearingDeg, float radius)
    {
        double a = bearingDeg * Math.PI / 180.0;
        return new ScopePoint(d.x + d.w * 0.5f + (float)Math.Sin(a) * radius * d.w * 0.5f, d.y + d.h * 0.5f - (float)Math.Cos(a) * radius * d.h * 0.5f);
    }

    public static void Search(SkiaCanvas c, float s)
    {
        var d = Window(c, s, out var strip);
        ScopeComposer.Background(c, ScopeImage.RwrScope, d);
        ScopeComposer.Emitter(c, Bearing(d, 300f, 0.72f), "S", EmitterState.Search, false, false, true, s);
        ScopeComposer.Emitter(c, Bearing(d, 40f, 0.78f), ScopeComposer.ThreatCode("Su-35S Red 2"), EmitterState.Search, false, true, true, s);
        ScopeComposer.Emitter(c, Bearing(d, 205f, 0.8f), "A", EmitterState.Search, false, true, true, s);
        ScopeComposer.Emitter(c, Bearing(d, 345f, 0.82f), "U", EmitterState.Search, false, true, true, s);
        ScopeComposer.Emitter(c, Bearing(d, 120f, 0.55f), ScopeComposer.ThreatCode("F-15Z2 Blue 1"), EmitterState.Search, true, true, true, s);
        ScopeComposer.RwrStatus(c, strip, "5 EMITTERS", EmitterState.Search, s);
    }

    public static void Threats(SkiaCanvas c, float s)
    {
        var d = Window(c, s, out var strip);
        ScopeComposer.Background(c, ScopeImage.RwrScope, d);
        ScopeComposer.Emitter(c, Bearing(d, 210f, 0.78f), "S", EmitterState.Search, false, false, true, s);
        ScopeComposer.Emitter(c, Bearing(d, 18f, 0.62f), ScopeComposer.ThreatCode("MiG-29M Red 2"), EmitterState.Lock, false, true, true, s);
        ScopeComposer.Emitter(c, Bearing(d, 328f, 0.5f), ScopeComposer.ThreatCode("Su-35S Red 2"), EmitterState.Launch, false, true, true, s);
        ScopeComposer.Emitter(c, Bearing(d, 70f, 0.8f), ScopeComposer.ThreatCode("EF2000 Red 3"), EmitterState.Lock, false, true, true, s);
        ScopeComposer.Emitter(c, Bearing(d, 78f, 0.66f), "J", EmitterState.Search, false, true, true, s);
        ScopeComposer.Missile(c, d, Bearing(d, 334f, 0.86f), true, s);
        ScopeComposer.Missile(c, d, Bearing(d, 105f, 0.62f), true, s);
        ScopeComposer.RwrStatus(c, strip, "LAUNCH · 35 · 31 km · +2", EmitterState.Launch, s);
    }
}
