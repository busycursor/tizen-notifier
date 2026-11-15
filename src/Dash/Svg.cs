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
            double? min = null, double? max = null, double? x0 = null, double? x1 = null)
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
                if (fillOpacity > 0)
                {
                    var (hex, _) = Hex(color);
                    sb.Append("<path fill=\"").Append(hex).Append("\" fill-opacity=\"").Append(N(fillOpacity)).Append("\" d=\"M")
                      .Append(pts[0].Split(',')[0]).Append(',').Append(h).Append(" L").Append(string.Join(" L", pts))
                      .Append(" L").Append(pts[pts.Count - 1].Split(',')[0]).Append(',').Append(h).Append(" Z\"/>");
                }
                sb.Append("<polyline fill=\"none\" ").Append(Paint("stroke", color)).Append(" stroke-width=\"").Append(N(width))
                  .Append("\" stroke-linejoin=\"round\" stroke-linecap=\"round\" points=\"").Append(string.Join(" ", pts)).Append("\"/>");
            }
            sb.Append("</svg>");
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

        public static string Circle(string color) =>
            "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 100 100\"><circle cx=\"50\" cy=\"50\" r=\"50\" " + Paint("fill", color) + "/></svg>";

        public static string DataUri(string svg) => "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));
    }
}
