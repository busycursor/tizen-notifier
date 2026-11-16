// ambient layout: the view's cards as one list, flowed in fixed-width columns from a corner. Each column fills from the
// origin's edge toward the opposite one; the next column steps sideways away from the origin. Strictly linear: a card
// that doesn't fit the rest of the current column starts the next one, and earlier columns are never backfilled.
// Pure C#. Sizes are CSS px (before scale).
using System;
using System.Collections.Generic;
using System.Linq;

namespace TizenNotifier
{
    internal sealed class FlowItem
    {
        public string Key;                         // place in the config: "badges", "s<section>.c<card>", "c<card>"
        public Dictionary<string, object> Cfg;     // the card as configured
        public Dictionary<string, object> Node;    // built at the column width (null while hidden)
        public double H;
        public bool Hidden;
    }

    internal static class Flow
    {
        public static double ColumnWidth(Ctx c) => Json.N(c.T.Params, "column_width", 360);

        // Every card of the view in config order: badges, section cards by section, then view-level cards.
        public static List<FlowItem> Collect(Ctx c, Dictionary<string, object> view)
        {
            double cw = ColumnWidth(c);
            var items = new List<FlowItem>();
            var badges = Json.A(view, "badges");
            if (badges != null && badges.Count > 0)
            {
                var holder = Ui.Box(0, 0, cw, 1);
                double b = Layout.BadgeBlock(c, holder, badges, cw, 0);
                if (b > 0 && c.T.Recolor != null) Recolor.Card(holder, c.T.Recolor, c.SvgCache, tile: false);
                if (b > 0) { holder["h"] = b; items.Add(new FlowItem { Key = "badges", Cfg = new Dictionary<string, object>(), Node = holder, H = b }); }
            }
            var secs = Json.A(view, "sections") ?? new List<object>();
            bool upward = (Json.S(c.T.Params, "origin") ?? "bottom-left").StartsWith("bottom");
            for (int si = 0; si < secs.Count; si++)
            {
                if (!(secs[si] is Dictionary<string, object> sec) || !c.Visible(sec)) continue;
                var cards = Json.A(sec, "cards") ?? new List<object>();
                var part = new List<FlowItem>();
                for (int ci = 0; ci < cards.Count; ci++)
                    if (cards[ci] is Dictionary<string, object> cfg) Add(c, part, "s" + si + ".c" + ci, cfg, cw);
                // a heading labels its section: it sits above the section's cards, so when columns fill upward it is
                // placed after them
                if (upward) part = part.Where(i => Cards.T(i.Cfg) != "heading").Concat(part.Where(i => Cards.T(i.Cfg) == "heading")).ToList();
                items.AddRange(part);
            }
            var vcards = Json.A(view, "cards") ?? new List<object>();
            for (int ci = 0; ci < vcards.Count; ci++)
                if (vcards[ci] is Dictionary<string, object> cfg) Add(c, items, "c" + ci, cfg, cw);
            return items;
        }

        private static void Add(Ctx c, List<FlowItem> items, string key, Dictionary<string, object> cfg, double cw)
        {
            // a heading with nothing in it takes no space
            if (Cards.T(cfg) == "heading" && string.IsNullOrWhiteSpace(Json.S(cfg, "heading")) && (Json.A(cfg, "badges") ?? new List<object>()).Count == 0) return;
            var (draw, slot) = c.Decide(key, cfg);
            if (slot)
            {
                // reserved: the card's size, measured without contacting anything; an empty box in its place
                double hs = c.Measure(() => { var e = Cards.IgnoringConditions(c, cfg); return e == null ? 0 : Height(c, cfg, e, cw); });
                if (hs > 0) items.Add(new FlowItem { Key = key, Cfg = cfg, Node = Ui.Box(0, 0, cw, hs), H = hs, Hidden = true });
                return;
            }
            if (!draw) return;
            var eff = Cards.Effective(c, cfg);
            if (eff == null) return;
            double h = Height(c, cfg, eff, cw);
            var node = Cards.Draw(c, eff, cw, h);
            if (node == null) return;
            if (c.T.Recolor != null) Recolor.Card(node, c.T.Recolor, c.SvgCache);
            items.Add(new FlowItem { Key = key, Cfg = cfg, Node = node, H = h });
        }

        // A card's natural height at the column width: its grid rows when it has them, else by content.
        public static double Height(Ctx c, Dictionary<string, object> cfg, Dictionary<string, object> eff, double cw)
        {
            var (_, rows) = Cards.Grid(c, cfg, eff);
            return rows > 0 ? rows * Layout.Row + (rows - 1) * Layout.Gap : Cards.Height(c, eff, cw);
        }

        // Position the items into `root` (W x H). Items that don't fit are reported offscreen and left out.
        public static void Place(Ctx c, Dictionary<string, object> root, List<FlowItem> items, double W, double H)
        {
            var p = c.T.Params;
            string origin = Json.S(p, "origin") ?? "bottom-left";
            bool fromBottom = origin.StartsWith("bottom"), fromRight = origin.EndsWith("right");
            double cw = ColumnWidth(c), gap = Json.N(p, "gap", 16), margin = Json.N(p, "margin", 48);
            double avail = H - 2 * margin;
            int col = 0; double used = 0;
            bool full = false;
            int last = items.FindLastIndex(i => !i.Hidden);   // reserved slots after the last shown card give their space back
            for (int ii = 0; ii <= last; ii++)
            {
                var it = items[ii];
                double h = Math.Min(it.H, avail);
                if (!full && used > 0 && used + gap + h > avail) { col++; used = 0; }
                double x = fromRight ? W - margin - cw - col * (cw + gap) : margin + col * (cw + gap);
                if (full || x < margin - 0.5 || x + cw > W - margin + 0.5)
                {
                    full = true;
                    c.S.Offscreen.Add(it.Key + " does not fit (" + Math.Round(it.H) + " tall)");
                    continue;
                }
                double y = fromBottom ? H - margin - used - (used > 0 ? gap : 0) - h : margin + used + (used > 0 ? gap : 0);
                used += (used > 0 ? gap : 0) + h;
                var n = it.Node;
                n["x"] = x; n["y"] = y;
                if (h < it.H)
                {
                    n["h"] = h;
                    var props = Json.O(n, "props") ?? new Dictionary<string, object>();
                    props["ClippingMode"] = "ClipChildren"; n["props"] = props;
                }
                n["_card"] = it.Key;
                Ui.Add(root, n);
            }
        }
    }
}
