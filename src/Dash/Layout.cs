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
        public const double Row = 56, Gap = 8, SecGap = 32, ColMin = 320, ColMax = 500, Pad = 32;   // as the frontend's sections view

        public static Dictionary<string, object> Render(DashSession s, Dictionary<string, object> view)
        {
            var o = s.Eff = Settings.Resolve(s.Opt, s.Config, view, s.Warnings);
            double scale = o.Scale, W = o.W / scale, H = o.H / scale;
            var ctx = new Ctx(s, W);
            Ui.WidthFactor = ctx.T.WidthFactor;
            var root = Ui.Box(0, 0, W, H);
            if (ctx.T.Name == "lovelace" && Json.B(ctx.T.Params, "background", false)) Ui.Add(root, Ui.Box(0, 0, W, H, "#111111"));
            if (ctx.T.Layout == "flow")
            {
                Flow.Place(ctx, root, Flow.Collect(ctx, view), W, H);
                return Finished(s, ctx, root, scale);
            }
            string type = Json.S(view, "type") ?? (Json.Has(view, "sections") ? "sections" : Json.B(view, "panel", false) ? "panel" : "masonry");
            double top = 24;   // the badge row sits 24 px from the top; sections follow below it
            var badges = Json.A(view, "badges");
            if (badges != null && badges.Count > 0)
            {
                double b = BadgeRow(ctx, root, badges, W, top);
                if (b >= 0) top = b + (type == "sections" ? 24 : Gap);
            }
            else top = type == "sections" ? 24 : Pad;
            switch (type)
            {
                case "custom:grid-layout": case "custom:masonry-layout": case "custom:horizontal-layout": case "custom:vertical-layout":
                {
                    var holder = Ui.Box(Pad, top, W - 2 * Pad, 1);
                    var lc = new Dictionary<string, object> { ["layout_type"] = type, ["cards"] = Json.Get(view, "cards"), ["layout"] = Json.Get(view, "layout") };
                    var (lh, _) = Cards.RunLayout(ctx, lc, W - 2 * Pad, holder);
                    holder["h"] = Math.Max(1, lh);
                    Ui.Add(root, holder);
                    break;
                }
                case "sections": Sections(ctx, root, view, W, top); break;
                case "panel": Panel(ctx, root, view, W, H, top); break;
                default: Masonry(ctx, root, view, W, top); break;
            }
            foreach (var k in Json.A(root, "children") ?? new List<object>()) Offscreen(s, k as Dictionary<string, object>, 0, 0, W, H);
            return Finished(s, ctx, root, scale);
        }

        private static Dictionary<string, object> Finished(DashSession s, Ctx ctx, Dictionary<string, object> root, double scale)
        {
            var o = s.Eff;
            s.LastCond = ctx.Cond;
            Appearing(root, s.ShownCond, ctx.Cond);
            Finish(root, scale, "d", ctx.T.Font);
            if (o.Opacity < 1) root["opacity"] = o.Opacity;
            return new Dictionary<string, object>
            {
                ["id"] = "dash:" + s.Opt.Id, ["duration"] = 0.0, ["x"] = o.X, ["y"] = o.Y, ["w"] = o.W, ["h"] = o.H, ["stack"] = false,
                ["level"] = "base", ["enter"] = new Dictionary<string, object> { ["type"] = "fade", ["ms"] = 300.0 }, ["root"] = root,
            };
        }

        // ---------- badges ----------
        public static double BadgeBlock(Ctx c, Dictionary<string, object> root, List<object> badges, double W, double top) => BadgeRow(c, root, badges, W, top);

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
            var all = Json.A(view, "sections") ?? new List<object>();
            var secs = Enumerable.Range(0, all.Count).Where(i => all[i] is Dictionary<string, object> d && c.Visible(d)).Select(i => (sec: (Dictionary<string, object>)all[i], si: i)).ToList();
            if (secs.Count == 0) return;
            int maxCols = (int)Json.N(view, "max_columns", 4);
            int span(Dictionary<string, object> sec) => Math.Max(1, (int)Json.N(sec, "column_span", 1));
            int fit = (int)Math.Floor((W - 2 * Pad + SecGap) / (ColMin + SecGap));
            int cols = Math.Max(1, Math.Min(Math.Min(maxCols, fit), secs.Sum(x => span(x.sec))));
            double colW = Math.Min(ColMax, (W - 2 * Pad - SecGap * (cols - 1)) / cols);
            double total = cols * colW + (cols - 1) * SecGap, left = (W - total) / 2;
            bool dense = Json.B(view, "dense_section_placement", false);

            // rows of the outer grid: each has a per-column "used" flag; row height = tallest section in it
            var rows = new List<(bool[] used, double h, List<(Dictionary<string, object> node, int col)> items)>();
            foreach (var (sec, si) in secs)
            {
                int sp = Math.Min(span(sec), cols);
                double sw = sp * colW + (sp - 1) * SecGap;
                var node = Section(c, sec, si, sw, sp);
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

        // One section: a grid of 12 columns per view column it spans (a 3-wide section has 36). Rows are 56 px at least; a
        // card sized by its content occupies one row that grows to fit it (the frontend's minmax(56px, auto) rows), and a
        // card with a row count spans that many rows. Returns a transparent box holding the cards, or null when empty.
        private static Dictionary<string, object> Section(Ctx c, Dictionary<string, object> sec, int si, double sw, int span)
        {
            var raw = Json.A(sec, "cards") ?? new List<object>();
            int G = 12 * Math.Max(1, span);
            double cell = (sw - (G - 1) * Gap) / G;
            // what to place: drawn cards and reserved slots; slots after the last drawn card give their space back
            var todo = new List<(string key, Dictionary<string, object> cfg, Dictionary<string, object> eff, bool slot)>();
            for (int ci = 0; ci < raw.Count; ci++)
            {
                if (!(raw[ci] is Dictionary<string, object> cfg)) continue;
                string key = "s" + si + ".c" + ci;
                var (draw, slot) = c.Decide(key, cfg);
                if (!draw && !slot) continue;
                var eff = slot ? c.Measure(() => Cards.IgnoringConditions(c, cfg)) : Cards.Effective(c, cfg);
                if (eff != null) todo.Add((key, cfg, eff, slot));
            }
            int lastDrawn = todo.FindLastIndex(t => !t.slot);
            var occ = new List<bool[]>();
            var placed = new List<(Dictionary<string, object> eff, int r, int k, int cols, int rows, double natural, string key, bool slot)>();
            for (int ti = 0; ti <= lastDrawn; ti++)
            {
                var (key, cfg, eff, isSlot) = todo[ti];
                var (gc, gr) = Cards.Grid(c, cfg, eff);
                gc = gc <= 0 ? G : Math.Max(1, Math.Min(G, gc));
                double w = gc * cell + (gc - 1) * Gap;
                int rowsN = gr > 0 ? gr : 1;
                double natural = gr > 0 ? 0 : isSlot ? c.Measure(() => Cards.Height(c, eff, w)) : Cards.Height(c, eff, w);
                int pr = 0, pc = 0;
                for (int r = 0; ; r++)
                {
                    while (occ.Count < r + rowsN) occ.Add(new bool[G]);
                    int found = -1;
                    for (int k = 0; k + gc <= G && found < 0; k++)
                    {
                        bool ok = true;
                        for (int rr = r; rr < r + rowsN && ok; rr++) for (int kk = k; kk < k + gc && ok; kk++) if (occ[rr][kk]) ok = false;
                        if (ok) found = k;
                    }
                    if (found >= 0) { pr = r; pc = found; break; }
                }
                for (int rr = pr; rr < pr + rowsN; rr++) for (int kk = pc; kk < pc + gc; kk++) occ[rr][kk] = true;
                placed.Add((eff, pr, pc, gc, rowsN, natural, key, isSlot));
            }
            if (placed.Count == 0) return null;
            int nRows = placed.Max(p => p.r + p.rows);
            var rowH = Enumerable.Repeat(Row, nRows).ToArray();
            foreach (var p in placed) if (p.natural > 0) rowH[p.r] = Math.Max(rowH[p.r], p.natural);
            var rowY = new double[nRows]; double y = 0;
            for (int r = 0; r < nRows; r++) { rowY[r] = y; y += rowH[r] + Gap; }
            var box = Ui.Box(0, 0, sw, 1);
            double bottom = 0;
            foreach (var p in placed)
            {
                double w = p.cols * cell + (p.cols - 1) * Gap;
                double h = p.natural > 0 ? p.natural : Enumerable.Range(p.r, p.rows).Sum(r => rowH[r]) + (p.rows - 1) * Gap;
                var node = p.slot ? Ui.Box(0, 0, w, h) : Cards.Draw(c, p.eff, w, h);
                if (node == null) continue;
                node["x"] = p.k * (cell + Gap); node["y"] = rowY[p.r];
                node["_card"] = p.key;
                Ui.Add(box, node);
                bottom = Math.Max(bottom, rowY[p.r] + h);
            }
            if (bottom <= 0) return null;
            box["h"] = bottom;
            return box;
        }

        // ---------- masonry / panel ----------
        private static void Masonry(Ctx c, Dictionary<string, object> root, Dictionary<string, object> view, double W, double top)
        {
            var raw = Json.A(view, "cards") ?? new List<object>();
            int n = Math.Max(1, Math.Min((int)Json.N(view, "max_columns", 4), (int)Math.Floor(W / 500)));
            double colW = Math.Min(492, (W - 2 * Pad - Gap * (n - 1)) / n);
            double left = (W - (n * colW + (n - 1) * Gap)) / 2;
            var heights = new double[n];
            var sizes = new double[n];
            var todo = new List<(string key, Dictionary<string, object> eff, bool slot)>();
            for (int ci = 0; ci < raw.Count; ci++)
            {
                if (!(raw[ci] is Dictionary<string, object> cfg)) continue;
                var (draw, slot) = c.Decide("c" + ci, cfg);
                if (!draw && !slot) continue;
                var e = slot ? c.Measure(() => Cards.IgnoringConditions(c, cfg)) : Cards.Effective(c, cfg);
                if (e != null) todo.Add(("c" + ci, e, slot));
            }
            int lastDrawn = todo.FindLastIndex(t => !t.slot);
            for (int ti = 0; ti <= lastDrawn; ti++)
            {
                var (key, eff, isSlot) = todo[ti];
                double h = isSlot ? c.Measure(() => Cards.Height(c, eff, colW)) : Cards.Height(c, eff, colW);
                var node = isSlot ? Ui.Box(0, 0, colW, h) : Cards.Draw(c, eff, colW, h);
                if (node == null) continue;
                node["_card"] = key;
                // the frontend: the first column whose size is under 5 units, else the smallest (size ~ 50 px per unit;
                // picture cards count 3 like the frontend's own estimate)
                string ct = Cards.T(eff);
                double units = ct.StartsWith("picture") || ct == "camera" ? 3 : Math.Ceiling(h / 50);
                int col = 0;
                for (int i = 0; i < n; i++) { if (sizes[i] < 5) { col = i; break; } if (sizes[i] < sizes[col]) col = i; }
                sizes[col] += units;
                node["x"] = left + col * (colW + Gap); node["y"] = top + heights[col];
                heights[col] += h + Gap;
                Ui.Add(root, node);
            }
        }

        private static void Panel(Ctx c, Dictionary<string, object> root, Dictionary<string, object> view, double W, double H, double top)
        {
            var cfg = (Json.A(view, "cards") ?? new List<object>()).OfType<Dictionary<string, object>>().FirstOrDefault();
            if (cfg == null || !c.Decide("c0", cfg).draw) return;
            var eff = Cards.Effective(c, cfg);
            if (eff == null) return;
            var node = Cards.Draw(c, eff, W - 2 * Pad, H - top - Pad);
            if (node == null) return;
            node["x"] = Pad; node["y"] = top;
            node["_card"] = "c0";
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

        // A conditional card shown now that was hidden in what is on screen fades in (reserve and slide; instant just appears,
        // crossfade fades the whole overlay).
        private static void Appearing(Dictionary<string, object> n, Dictionary<string, (bool visible, string mode)> shown, Dictionary<string, (bool visible, string mode)> now)
        {
            if (shown == null) return;
            if (n.TryGetValue("_card", out var k) && k is string key && now.TryGetValue(key, out var cur) && cur.visible
                && (cur.mode == "reserve" || cur.mode == "slide") && (!shown.TryGetValue(key, out var was) || !was.visible))
            {
                n["animate"] = new List<object> { new Dictionary<string, object> { ["prop"] = "opacity", ["from"] = 0.0, ["to"] = 1.0, ["ms"] = 400.0 } };
                return;
            }
            if (Json.A(n, "children") is List<object> kids) foreach (var c in kids) if (c is Dictionary<string, object> cd) Appearing(cd, shown, now);
        }

        // ---------- finish: CSS px -> TV px, stable names ----------
        private static readonly string[] Scaled = { "x", "y", "w", "h", "radius", "pixel_size", "gap", "line_spacing" };

        private static void Finish(Dictionary<string, object> n, double scale, string name, string font)
        {
            // a placed card is named by its place in the config, so its name survives cards around it appearing
            if (n.TryGetValue("_card", out var key)) { name = "d." + key; n.Remove("_card"); }
            n["name"] = name;
            if (font != null && Json.S(n, "type") == "text") n["font"] = font;
            foreach (var k in Scaled) if (n.TryGetValue(k, out var v) && v is double d) n[k] = Math.Round(d * scale);
            if (Json.Get(n, "children") is List<object> kids)
                for (int i = 0; i < kids.Count; i++) if (kids[i] is Dictionary<string, object> kd) Finish(kd, scale, name + "." + i, font);
        }
    }

    // What every card renderer gets: the session (data), the palette and the view width (for screen conditions).
    internal sealed class Ctx
    {
        public readonly DashSession S;
        public readonly DashTheme T;
        public Palette P => T.P;
        public readonly double ViewW;
        public readonly Dictionary<string, string> SvgCache = new Dictionary<string, string>();   // recolored SVGs, this render
        public Ctx(DashSession s, double viewW) { S = s; ViewW = viewW; T = DashTheme.From(s.Eff); }
        public bool Visible(Dictionary<string, object> cfg) => Conditions.All(S, Json.Get(cfg, "visibility"), ViewW);

        // ---- conditional cards (visibility rules or the conditional card) ----
        // what each one did this render, by card key: shown or not, and its mode (instant | reserve | crossfade | slide)
        public readonly Dictionary<string, (bool visible, string mode)> Cond = new Dictionary<string, (bool, string)>();
        public static bool HasConditions(Dictionary<string, object> cfg) => Json.Has(cfg, "visibility") || Cards.T(cfg) == "conditional";
        public string CondMode(Dictionary<string, object> cfg)
        {
            var m = Json.S(Json.O(cfg, Settings.Key), "conditional");
            if (m != null && !Settings.CondModes.Contains(m)) { S.Warnings.Add(Settings.Key + ".conditional: unknown mode '" + m + "'"); m = null; }
            return m ?? S.Eff.Conditionals;
        }
        public bool CondVisible(Dictionary<string, object> cfg)
        {
            if (!Visible(cfg)) return false;
            if (Cards.T(cfg) != "conditional") return true;
            var inner = Json.O(cfg, "card");
            return Conditions.All(S, Json.Get(cfg, "conditions"), ViewW) && (inner == null || Visible(inner));
        }

        // A card's decision for this render: (draw it, keep an empty slot for it, or neither). Records it in Cond.
        public (bool draw, bool slot) Decide(string key, Dictionary<string, object> cfg)
        {
            if (!HasConditions(cfg)) return (true, false);
            string mode = CondMode(cfg);
            bool vis = CondVisible(cfg);
            Cond[key] = (vis, mode);
            return (vis, !vis && mode == "reserve");
        }

        // Size of a hidden card without asking HA or the relay for anything (data getters return what is cached).
        public T Measure<T>(Func<T> f)
        {
            S.Measuring = true;
            try { return f(); }
            finally { S.Measuring = false; }
        }
        public Ent E(Dictionary<string, object> cfg) => S.State(Json.S(cfg, "entity"));
        public void Unsupported(string what) { S.Unsupported.Add(what); }
    }
}
