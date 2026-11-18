using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NotchWin;

/// <summary>A look for the notch. Colours are applied as resources, so the whole notch restyles instantly.</summary>
public class Theme
{
    public string Id = "";
    public string Name = "";
    public string Description = "";
    public Color Bg1, Bg2;                 // background gradient: top → bottom
    public Color Border = Colors.Transparent;
    public double BorderThickness;
    public Color Glow = Colors.Black;      // shadow colour (a coloured glow for neon-style themes)
    public double GlowOpacity = 0.55;
    public bool Shadow = true;
    public Color Fg = Colors.White;        // text colour
    public Color Accent1, Accent2, Accent3; // CPU, RAM, GPU / equalizer

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    public static readonly Theme[] All =
    {
        new() { Id = "midnight", Name = "Midnight", Description = "Pure black, like the real thing",
            Bg1 = C("#050505"), Bg2 = C("#050505"),
            Accent1 = C("#0A84FF"), Accent2 = C("#BF5AF2"), Accent3 = C("#30D158") },

        new() { Id = "nightfall", Name = "Nightfall", Description = "Deep indigo dusk with violet light",
            Bg1 = C("#0B0A1A"), Bg2 = C("#231650"), Border = C("#40FFFFFF"), BorderThickness = 0,
            Glow = C("#5B3FD9"), GlowOpacity = 0.55,
            Accent1 = C("#818CF8"), Accent2 = C("#C084FC"), Accent3 = C("#F0ABFC") },

        new() { Id = "aurora", Name = "Aurora", Description = "Northern lights: teal into green",
            Bg1 = C("#03110F"), Bg2 = C("#0B3B33"),
            Glow = C("#10B981"), GlowOpacity = 0.45,
            Accent1 = C("#2DD4BF"), Accent2 = C("#34D399"), Accent3 = C("#A3E635") },

        new() { Id = "sunset", Name = "Sunset", Description = "Warm plum fading into orange",
            Bg1 = C("#1A0712"), Bg2 = C("#5A1E1A"),
            Glow = C("#F97316"), GlowOpacity = 0.4,
            Accent1 = C("#FB923C"), Accent2 = C("#F472B6"), Accent3 = C("#FACC15") },

        new() { Id = "ocean", Name = "Ocean", Description = "Deep sea blues",
            Bg1 = C("#020B18"), Bg2 = C("#0A2A4A"),
            Glow = C("#0EA5E9"), GlowOpacity = 0.4,
            Accent1 = C("#38BDF8"), Accent2 = C("#22D3EE"), Accent3 = C("#5EEAD4") },

        new() { Id = "neon", Name = "Neon", Description = "Black with a glowing cyberpunk edge",
            Bg1 = C("#05010A"), Bg2 = C("#0A0214"), Border = C("#FF22D3EE"), BorderThickness = 1.2,
            Glow = C("#22D3EE"), GlowOpacity = 0.85,
            Accent1 = C("#22D3EE"), Accent2 = C("#E879F9"), Accent3 = C("#A3FF12") },

        new() { Id = "sakura", Name = "Sakura", Description = "Soft cherry-blossom pinks",
            Bg1 = C("#140810"), Bg2 = C("#3D1530"),
            Glow = C("#EC4899"), GlowOpacity = 0.4,
            Accent1 = C("#F9A8D4"), Accent2 = C("#F472B6"), Accent3 = C("#FBCFE8") },

        new() { Id = "ember", Name = "Ember", Description = "Charcoal with glowing red embers",
            Bg1 = C("#0C0606"), Bg2 = C("#2A0A08"), Border = C("#55FF5A36"), BorderThickness = 1,
            Glow = C("#EF4444"), GlowOpacity = 0.5,
            Accent1 = C("#F87171"), Accent2 = C("#FB923C"), Accent3 = C("#FCD34D") },

        new() { Id = "glass", Name = "Glass", Description = "See-through smoked glass",
            Bg1 = C("#B8101014"), Bg2 = C("#C8181822"), Border = C("#40FFFFFF"), BorderThickness = 1,
            Shadow = false,
            Accent1 = C("#93C5FD"), Accent2 = C("#C4B5FD"), Accent3 = C("#86EFAC") },

        new() { Id = "daylight", Name = "Daylight", Description = "Clean light mode",
            Bg1 = C("#FAFAFC"), Bg2 = C("#ECECF2"), Border = C("#22000000"), BorderThickness = 1,
            Glow = C("#000000"), GlowOpacity = 0.25, Fg = C("#16161A"),
            Accent1 = C("#2563EB"), Accent2 = C("#9333EA"), Accent3 = C("#16A34A") },

        new() { Id = "vibe", Name = "Vibe", Description = "Takes its colours from whatever's playing",
            Bg1 = C("#050505"), Bg2 = C("#1B1440"),
            Glow = C("#5B3FD9"), GlowOpacity = 0.5,
            Accent1 = C("#818CF8"), Accent2 = C("#C084FC"), Accent3 = C("#F0ABFC") },
    };

    public static Theme Find(string id) =>
        All.FirstOrDefault(t => t.Id.Equals(id ?? "", System.StringComparison.OrdinalIgnoreCase)) ?? All[0];

    public static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    public LinearGradientBrush MakeBackground() =>
        new(new GradientStopCollection { new GradientStop(Bg1, 0), new GradientStop(Bg2, 1) }, 90);

    /// <summary>All the brushes the notch uses, keyed by resource name.</summary>
    public Dictionary<string, Brush> MakeBrushes() => new()
    {
        ["Fg"] = new SolidColorBrush(Fg),
        ["FgSoft"] = new SolidColorBrush(WithAlpha(Fg, 0xC0)),
        ["FgDim"] = new SolidColorBrush(WithAlpha(Fg, 0x99)),
        ["FgFaint"] = new SolidColorBrush(WithAlpha(Fg, 0x55)),
        ["Track"] = new SolidColorBrush(WithAlpha(Fg, 0x1F)),
        ["ButtonBg"] = new SolidColorBrush(WithAlpha(Fg, 0x1F)),
        ["ButtonHover"] = new SolidColorBrush(WithAlpha(Fg, 0x40)),
        ["ButtonPress"] = new SolidColorBrush(WithAlpha(Fg, 0x66)),
        ["TileBg"] = new SolidColorBrush(WithAlpha(Fg, 0x14)),
        ["TileHover"] = new SolidColorBrush(WithAlpha(Fg, 0x33)),
        ["NotchBorder"] = new SolidColorBrush(Border),
        ["Eq"] = new SolidColorBrush(Accent3),
    };

    // ------------------------------------------------------------------
    //  "Vibe": colours picked from the album art
    // ------------------------------------------------------------------

    /// <summary>
    /// The most vivid colour in an image: average of its pixels, weighted towards saturated ones,
    /// so a mostly-black cover with a red logo comes out red, not grey.
    /// </summary>
    public static Color? DominantColor(BitmapSource img)
    {
        try
        {
            double scale = 24.0 / System.Math.Max(img.PixelWidth, img.PixelHeight);
            var small = new TransformedBitmap(img, new ScaleTransform(scale, scale));
            var bgra = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
            int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
            var px = new byte[h * stride];
            bgra.CopyPixels(px, stride, 0);

            double r = 0, g = 0, b = 0, total = 0;
            for (int i = 0; i < px.Length; i += 4)
            {
                double bb = px[i] / 255.0, gg = px[i + 1] / 255.0, rr = px[i + 2] / 255.0;
                double max = System.Math.Max(rr, System.Math.Max(gg, bb)), min = System.Math.Min(rr, System.Math.Min(gg, bb));
                double sat = max <= 0 ? 0 : (max - min) / max;
                double weight = 0.05 + sat * sat * max; // vivid & bright pixels count most
                r += rr * weight; g += gg * weight; b += bb * weight; total += weight;
            }
            if (total <= 0) return null;
            return Color.FromRgb((byte)(255 * r / total), (byte)(255 * g / total), (byte)(255 * b / total));
        }
        catch { return null; }
    }

    /// <summary>Scales a colour's brightness so it always reads well as a background or an accent.</summary>
    public static Color Shade(Color c, double targetBrightness)
    {
        double max = System.Math.Max(c.R, System.Math.Max(c.G, c.B)) / 255.0;
        if (max < 0.01) return Color.FromRgb((byte)(targetBrightness * 255), (byte)(targetBrightness * 255), (byte)(targetBrightness * 255));
        double k = targetBrightness / max;
        byte S(byte v) => (byte)System.Math.Clamp(v * k, 0, 255);
        return Color.FromRgb(S(c.R), S(c.G), S(c.B));
    }
}
