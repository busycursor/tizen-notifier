// Lovelace view layout -> one node tree in the notifier's /notify schema. Pure C#.
//   sections: columns of 320..500 CSS px (gap 32), max_columns, column_span; inside each section a 12-column grid,
//             56 px rows, 8 px gaps, dense placement; view badges in a centered row above.
//   masonry:  columns by width, each card to the shortest column.
//   panel:    the first card fills the view.
using System;
using System.Collections.Generic;
using System.Linq;

namespace TizenNotifier
{
    internal static class Layout
    {
        public const double Row = 56, Gap = 8, SecGap = 32, ColMin = 320, ColMax = 500, Pad = 16;

        public static Dictionary<string, object> Render(DashSession s, Dictionary<string, object> view)
        {
            var o = s.Opt;
            double scale = o.Scale, W = o.W / scale, H = o.H / scale;
            var ctx = new Ctx(s, W);
            var root = Ui.Box(0, 0, W, H);
            string type = Json.S(view, "type") ?? (Json.Has(view, "sections") ? "sections" : Json.B(view, "panel", false) ? "panel" : "masonry");
            double top = Pad;
            var badges = Json.A(view, "badges");
            if (badges != null && badges.Count > 0)
            {
                double b = BadgeRow(ctx, root, badges, W, top);
                if (b >= 0) top = b + (type == "sections" ? 16 : Gap);
            }
            switch (type)
            {
                case "sections": Sections(ctx, root, view, W, top); break;
                case "panel": Panel(ctx, root, view, W, H, top); break;
                default: Masonry(ctx, root, view, W, top); break;
            }
            foreach (var k in Json.A(root, "children") ?? new List<object>()) Offscreen(s, k as Dictionary<string, object>, 0, 0, W, H);
            Finish(root, scale, "d");
            return new Dictionary<string, object>
            {
                ["id"] = "dash:" + o.Id, ["duration"] = 0.0, ["x"] = o.X, ["y"] = o.Y, ["w"] = o.W, ["h"] = o.H, ["stack"] = false,
                ["level"] = "base", ["enter"] = new Dictionary<string, object> { ["type"] = "fade", ["ms"] = 300.0 }, ["root"] = root,
            };
        }

        // ---------- badges ----------
        private static double BadgeRow(Ctx c, Dictionary<string, object> root, List<object> badges, double W, double top)
        {
            var built = new List<Dictionary<string, object>>();
            foreach (var b in badges)
            {
                var cfg = b as Dictionary<string, object>;
                if (b is string bs) cfg = new Dictionary<string, object> { ["type"] = "entity", ["entity"] = bs };
                if (cfg == null || !c.Visible(cfg)) continue;
                var n = Badges.Draw(c, cfg);
                if (n != null) built.Add(n);
            }
            if (built.Count == 0) return -1;
            // wrap into centered rows
            double y = top, rowH = 0;
            var row = new List<Dictionary<string, object>>();
            double rowW = 0;
            void Flush()
            {
                double x = (W - rowW) / 2;
                foreach (var n in row) { n["x"] = x; n["y"] = y + (rowH - (double)n["h"]) / 2; x += (double)n["w"] + Gap; Ui.Add(root, n); }
                y += rowH + Gap; row.Clear(); rowW = 0; rowH = 0;
            }
            foreach (var n in built)
            {
                double w = (double)n["w"];
                if (row.Count > 0 && rowW + Gap + w > W - 2 * Pad) Flush();
                rowW += (row.Count > 0 ? Gap : 0) + w; rowH = Math.Max(rowH, (double)n["h"]); row.Add(n);
            }
            if (row.Count > 0) Flush();
            return y - Gap;
        }

        // ---------- sections ----------
        private static void Sections(Ctx c, Dictionary<string, object> root, Dictionary<string, object> view, double W, double top)
        {
            var secs = (Json.A(view, "sections") ?? new List<object>()).OfType<Dictionary<string, object>>().Where(c.Visible).ToList();
            if (secs.Count == 0) return;
            int maxCols = (int)Json.N(view, "max_columns", 4);
            int span(Dictionary<string, object> sec) => Math.Max(1, (int)Json.N(sec, "column_span", 1));
            int fit = (int)Math.Floor((W - 2 * Pad + SecGap) / (ColMin + SecGap));
            int cols = Math.Max(1, Math.Min(Math.Min(maxCols, fit), secs.Sum(span)));
            double colW = Math.Min(ColMax, (W - 2 * Pad - SecGap * (cols - 1)) / cols);
            double total = cols * colW + (cols - 1) * SecGap, left = (W - total) / 2;
            bool dense = Json.B(view, "dense_section_placement", false);

            // rows of the outer grid: each has a per-column "used" flag; row height = tallest section in it
            var rows = new List<(bool[] used, double h, List<(Dictionary<string, object> node, int col)> items)>();
            foreach (var sec in secs)
            {
                int sp = Math.Min(span(sec), cols);
                double sw = sp * colW + (sp - 1) * SecGap;
                var node = Section(c, sec, sw);
                if (node == null) continue;
                double sh = (double)node["h"];
                int ri = -1, ci = -1;
                for (int r = dense ? 0 : Math.Max(0, rows.Count - 1); r < rows.Count && ri < 0; r++)
                    for (int k = 0; k + sp <= cols; k++)
                        if (Enumerable.Range(k, sp).All(i => !rows[r].used[i]))
                        {
                            // in non-dense mode only fill to the right of what's already in the last row
                            if (!dense && Enumerable.Range(k, cols - k).Any(i => rows[r].used[i])) continue;
                            ri = r; ci = k; break;
                        }
                if (ri < 0) { rows.Add((new bool[cols], 0, new List<(Dictionary<string, object>, int)>())); ri = rows.Count - 1; ci = 0; }
                var row = rows[ri];
                for (int i = ci; i < ci + sp; i++) row.used[i] = true;
                row.items.Add((node, ci));
                rows[ri] = (row.used, Math.Max(row.h, sh), row.items);
            }
            double y = top;
            foreach (var row in rows)
            {
                foreach (var (node, col) in row.items) { node["x"] = left + col * (colW + SecGap); node["y"] = y; Ui.Add(root, node); }
                y += row.h + SecGap;
            }
        }

        // One section: a 12-column grid. Returns a transparent box holding its cards, or null when it has none.
        private static Dictionary<string, object> Section(Ctx c, Dictionary<string, object> sec, double sw)
        {
            var cards = (Json.A(sec, "cards") ?? new List<object>()).OfType<Dictionary<string, object>>().ToList();
            double cell = (sw - 11 * Gap) / 12;
            var occ = new List<bool[]>();
            var box = Ui.Box(0, 0, sw, 1);
            double bottom = 0;
            foreach (var cfg in cards)
            {
                if (!c.Visible(cfg)) continue;
                var eff = Cards.Effective(c, cfg);
                if (eff == null) continue;
                var (gc, gr) = Cards.Grid(c, cfg, eff);
                gc = Math.Max(1, Math.Min(12, gc));
                double w = gc * cell + (gc - 1) * Gap;
                int rowsN = gr > 0 ? gr : Math.Max(1, (int)Math.Ceiling((Cards.Height(c, eff, w) + Gap) / (Row + Gap)));
                double h = rowsN * Row + (rowsN - 1) * Gap;
                // first free spot, row-major (CSS grid dense)
                int pr = 0, pc = 0;
                for (int r = 0; ; r++)
                {
                    while (occ.Count < r + rowsN) occ.Add(new bool[12]);
                    int found = -1;
                    for (int k = 0; k + gc <= 12 && found < 0; k++)
                    {
                        bool ok = true;
                        for (int rr = r; rr < r + rowsN && ok; rr++) for (int kk = k; kk < k + gc && ok; kk++) if (occ[rr][kk]) ok = false;
                        if (ok) found = k;
                    }
                    if (found >= 0) { pr = r; pc = found; break; }
                }
                for (int rr = pr; rr < pr + rowsN; rr++) for (int kk = pc; kk < pc + gc; kk++) occ[rr][kk] = true;
                var node = Cards.Draw(c, eff, w, h);
                if (node == null) continue;
                node["x"] = pc * (cell + Gap); node["y"] = pr * (Row + Gap);
                Ui.Add(box, node);
                bottom = Math.Max(bottom, pr * (Row + Gap) + h);
            }
            if (bottom <= 0) return null;
            box["h"] = bottom;
            return box;
        }

        // ---------- masonry / panel ----------
        private static void Masonry(Ctx c, Dictionary<string, object> root, Dictionary<string, object> view, double W, double top)
        {
            var cards = (Json.A(view, "cards") ?? new List<object>()).OfType<Dictionary<string, object>>().ToList();
            int n = Math.Max(1, Math.Min(4, (int)Math.Floor((W - 2 * Pad) / 350)));
            double colW = (W - 2 * Pad - Gap * (n - 1)) / n;
            var heights = new double[n];
            foreach (var cfg in cards)
            {
                if (!c.Visible(cfg)) continue;
                var eff = Cards.Effective(c, cfg);
                if (eff == null) continue;
                double h = Cards.Height(c, eff, colW);
                var node = Cards.Draw(c, eff, colW, h);
                if (node == null) continue;
                int col = Array.IndexOf(heights, heights.Min());
                node["x"] = Pad + col * (colW + Gap); node["y"] = top + heights[col];
                heights[col] += h + Gap;
                Ui.Add(root, node);
            }
        }

        private static void Panel(Ctx c, Dictionary<string, object> root, Dictionary<string, object> view, double W, double H, double top)
        {
            var cfg = (Json.A(view, "cards") ?? new List<object>()).OfType<Dictionary<string, object>>().FirstOrDefault();
            if (cfg == null || !c.Visible(cfg)) return;
            var eff = Cards.Effective(c, cfg);
            if (eff == null) return;
            var node = Cards.Draw(c, eff, W - 2 * Pad, H - top - Pad);
            if (node == null) return;
            node["x"] = Pad; node["y"] = top;
            Ui.Add(root, node);
        }

        // Top-level sections / cards that don't fit the region (CSS px): reported in the session status.
        private static void Offscreen(DashSession s, Dictionary<string, object> n, double ox, double oy, double W, double H)
        {
            if (n == null) return;
            double x = ox + Json.N(n, "x", 0), y = oy + Json.N(n, "y", 0), w = Json.N(n, "w", 0), h = Json.N(n, "h", 0);
            if (y + h <= H + 1 && x + w <= W + 1) return;
            if (Json.A(n, "children") is List<object> kids && Json.S(n, "bg") == null)   // a section holder: check its cards
                foreach (var k in kids) Offscreen(s, k as Dictionary<string, object>, x, y, W, H);
            else s.Offscreen.Add((y >= H ? "below the screen" : "cut off at the bottom") + " at y=" + Math.Round(y) + " (" + Math.Round(h) + " tall)");
        }

        // ---------- finish: CSS px -> TV px, stable names ----------
        private static readonly string[] Scaled = { "x", "y", "w", "h", "radius", "pixel_size", "gap", "line_spacing" };

        private static void Finish(Dictionary<string, object> n, double scale, string name)
        {
            n["name"] = name;
            foreach (var k in Scaled) if (n.TryGetValue(k, out var v) && v is double d) n[k] = Math.Round(d * scale);
            if (Json.Get(n, "children") is List<object> kids)
                for (int i = 0; i < kids.Count; i++) if (kids[i] is Dictionary<string, object> kd) Finish(kd, scale, name + "." + i);
        }
    }

    // What every card renderer gets: the session (data), the palette and the view width (for screen conditions).
    internal sealed class Ctx
    {
        public readonly DashSession S;
        public readonly Palette P;
        public readonly double ViewW;
        public Ctx(DashSession s, double viewW) { S = s; ViewW = viewW; P = Palette.From(s.Opt.Theme); }
        public bool Visible(Dictionary<string, object> cfg) => Conditions.All(S, Json.Get(cfg, "visibility"), ViewW);
        public Ent E(Dictionary<string, object> cfg) => S.State(Json.S(cfg, "entity"));
        public void Unsupported(string what) { S.Unsupported.Add(what); }
    }
}
