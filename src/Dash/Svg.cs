// SVG building blocks for icon, graph and gauge nodes. Pure C# (no NUI): also compiled into the desktop dash host.
// Measured 2026-11-15 on Tizen 6.0: the image view draws these SVGs (paths with fill, polylines with stroke,
// fill-opacity, alpha over other content), so icons and graphs need no extra renderer.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace TizenNotifier
{
    internal static class Mdi
    {
        private static Dictionary<string, string> paths;
        private static readonly object L = new object();
        public static string File;   // path of mdi.tsv.gz (lines "name\tpath" or "alias\t=name")

        // "mdi:lightbulb" or "lightbulb" -> SVG path data, or null.
        public static string Path(string icon)
        {
            if (string.IsNullOrEmpty(icon)) return null;
            string n = icon.StartsWith("mdi:") ? icon.Substring(4) : icon.Contains(":") ? null : icon;
            if (n == null) return null;
            lock (L)
            {
                if (paths == null) Load();
                if (!paths.TryGetValue(n, out var p)) return null;
                if (p.StartsWith("=") && !paths.TryGetValue(p.Substring(1), out p)) return null;
                return p;
            }
        }

        private static void Load()
        {
            paths = new Dictionary<string, string>(16000);
            if (File == null || !System.IO.File.Exists(File)) return;
            using (var fs = System.IO.File.OpenRead(File))
            using (var gz = new GZipStream(fs, CompressionMode.Decompress))
            using (var r = new StreamReader(gz, Encoding.UTF8))
            {
                string line;
                while ((line = r.ReadLine()) != null)
                {
                    int t = line.IndexOf('\t');
                    if (t > 0) paths[line.Substring(0, t)] = line.Substring(t + 1);
                }
            }
        }
    }

    internal static class Svg
    {
        private static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        // #RRGGBBAA / #RGB / names -> fill + fill-opacity attributes SVG understands everywhere.
        public static string Paint(string attr, string color)
        {
            var (hex, a) = Hex(color);
            var s = attr + "=\"" + hex + "\"";
            if (a < 0.999) s += " " + attr + "-opacity=\"" + N(a) + "\"";
            return s;
        }

        public static (string hex, double alpha) Hex(string color)
        {
            string c = (color ?? "#ffffff").Trim().ToLowerInvariant();
            if (c.StartsWith("#")) c = c.Substring(1);
            if (c.Length == 3 || c.Length == 4) c = string.Concat(c.Select(ch => new string(ch, 2)));
            if (c.Length == 8) return ("#" + c.Substring(0, 6), int.Parse(c.Substring(6, 2), NumberStyles.HexNumber) / 255.0);
            if (c.Length == 6) return ("#" + c, 1.0);
            return ("#ffffff", 1.0);
        }

        public static string Icon(string icon, string color)
        {
            var p = Mdi.Path(icon) ?? Mdi.Path("mdi:help-circle-outline");
            if (p == null) return null;
            return "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\"><path " + Paint("fill", color) + " d=\"" + p + "\"/></svg>";
        }

        // A line graph of values over a w x h box. min/max default to the data range (padded a little).
        public static string Graph(IList<double> xs, IList<double> ys, int w, int h, string color, double width, double fillOpacity,
            double? min = null, double? max = null, double? x0 = null, double? x1 = null, bool smooth = false)
        {
            var sb = new StringBuilder();
            sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 ").Append(w).Append(' ').Append(h).Append("\">");
            if (ys.Count >= 2)
            {
                double lo = min ?? ys.Min(), hi = max ?? ys.Max();
                if (hi - lo < 1e-9) { lo -= 1; hi += 1; }
                else if (min == null && max == null) { double pad = (hi - lo) * 0.08; lo -= pad; hi += pad; }
                double a = x0 ?? xs[0], b = x1 ?? xs[xs.Count - 1];
                if (b - a < 1e-9) b = a + 1;
                double inset = width / 2 + 1;
                var pts = new List<string>();
                for (int i = 0; i < ys.Count; i++)
                {
                    double x = (xs[i] - a) / (b - a) * w;
                    double y = inset + (1 - (ys[i] - lo) / (hi - lo)) * (h - 2 * inset);
                    pts.Add(N(Math.Max(0, Math.Min(w, x))) + "," + N(y));
                }
                string line = smooth ? Smooth(pts) : "M" + string.Join(" L", pts);
                if (fillOpacity > 0)
                {
                    var (hex, _) = Hex(color);
                    sb.Append("<path fill=\"").Append(hex).Append("\" fill-opacity=\"").Append(N(fillOpacity)).Append("\" d=\"M")
                      .Append(pts[0].Split(',')[0]).Append(',').Append(h).Append(" L").Append(line.Substring(1))
                      .Append(" L").Append(pts[pts.Count - 1].Split(',')[0]).Append(',').Append(h).Append(" Z\"/>");
                }
                sb.Append("<path fill=\"none\" ").Append(Paint("stroke", color)).Append(" stroke-width=\"").Append(N(width))
                  .Append("\" stroke-linejoin=\"round\" stroke-linecap=\"round\" d=\"").Append(line).Append("\"/>");
            }
            sb.Append("</svg>");
            return sb.ToString();
        }

        // Catmull-Rom through the points, written as cubic Beziers ("M x,y C ...").
        private static string Smooth(List<string> pts)
        {
            var p = pts.Select(s => { var a = s.Split(','); return (x: double.Parse(a[0], CultureInfo.InvariantCulture), y: double.Parse(a[1], CultureInfo.InvariantCulture)); }).ToList();
            var sb = new StringBuilder("M").Append(N(p[0].x)).Append(',').Append(N(p[0].y));
            for (int i = 0; i + 1 < p.Count; i++)
            {
                var p0 = p[Math.Max(0, i - 1)]; var p1 = p[i]; var p2 = p[i + 1]; var p3 = p[Math.Min(p.Count - 1, i + 2)];
                double c1x = p1.x + (p2.x - p0.x) / 6, c1y = p1.y + (p2.y - p0.y) / 6, c2x = p2.x - (p3.x - p1.x) / 6, c2y = p2.y - (p3.y - p1.y) / 6;
                sb.Append(" C").Append(N(c1x)).Append(',').Append(N(c1y)).Append(' ').Append(N(c2x)).Append(',').Append(N(c2y)).Append(' ').Append(N(p2.x)).Append(',').Append(N(p2.y));
            }
            return sb.ToString();
        }

        // A gauge: a 180-degree track with the value arc over it.
        public static string Gauge(double fraction, string color, string track, int size)
        {
            fraction = Math.Max(0, Math.Min(1, fraction));
            double r = 40, cx = 50, cy = 50, sw = 12;
            string Arc(double f)
            {
                double ang = Math.PI * (1 - f);
                double x = cx + r * Math.Cos(ang), y = cy - r * Math.Sin(ang);
                return "M" + N(cx - r) + "," + N(cy) + " A" + N(r) + "," + N(r) + " 0 0,1 " + N(x) + "," + N(y);
            }
            var sb = new StringBuilder("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 100 56\">");
            sb.Append("<path fill=\"none\" ").Append(Paint("stroke", track)).Append(" stroke-width=\"").Append(N(sw)).Append("\" d=\"").Append(Arc(1)).Append("\"/>");
            if (fraction > 0.001)
                sb.Append("<path fill=\"none\" ").Append(Paint("stroke", color)).Append(" stroke-width=\"").Append(N(sw)).Append("\" d=\"").Append(Arc(fraction)).Append("\"/>");
            sb.Append("</svg>");
            return sb.ToString();
        }

        // A 270-degree ring (thermostat / light): track, then the value arc from the start.
        public static string Ring(double fraction, string color, string track, double strokeFrac = 0.09)
        {
            fraction = Math.Max(0, Math.Min(1, fraction));
            double cx = 50, cy = 50, r = 42, sw = 100 * strokeFrac;
            string Arc(double f)
            {
                double a0 = 135 * Math.PI / 180, a1 = (135 + 270 * f) * Math.PI / 180;
                double x0 = cx + r * Math.Cos(a0), y0 = cy + r * Math.Sin(a0), x1 = cx + r * Math.Cos(a1), y1 = cy + r * Math.Sin(a1);
                return "M" + N(x0) + "," + N(y0) + " A" + N(r) + "," + N(r) + " 0 " + (270 * f > 180 ? 1 : 0) + ",1 " + N(x1) + "," + N(y1);
            }
            var sb = new StringBuilder("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 100 100\">");
            sb.Append("<path fill=\"none\" stroke-linecap=\"round\" ").Append(Paint("stroke", track)).Append(" stroke-width=\"").Append(N(sw)).Append("\" d=\"").Append(Arc(1)).Append("\"/>");
            if (fraction > 0.003) sb.Append("<path fill=\"none\" stroke-linecap=\"round\" ").Append(Paint("stroke", color)).Append(" stroke-width=\"").Append(N(sw)).Append("\" d=\"").Append(Arc(fraction)).Append("\"/>");
            return sb.Append("</svg>").ToString();
        }

        // Vertical bars over a w x h box (statistics), values scaled from min(0, lo) to hi.
        public static string Bars(IList<double> vals, int w, int h, string color)
        {
            var sb = new StringBuilder("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 ").Append(w).Append(' ').Append(h).Append("\">");
            if (vals.Count > 0)
            {
                double lo = Math.Min(0, vals.Min()), hi = Math.Max(vals.Max(), lo + 1e-9);
                double bw = (double)w / vals.Count, gap = Math.Max(1, bw * 0.2);
                for (int i = 0; i < vals.Count; i++)
                {
                    double bh = (vals[i] - lo) / (hi - lo) * (h - 2);
                    sb.Append("<rect ").Append(Paint("fill", color)).Append(" x=\"").Append(N(i * bw + gap / 2)).Append("\" y=\"").Append(N(h - bh)).Append("\" width=\"")
                      .Append(N(Math.Max(1, bw - gap))).Append("\" height=\"").Append(N(Math.Max(0, bh))).Append("\" rx=\"").Append(N(Math.Min(3, bw / 4))).Append("\"/>");
                }
            }
            return sb.Append("</svg>").ToString();
        }

        public static string Circle(string color) =>
            "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 100 100\"><circle cx=\"50\" cy=\"50\" r=\"50\" " + Paint("fill", color) + "/></svg>";

        public static string DataUri(string svg) => "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));
    }
}
