// ambient's single-color pass. Every color in a finished card becomes the theme's one foreground color; a color keeps
// its own value (perceptual lightness, CIE L*) and transparency, so contrast inside a card survives:
//   v = min_value + (1 - min_value) * L*
//   alpha mode: foreground at alpha a * v          ramp mode: tile color mixed toward the foreground by v, alpha a
// The card's own background becomes the translucent tile. Photos, camera views and video are never touched; SVG icons
// and graphs (data URIs) are rewritten: fill / stroke / stop-color, as attributes or in style, with *-opacity multiplied.
// Pure C#.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace TizenNotifier
{
    internal static class Recolor
    {
        private const string SvgPrefix = "data:image/svg+xml;base64,";

        // CSS names as SVG reads them (HA's own names never reach here: cards resolve them to hex first)
        private static readonly Dictionary<string, string> Css = new Dictionary<string, string>
        {
            ["white"] = "#ffffff", ["black"] = "#000000", ["red"] = "#ff0000", ["green"] = "#008000", ["blue"] = "#0000ff",
            ["orange"] = "#ffa500", ["yellow"] = "#ffff00", ["gray"] = "#808080", ["grey"] = "#808080", ["silver"] = "#c0c0c0",
            ["purple"] = "#800080", ["transparent"] = "#00000000",
        };

        // #rgb, #rgba, #rrggbb, #rrggbbaa, rgb(), rgba(), CSS names -> r, g, b, a in 0..1; null when not a color
        public static double[] Parse(string c)
        {
            if (string.IsNullOrWhiteSpace(c)) return null;
            c = c.Trim().ToLowerInvariant();
            if (Css.TryGetValue(c, out var named)) c = named;
            if (c.StartsWith("#"))
            {
                var h = c.Substring(1);
                if (h.Length == 3 || h.Length == 4) h = string.Concat(h.Select(ch => new string(ch, 2)));
                if (h.Length == 6) h += "ff";
                if (h.Length != 8 || !int.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _)) return null;
                return Enumerable.Range(0, 4).Select(i => Convert.ToInt32(h.Substring(i * 2, 2), 16) / 255.0).ToArray();
            }
            if (c.StartsWith("rgb"))
            {
                var p = c.Substring(c.IndexOf('(') + 1).TrimEnd(')').Split(',').Select(x => x.Trim()).ToArray();
                if (p.Length < 3) return null;
                var v = new double[4];
                for (int i = 0; i < 3; i++) { if (!double.TryParse(p[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return null; v[i] = d / 255; }
                v[3] = p.Length > 3 && double.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var a) ? a : 1;
                return v;
            }
            return null;
        }

        private static double Lin(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        public static double Luminance(double[] c) => 0.2126 * Lin(c[0]) + 0.7152 * Lin(c[1]) + 0.0722 * Lin(c[2]);
        // perceptual lightness (CIE L*, 0..1): spreads saturated mid colors (graph lines, accents) better than luminance
        public static double Lightness(double[] c)
        {
            double y = Luminance(c);
            return (y > 216.0 / 24389 ? 116 * Math.Pow(y, 1.0 / 3) - 16 : y * 24389.0 / 27) / 100;
        }

        private static string Hex(double r, double g, double b, double a) =>
            "#" + string.Concat(new[] { r, g, b, a }.Select(x => ((int)Math.Round(Math.Max(0, Math.Min(1, x)) * 255)).ToString("x2")));

        // One color -> #rrggbbaa in the theme's color. Unparseable values come back unchanged.
        public static string Map(string color, RecolorSpec r)
        {
            var c = Parse(color);
            if (c == null) return color;
            var (rgb, a) = MapParts(c, r);
            return Hex(rgb[0], rgb[1], rgb[2], a);
        }

        private static (double[] rgb, double a) MapParts(double[] c, RecolorSpec r)
        {
            var fg = Parse(r.Color) ?? new double[] { 1, 1, 1, 1 };
            double v = r.MinValue + (1 - r.MinValue) * Math.Max(0, Math.Min(1, Lightness(c)));
            if (r.Mode == "ramp")
            {
                var bg = Parse(r.TileColor) ?? new double[] { 0, 0, 0, 1 };
                return (Enumerable.Range(0, 3).Select(i => bg[i] + (fg[i] - bg[i]) * v).ToArray(), c[3]);
            }
            return (new[] { fg[0], fg[1], fg[2] }, c[3] * v);
        }

        // A built card, in place: its background becomes the tile, every other color is mapped.
        // tile: false for a holder that isn't a card (the badge block: its chips are mapped, it gets no tile).
        public static void Card(Dictionary<string, object> card, RecolorSpec r, Dictionary<string, string> svgCache = null, bool tile = true)
        {
            svgCache = svgCache ?? new Dictionary<string, string>();
            bool drawn = Json.S(card, "bg") != null;   // stacks and chip rows draw no card of their own: no tile
            Node(card, r, svgCache, false, true);
            if (!tile || !drawn) return;
            var tc = Parse(r.TileColor) ?? new double[] { 0, 0, 0, 1 };
            card["bg"] = Hex(tc[0], tc[1], tc[2], r.TileOpacity);
            card["radius"] = r.Radius;
        }

        // onFill: inside a filled shape (an event bar, a today circle): its text takes the tile color, so it stays
        // readable whatever the shape maps to. A shape counts as filled when its own background is at least half opaque.
        private static void Node(Dictionary<string, object> n, RecolorSpec r, Dictionary<string, string> cache, bool onFill, bool isCard = false)
        {
            string type = Json.S(n, "type") ?? "box";
            string src = Json.S(n, "src");
            if (type == "video" || type == "mjpeg" || type == "gif" || type == "lottie") return;   // moving pictures: as they are
            if (type == "image" && src != null && !src.StartsWith(SvgPrefix)) return;              // photos: as they are
            bool surface = !isCard && r.Surface != null && string.Equals(Json.S(n, "bg"), r.Surface, StringComparison.OrdinalIgnoreCase);
            if (surface) onFill = false;
            else if (!isCard && Parse(Json.S(n, "bg")) is double[] bgc && bgc[3] >= 0.5) onFill = true;
            if (type == "text" && Parse(Json.S(n, "color")) is double[] tc)
            {
                // all text at one opacity (a color's own transparency made secondary text vanish on a dim panel); on a
                // filled shape it takes the tile color, elsewhere the foreground, with an outline in the other
                var knock = Parse(r.KnockoutColor) ?? new double[] { 0, 0, 0, 1 };
                var fg = Parse(r.Color) ?? new double[] { 1, 1, 1, 1 };
                var tcol = onFill ? knock : fg;
                n["color"] = Hex(tcol[0], tcol[1], tcol[2], r.TextOpacity);
                if (r.Outline > 0 && !onFill)
                {
                    var oc = Parse(r.OutlineColor) ?? new double[] { 0, 0, 0, 1 };
                    n["outline"] = new Dictionary<string, object> { ["color"] = Hex(oc[0], oc[1], oc[2], oc[3]), ["width"] = r.Outline };
                }
                int Rank(string wt) => wt == "light" || wt == "thin" ? 0 : wt == "medium" || wt == "semiBold" ? 2 : wt == "bold" ? 3 : 1;
                if (Rank(Json.S(n, "weight")) < Rank(r.MinWeight)) { if (r.MinWeight == "regular") n.Remove("weight"); else n["weight"] = r.MinWeight; }
                if (r.Shadow > 0 && !onFill)
                {
                    var sc = Parse(r.ShadowColor) ?? new double[] { 0, 0, 0, 1 };
                    n["text_shadow"] = new Dictionary<string, object> { ["color"] = Hex(sc[0], sc[1], sc[2], sc[3]), ["x"] = r.ShadowX, ["y"] = r.ShadowY, ["blur"] = r.Shadow };
                }
            }
            else if (Json.S(n, "color") is string col) n["color"] = Map(col, r);
            if (surface) { var tc2 = Parse(r.TileColor) ?? new double[] { 0, 0, 0, 1 }; n["bg"] = Hex(tc2[0], tc2[1], tc2[2], r.TileOpacity); }
            else if (Json.S(n, "bg") is string c) n["bg"] = Map(c, r);
            if (Json.O(n, "gradient") is Dictionary<string, object> g && Json.A(g, "colors") is List<object> cols)
                g["colors"] = cols.Select(x => x is string s ? (object)(SurfaceStop(s, r) ?? Map(s, r)) : x).ToList();
            if (Json.O(n, "shadow") is Dictionary<string, object> sh && Json.S(sh, "color") is string shc) sh["color"] = Map(shc, r);
            if (src != null && src.StartsWith(SvgPrefix))
            {
                if (!cache.TryGetValue(src, out var mapped))
                {
                    var svg = Encoding.UTF8.GetString(Convert.FromBase64String(src.Substring(SvgPrefix.Length)));
                    cache[src] = mapped = TizenNotifier.Svg.DataUri(Svg(svg, r));
                }
                n["src"] = mapped;
            }
            if (Json.A(n, "children") is List<object> kids)
                foreach (var k in kids) if (k is Dictionary<string, object> kd) Node(kd, r, cache, onFill);
        }

        // A gradient stop in the card color (a fade into the card) -> the tile color, at the same share of the tile's alpha.
        private static string SurfaceStop(string stop, RecolorSpec r)
        {
            var sc = Parse(r.Surface); var c = Parse(stop);
            if (sc == null || c == null || Math.Abs(sc[0] - c[0]) + Math.Abs(sc[1] - c[1]) + Math.Abs(sc[2] - c[2]) > 0.01) return null;
            var t = Parse(r.TileColor) ?? new double[] { 0, 0, 0, 1 };
            return Hex(t[0], t[1], t[2], r.TileOpacity * (sc[3] > 0 ? c[3] / sc[3] : 0));
        }

        // ---------- SVG ----------
        private static readonly Regex Tag = new Regex(@"<[a-zA-Z][^>]*>");
        private static readonly string[] Paints = { "fill", "stroke", "stop-color" };
        private static string OpacityOf(string paint) => paint == "stop-color" ? "stop-opacity" : paint + "-opacity";
        private static string Num(double d) => Math.Round(d, 3).ToString("0.###", CultureInfo.InvariantCulture);

        public static string Svg(string svg, RecolorSpec r) => Tag.Replace(svg, m => TagRewrite(m.Value, r));

        private static string TagRewrite(string tag, RecolorSpec r)
        {
            foreach (var p in Paints)
            {
                var am = new Regex("(?<=[\\s<])" + p + "=\"([^\"]*)\"").Match(tag);
                if (!am.Success) continue;
                var c = Parse(am.Groups[1].Value);
                if (c == null) continue;   // none, url(#...), currentColor
                var (rgb, a) = MapParts(c, r);
                tag = tag.Substring(0, am.Index) + p + "=\"" + Hex(rgb[0], rgb[1], rgb[2], 1).Substring(0, 7) + "\"" + tag.Substring(am.Index + am.Length);
                string on = OpacityOf(p);
                var om = new Regex("(?<=\\s)" + on + "=\"([^\"]*)\"").Match(tag);
                double o = om.Success && double.TryParse(om.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var od) ? od : 1;
                double na = o * a;
                if (p == "fill" && om.Success && o < 1) na = Math.Max(na, r.MinFill);   // a shaded area, not an icon
                string attr = on + "=\"" + Num(na) + "\"";
                tag = om.Success ? tag.Substring(0, om.Index) + attr + tag.Substring(om.Index + om.Length) : InsertAttr(tag, attr);
            }
            var sm = new Regex("(?<=\\s)style=\"([^\"]*)\"").Match(tag);
            if (sm.Success)
            {
                var props = sm.Groups[1].Value.Split(';').Select(x => x.Trim()).Where(x => x.Length > 0)
                    .Select(x => { int i = x.IndexOf(':'); return i < 0 ? (k: x, v: "") : (k: x.Substring(0, i).Trim(), v: x.Substring(i + 1).Trim()); }).ToList();
                foreach (var p in Paints)
                {
                    int i = props.FindIndex(x => x.k == p);
                    if (i < 0) continue;
                    var c = Parse(props[i].v);
                    if (c == null) continue;
                    var (rgb, a) = MapParts(c, r);
                    props[i] = (p, Hex(rgb[0], rgb[1], rgb[2], 1).Substring(0, 7));
                    string on = OpacityOf(p);
                    int oi = props.FindIndex(x => x.k == on);
                    double o = oi >= 0 && double.TryParse(props[oi].v, NumberStyles.Float, CultureInfo.InvariantCulture, out var od) ? od : 1;
                    if (oi >= 0) props[oi] = (on, Num(o * a)); else props.Add((on, Num(o * a)));
                }
                string style = "style=\"" + string.Join(";", props.Select(x => x.k + ":" + x.v)) + "\"";
                tag = tag.Substring(0, sm.Index) + style + tag.Substring(sm.Index + sm.Length);
            }
            return tag;
        }

        private static string InsertAttr(string tag, string attr)
        {
            int end = tag.EndsWith("/>") ? tag.Length - 2 : tag.Length - 1;
            return tag.Substring(0, end).TrimEnd() + " " + attr + tag.Substring(end);
        }
    }
}
