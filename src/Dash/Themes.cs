// The three dashboard themes. A DashTheme is built per render from the effective settings (Settings.Resolve).
//   default:  today's look (translucent cards, system font), HA-matched layout.
//   lovelace: HA's own dark theme (colors sampled from HA screenshots of the scratch views, 2026-11-15), Roboto.
//   ambient:  one foreground color, translucent tiles, Inter, corner flow layout (Flow.cs, Recolor.cs).
using System;
using System.Collections.Generic;

namespace TizenNotifier
{
    internal sealed class RecolorSpec
    {
        public string Color = "#ffffff", TileColor = "#000000", Mode = "alpha";
        public string Surface;   // the palette's card color: inner boxes drawn in it (badge chips, sub-panels) become tiles
        // text over a dimmed Art Mode panel: one opacity for all text, and an outline (TV px; 0 = none)
        public double TextOpacity = 1, Outline = 2, Shadow = 0, ShadowX = 2, ShadowY = 2;   // Shadow: blur (0 = no shadow)
        public string OutlineColor = "#000000", ShadowColor = "#000000";
        public string MinWeight = "medium";
        public string KnockoutColor = "#000000";
        public double MinFill = 0;   // translucent SVG fills (a graph's shaded area) are at least this opaque   // text on a filled shape (event bars, the today circle)   // light < regular < medium < bold: lighter text is drawn at this weight
        public double TileOpacity = 0.35, MinValue = 0.35, Radius = 16;
    }

    internal sealed class DashTheme
    {
        public string Name = "default", Font, Layout = "ha", Conditionals = "instant";
        public Palette P = new Palette();
        public RecolorSpec Recolor;
        public Dictionary<string, object> Params = new Dictionary<string, object>();
        public double WidthFactor = 1.0;   // text width estimate relative to the TV's system font

        public static DashTheme From(EffectiveOptions e)
        {
            var t = new DashTheme { Name = e.ThemeName, Params = e.ThemeParams, Conditionals = e.Conditionals };
            var p = e.ThemeParams;
            switch (e.ThemeName)
            {
                case "lovelace":
                    // sampled: view #111111, card #1c1c1c, border #333333, primary text #e1e1e1, secondary #9b9b9b
                    t.P = Palette.From(p, new Palette { CardBg = "#1c1c1c", Text = "#e1e1e1", Secondary = "#9b9b9b", Divider = "#333333", Accent = "#03a9f4",
                        // HA's current series palette (calendar s4: calendars 0, 1, 3 drew #4269d0, #f4bd4a, #6cc5b0; ended events at 50%)
                        Series = new[] { "#4269d0", "#f4bd4a", "#ff725c", "#6cc5b0", "#3ca951", "#ff8ab7", "#a463f2", "#97bbf5", "#9c6b4e", "#9498a0" },
                        PastAlpha = 0.5, IdleAlpha = 0.5 });
                    t.Font = "Roboto";
                    break;
                case "ambient":
                    t.P = Palette.From(p, new Palette { Radius = Json.N(p, "radius", 16) });
                    t.Font = Json.S(p, "font") ?? "Inter";
                    t.Layout = "flow";
                    t.WidthFactor = 1.08;   // Inter runs wider than the system font; measured on the TV in the last step
                    t.Recolor = new RecolorSpec
                    {
                        Color = Json.S(p, "color") ?? "#ffffff", TileColor = Json.S(p, "tile_color") ?? "#000000",
                        TileOpacity = Json.N(p, "tile_opacity", 0.35), MinValue = Json.N(p, "min_value", 0.35),
                        Radius = Json.N(p, "radius", 16), Mode = Json.S(p, "recolor") ?? "alpha", Surface = t.P.CardBg,
                        TextOpacity = Math.Max(0, Math.Min(1, Json.N(p, "text_opacity", 1))), Outline = Math.Max(0, Json.N(p, "outline", 2)),
                        OutlineColor = Json.S(p, "outline_color") ?? "#000000",
                        Shadow = Math.Max(0, Json.N(p, "shadow", 0)), ShadowColor = Json.S(p, "shadow_color") ?? "#000000",
                        ShadowX = Json.N(p, "shadow_x", 2), ShadowY = Json.N(p, "shadow_y", 2),
                        MinWeight = Json.S(p, "min_weight") ?? "medium", KnockoutColor = Json.S(p, "knockout_color") ?? "#000000",
                        MinFill = Math.Max(0, Math.Min(1, Json.N(p, "fill_opacity", 0.45))),
                    };
                    break;
                default:
                    t.P = Palette.From(p);
                    break;
            }
            return t;
        }
    }
}
