// Node builders for the dashboard renderer. Everything is built in frontend CSS pixels at (0,0) of its card;
// Layout scales and names the finished tree. Numbers are doubles because the notifier's node reader expects JSON numbers.
using System;
using System.Collections.Generic;
using System.Linq;

namespace TizenNotifier
{
    internal sealed class Palette
    {
        public string CardBg = "#121216b8", Text = "#ffffff", Secondary = "#ffffffb0", Divider = "#ffffff26", Accent = "#03a9f4", Inactive = "#9e9e9e";
        public double Radius = 12, Pad = 12;

        public static Palette From(Dictionary<string, object> t)
        {
            var p = new Palette();
            p.CardBg = Json.S(t, "card_bg") ?? p.CardBg;
            p.Text = Json.S(t, "text") ?? p.Text;
            p.Secondary = Json.S(t, "secondary_text") ?? p.Secondary;
            p.Accent = Json.S(t, "accent") ?? p.Accent;
            p.Radius = Json.N(t, "radius", p.Radius);
            if (Json.Get(t, "card_opacity") is double o) p.CardBg = Theme.WithAlpha(p.CardBg, o);
            return p;
        }
    }

    internal static class Ui
    {
        public static Dictionary<string, object> Box(double x, double y, double w, double h, string bg = null, double radius = 0)
        {
            var d = new Dictionary<string, object> { ["type"] = "box", ["x"] = x, ["y"] = y, ["w"] = Math.Max(1, w), ["h"] = Math.Max(1, h) };
            if (bg != null) d["bg"] = bg;
            if (radius > 0) d["radius"] = radius;
            return d;
        }

        public static Dictionary<string, object> Add(Dictionary<string, object> parent, Dictionary<string, object> child)
        {
            if (child == null) return parent;
            if (!(Json.Get(parent, "children") is List<object> l)) { l = new List<object>(); parent["children"] = l; }
            l.Add(child);
            return parent;
        }

        public static Dictionary<string, object> Text(double x, double y, double w, double h, string text, double px, string color,
            string weight = null, string align = "left", string valign = "center", bool multiline = false)
        {
            var d = new Dictionary<string, object>
            {
                ["type"] = "text", ["x"] = x, ["y"] = y, ["w"] = Math.Max(1, w), ["h"] = Math.Max(1, h), ["text"] = text ?? "", ["pixel_size"] = px,
                ["color"] = color, ["align"] = align, ["valign"] = valign, ["ellipsis"] = true, ["multiline"] = multiline,
            };
            if (weight != null) d["weight"] = weight;
            return d;
        }

        public static Dictionary<string, object> Icon(double x, double y, double size, string icon, string color) =>
            new Dictionary<string, object> { ["type"] = "image", ["x"] = x, ["y"] = y, ["w"] = size, ["h"] = size, ["fit"] = "fill", ["src"] = Svg.DataUri(Svg.Icon(icon, color)) };

        public static Dictionary<string, object> SvgImg(double x, double y, double w, double h, string svg) =>
            new Dictionary<string, object> { ["type"] = "image", ["x"] = x, ["y"] = y, ["w"] = Math.Max(1, w), ["h"] = Math.Max(1, h), ["fit"] = "fill", ["src"] = TizenNotifier.Svg.DataUri(svg) };

        public static Dictionary<string, object> Image(double x, double y, double w, double h, string src, double radius = 0, int refreshMs = 0, string fit = "cover")
        {
            var d = new Dictionary<string, object> { ["type"] = "image", ["x"] = x, ["y"] = y, ["w"] = Math.Max(1, w), ["h"] = Math.Max(1, h), ["src"] = src, ["fit"] = fit };
            if (radius > 0) d["radius"] = radius;
            if (refreshMs > 0) d["refresh_ms"] = (double)refreshMs;
            return d;
        }

        // Icon inside a tinted circle (tile / mushroom style).
        public static Dictionary<string, object> IconBadge(double x, double y, double size, string icon, string color, bool active)
        {
            var b = Box(x, y, size, size, Theme.WithAlpha(color, active ? 0.22 : 0.12), size / 2);
            Add(b, Icon(size * 0.2, size * 0.2, size * 0.6, icon, active ? color : "#e0e0e0"));
            return b;
        }

        // Rough text width for a proportional sans-serif (no font metrics outside NUI).
        public static double Width(string s, double px, bool bold = false) => string.IsNullOrEmpty(s) ? 0 : s.Length * px * (bold ? 0.58 : 0.54);

        // Greedy word wrap with the same estimate; returns line count (at least 1).
        public static int Lines(string s, double px, double w)
        {
            if (string.IsNullOrEmpty(s)) return 1;
            int lines = 0;
            foreach (var para in s.Split('\n'))
            {
                lines++;
                double cur = 0, space = px * 0.3;
                foreach (var word in para.Split(' '))
                {
                    double ww = Width(word, px);
                    if (cur > 0 && cur + space + ww > w) { lines++; cur = ww; }
                    else cur += (cur > 0 ? space : 0) + ww;
                    while (cur > w && w > px) { lines++; cur -= w; }
                }
            }
            return Math.Max(1, lines);
        }
    }
}
