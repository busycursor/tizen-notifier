// Lovelace card translators: card config + live data -> notifier nodes. Pure C#.
// Read-only by design: tap/hold actions are ignored and action-only cards are dropped.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace TizenNotifier
{
    internal static partial class Cards
    {
        private static readonly HashSet<string> ActionDomains = new HashSet<string> { "button", "input_button", "script", "scene", "automation" };

        private static readonly HashSet<string> Known = new HashSet<string>
        {
            "tile", "entity", "entities", "glance", "button", "sensor", "gauge", "history-graph", "statistics-graph", "markdown", "heading",
            "todo-list", "calendar", "weather-forecast", "clock", "picture", "picture-entity", "picture-glance", "camera",
            "horizontal-stack", "vertical-stack", "grid", "conditional", "entity-filter",
            "custom:mushroom-template-card", "custom:mushroom-entity-card", "custom:mushroom-title-card", "custom:mushroom-chips-card",
            "custom:mushroom-light-card", "custom:mushroom-person-card", "custom:mushroom-lock-card", "custom:mushroom-cover-card",
            "custom:mushroom-climate-card", "custom:mushroom-fan-card", "custom:mushroom-media-player-card",
            "custom:better-moment-card", "custom:advanced-camera-card", "custom:frigate-card",
        };

        public static string T(Dictionary<string, object> cfg) => (Json.S(cfg, "type") ?? "").ToLowerInvariant();

        // The card to actually draw: unwraps `conditional`, drops action-only and unsupported cards (null = nothing).
        public static Dictionary<string, object> Effective(Ctx c, Dictionary<string, object> cfg)
        {
            var tr = Translate(c, cfg);
            if (tr == null) return null;
            if (!ReferenceEquals(tr, cfg)) return Effective(c, tr);
            string t = T(cfg);
            if (t == "conditional")
            {
                if (!Conditions.All(c.S, Json.Get(cfg, "conditions"), c.ViewW)) return null;
                var inner = Json.O(cfg, "card");
                if (inner == null || !c.Visible(inner)) return null;
                inner = new Dictionary<string, object>(inner);
                if (!Json.Has(inner, "grid_options") && Json.Has(cfg, "grid_options")) inner["grid_options"] = cfg["grid_options"];
                return Effective(c, inner);
            }
            if (!Known.Contains(t) && !HasMore(t) && !HasCustom(t)) { c.Unsupported(t); return null; }
            if ((t == "button" || t == "tile" || t == "entity" || t.StartsWith("custom:mushroom-entity")) && Json.S(cfg, "entity") is string eid && ActionDomains.Contains(eid.Split('.')[0])) return null;
            if (t == "button" && Json.S(cfg, "entity") == null && Json.S(cfg, "name") == null && Json.S(cfg, "icon") == null) return null;
            return cfg;
        }

        // The card a conditional/visibility card would draw if its conditions held (for measuring a reserved slot).
        public static Dictionary<string, object> IgnoringConditions(Ctx c, Dictionary<string, object> cfg)
        {
            if (T(cfg) == "conditional")
            {
                var inner = Json.O(cfg, "card");
                if (inner == null) return null;
                inner = new Dictionary<string, object>(inner);
                inner.Remove("visibility");
                if (!Json.Has(inner, "grid_options") && Json.Has(cfg, "grid_options")) inner["grid_options"] = cfg["grid_options"];
                return IgnoringConditions(c, inner);
            }
            return Effective(c, cfg);
        }

        // Size in a sections grid: columns of 12, rows (0 = by content). grid_options, then legacy layout_options, then type defaults.
        public static (int cols, int rows) Grid(Ctx c, Dictionary<string, object> outer, Dictionary<string, object> cfg)
        {
            var go = Json.O(cfg, "grid_options") ?? Json.O(outer, "grid_options");
            var lo = Json.O(cfg, "layout_options") ?? Json.O(outer, "layout_options");
            var (dc, dr) = Default(c, cfg);
            int cols = dc, rows = dr;
            object gcol = Json.Get(go, "columns") ?? (Json.Get(lo, "grid_columns") is double lc ? (object)(lc * 3) : Json.Get(lo, "grid_columns"));
            object grow = Json.Get(go, "rows") ?? Json.Get(lo, "grid_rows");
            if (gcol is double gd) cols = (int)gd; else if (gcol as string == "full") cols = 0;   // 0 = full width
            if (grow is double rd) rows = (int)rd; else if (grow as string == "auto") rows = 0;
            return (cols, rows);
        }

        private static (int, int) Default(Ctx c, Dictionary<string, object> cfg)
        {
            var more = DefaultMore(cfg) ?? DefaultCustom(cfg);
            if (more != null) return more.Value;
            switch (T(cfg))
            {
                case "tile": return (6, Json.B(cfg, "vertical", false) ? 2 : 1);
                case "button": return (6, 2);
                case "entity": case "sensor": return (6, 2);
                case "gauge": return (6, 3);
                case "clock":
                    var cs = Json.S(cfg, "clock_size");
                    return (6, cs == "large" ? 3 : cs == "medium" ? 2 : 1);
                case "custom:mushroom-template-card": case "custom:mushroom-entity-card": case "custom:mushroom-light-card": case "custom:mushroom-person-card":
                case "custom:mushroom-lock-card": case "custom:mushroom-cover-card": case "custom:mushroom-climate-card": case "custom:mushroom-fan-card":
                case "custom:mushroom-media-player-card":
                    return (6, Json.S(cfg, "layout") == "vertical" ? 2 : 1);
            }
            return (0, 0);   // full width, height by content
        }

        // ---------- measuring (content-sized cards) ----------

        public static double Height(Ctx c, Dictionary<string, object> cfg, double w)
        {
            var P = c.P;
            var hm = HeightMore(c, cfg, w) ?? HeightCustom(c, cfg, w);
            if (hm != null) return hm.Value;
            switch (T(cfg))
            {
                case "tile": return Json.B(cfg, "vertical", false) ? 120 : 56;
                case "button": return 120;
                case "entity": case "sensor": return 120;
                case "gauge": return 184;
                case "clock": return Json.S(cfg, "clock_size") == "large" ? 184 : Json.S(cfg, "clock_size") == "medium" ? 120 : 56;
                case "entities":
                {
                    double h = 2 * P.Pad + (Json.S(cfg, "title") != null ? 40 : 0);
                    foreach (var row in Rows(c, cfg)) h += RowHeight(row);
                    return h;
                }
                case "entity-filter": return Height(c, FilterAsCard(c, cfg), w);
                case "glance":
                {
                    int n = Glance(c, cfg).Count, cols = Math.Max(1, (int)Json.N(cfg, "columns", Math.Min(5, Math.Max(1, n))));
                    int rows = Math.Max(1, (int)Math.Ceiling(n / (double)cols));
                    return 2 * P.Pad + (Json.S(cfg, "title") != null ? 40 : 0) + rows * 76;
                }
                case "markdown":
                {
                    double h = 32 + (Json.S(cfg, "title") != null ? 48 : 0);
                    foreach (var b in Markdown(c, cfg)) h += Ui.Lines(Plain(b.text), b.px, w - 32) * b.px * 1.5 + b.after;
                    return h;
                }
                case "heading": return Json.S(cfg, "heading_style") == "subtitle" ? 32 : 40;
                case "todo-list":
                {
                    var (open, done) = TodoItems(c, cfg);
                    double h = 2 * P.Pad + (Json.S(cfg, "title") != null ? 40 : 0) + Math.Max(1, open.Count) * 36;
                    if (done.Count > 0) h += 36 + done.Count * 36;
                    return h;
                }
                case "calendar":
                {
                    var days = Agenda(c, cfg);
                    double h = 2 * P.Pad + (Json.S(cfg, "title") != null ? 40 : 0);
                    if (days.Count == 0) return h + 32;
                    foreach (var d in days) h += 30 + d.Value.Count * 28 + 6;
                    return h;
                }
                case "weather-forecast":
                {
                    double h = 2 * P.Pad;
                    if (Json.B(cfg, "show_current", true)) h += 72;
                    if (Json.B(cfg, "show_forecast", true)) h += 104;
                    return h;
                }
                case "history-graph":
                    return 2 * P.Pad + (Json.S(cfg, "title") != null ? 40 : 0) + 160 + 28;
                case "statistics-graph":
                    return 2 * P.Pad + (Json.S(cfg, "title") != null ? 40 : 0) + 14 + 174 + 52;
                case "picture": case "picture-entity": case "picture-glance": case "camera": case "custom:advanced-camera-card": case "custom:frigate-card":
                    return w / Aspect(cfg);
                case "horizontal-stack":
                {
                    var kids = Kids(c, cfg);
                    if (kids.Count == 0) return 0;
                    double cw = (w - Layout.Gap * (kids.Count - 1)) / kids.Count;
                    return kids.Max(k => Height(c, k, cw));
                }
                case "vertical-stack":
                {
                    var kids = Kids(c, cfg);
                    return kids.Sum(k => Height(c, k, w)) + Math.Max(0, kids.Count - 1) * Layout.Gap;
                }
                case "grid":
                {
                    var kids = Kids(c, cfg);
                    int cols = Math.Max(1, (int)Json.N(cfg, "columns", 3));
                    double cw = (w - Layout.Gap * (cols - 1)) / cols;
                    int rows = (int)Math.Ceiling(kids.Count / (double)cols);
                    double rh = Json.B(cfg, "square", true) ? cw : kids.Count == 0 ? 0 : kids.Max(k => Height(c, k, cw));
                    return (Json.S(cfg, "title") != null ? 40 : 0) + rows * rh + Math.Max(0, rows - 1) * Layout.Gap;
                }
                case "custom:mushroom-title-card":
                    return 2 * P.Pad + (Json.S(cfg, "title") != null ? 36 : 0) + (Json.S(cfg, "subtitle") != null ? 24 : 0);
                case "custom:mushroom-chips-card": return 40;
                case "custom:better-moment-card": return Math.Max(1, (Json.A(cfg, "moment") ?? new List<object>()).Count) * 40;
            }
            if (T(cfg).StartsWith("custom:mushroom-")) return Json.S(cfg, "layout") == "vertical" ? 120 : 56;
            return 56;
        }

        private static List<Dictionary<string, object>> Kids(Ctx c, Dictionary<string, object> cfg) =>
            (Json.A(cfg, "cards") ?? new List<object>()).OfType<Dictionary<string, object>>().Where(c.Visible)
                .Select(k => Effective(c, k)).Where(k => k != null).ToList();

        private static double Aspect(Dictionary<string, object> cfg)
        {
            string ar = Json.S(cfg, "aspect_ratio") ?? Json.S(Json.O(cfg, "dimensions"), "aspect_ratio");
            if (ar != null)
            {
                ar = ar.Trim().TrimEnd('%');
                var p = ar.Split(':', 'x', '/');
                if (p.Length == 2 && double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var a) && double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var b) && b > 0) return a / b;
                if (double.TryParse(ar, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct) && pct > 0) return 100 / pct;   // "56.25%"
            }
            return 16.0 / 9;
        }

        // ---------- drawing ----------

        public static Dictionary<string, object> Draw(Ctx c, Dictionary<string, object> cfg, double w, double h)
        {
            var dm = DrawMore(c, cfg, w, h) ?? DrawCustom(c, cfg, w, h);
            if (dm != null) { c.S.CardsDrawn++; return dm; }
            Dictionary<string, object> n;
            switch (T(cfg))
            {
                case "tile": n = Tile(c, cfg, w, h); break;
                case "button": n = Button(c, cfg, w, h); break;
                case "entity": n = EntityCard(c, cfg, w, h, false); break;
                case "sensor": n = EntityCard(c, cfg, w, h, true); break;
                case "gauge": n = Gauge(c, cfg, w, h); break;
                case "entities": n = Entities(c, cfg, w, h); break;
                case "entity-filter": n = Draw(c, FilterAsCard(c, cfg), w, h); return n;
                case "glance": n = GlanceCard(c, cfg, w, h); break;
                case "markdown": n = MarkdownCard(c, cfg, w, h); break;
                case "heading": n = Heading(c, cfg, w, h); break;
                case "todo-list": n = Todo(c, cfg, w, h); break;
                case "calendar": n = Calendar(c, cfg, w, h); break;
                case "weather-forecast": n = Weather(c, cfg, w, h); break;
                case "clock": n = Clock(c, cfg, w, h); break;
                case "history-graph": case "statistics-graph": n = HistoryGraph(c, cfg, w, h); break;
                case "picture": case "picture-entity": case "picture-glance": case "camera": n = Picture(c, cfg, w, h); break;
                case "custom:advanced-camera-card": case "custom:frigate-card": n = AdvancedCamera(c, cfg, w, h); break;
                case "horizontal-stack": case "vertical-stack": case "grid": return Stack(c, cfg, w, h);
                case "custom:mushroom-title-card": n = MushroomTitle(c, cfg, w, h); break;
                case "custom:mushroom-chips-card": return Chips(c, cfg, w, h);
                case "custom:better-moment-card": n = Moment(c, cfg, w, h, false); break;
                default:
                    if (T(cfg).StartsWith("custom:mushroom-")) { n = Mushroom(c, cfg, w, h); break; }
                    return null;
            }
            if (n != null) c.S.CardsDrawn++;
            return n;
        }

        private static Dictionary<string, object> Card(Ctx c, double w, double h) => Ui.Box(0, 0, w, h, c.P.CardBg, c.P.Radius);

        private static Dictionary<string, object> Title(Ctx c, Dictionary<string, object> card, string title, double w, ref double y)
        {
            if (string.IsNullOrEmpty(title)) return card;
            Ui.Add(card, Ui.Text(c.P.Pad + 4, y, w - 2 * c.P.Pad - 8, 32, title, 20, c.P.Text, "medium"));
            y += 40;
            return card;
        }

        // Icon color for an entity under a card's `color` / state_color rules.
        private static string IconColor(Ctx c, Ent e, string cardColor, bool stateColor = true)
        {
            var cc = Theme.Color(cardColor);
            if (cc != null && (e == null || Format.Active(e))) return cc;
            if (!stateColor) return c.P.Accent;
            return Format.StateColor(e) ?? c.P.Inactive;
        }

        // ---- tile ----
        private static Dictionary<string, object> Tile(Ctx c, Dictionary<string, object> cfg, double w, double h)
        {
            var P = c.P; var e = c.E(cfg); var card = Card(c, w, h);
            string name = Format.Name(e, Json.S(cfg, "name"));
            string state = Json.B(cfg, "hide_state", false) ? null : StateContent(c, e, Json.Get(cfg, "state_content"));
            string icon = Format.Icon(e, Json.S(cfg, "icon"), c.S.Icons);
            string col = IconColor(c, e, Json.S(cfg, "color"));
            bool active = Format.Active(e);
            string pic = Json.B(cfg, "show_entity_picture", false) ? Picture(c, e) : null;
            if (Json.B(cfg, "vertical", false))
            {
                double s = 40;
                Ui.Add(card, pic != null ? Ui.Image((w - s) / 2, 14, s, s, pic, s / 2) : Ui.IconBadge((w - s) / 2, 14, s, icon, col, active));
                Ui.Add(card, Ui.Text(8, 60, w - 16, 22, name, 14, P.Text, "medium", "center"));
                if (state != null) Ui.Add(card, Ui.Text(8, 82, w - 16, 20, state, 12, P.Secondary, null, "center"));
                return card;
            }
            double ib = 36, iy = (Math.Min(h, 56) - ib) / 2;
            Ui.Add(card, pic != null ? Ui.Image(10, iy, ib, ib, pic, ib / 2) : Ui.Icon(10 + 6, iy + 6, 24, icon, active ? col : "#c8c8c8"));
            TileBadge(c, card, e, 10 + ib - 12, iy - 4);
            double tx = 10 + ib + 10, tw = w - tx - 10;
            if (state != null)
            {
                Ui.Add(card, Ui.Text(tx, iy - 1, tw, 20, name, 14, P.Text, "medium"));
                Ui.Add(card, Ui.Text(tx, iy + 19, tw, 18, state, 12, P.Secondary));
            }
            else Ui.Add(card, Ui.Text(tx, iy, tw, ib, name, 14, P.Text, "medium"));
            return card;
        }

        // The small badge on a tile's icon: unavailable "!", a climate entity's current action.
        private static void TileBadge(Ctx c, Dictionary<string, object> card, Ent e, double x, double y)
        {
            string icon = null, bg = null;
            if (e == null || e.State == "unavailable") { icon = "mdi:exclamation-thick"; bg = Theme.Named["orange"]; }
            else if (e.Domain == "climate" && e.A("hvac_action") is string act)
            {
                icon = act == "heating" ? "mdi:fire" : act == "cooling" ? "mdi:snowflake" : act == "drying" ? "mdi:water-percent" : act == "fan" ? "mdi:fan" : act == "off" ? "mdi:power" : "mdi:clock-outline";
                bg = act == "heating" ? Theme.Named["deep-orange"] : act == "cooling" ? Theme.Named["blue"] : "#9e9e9e";
            }
            if (icon == null) return;
            var b = Ui.Box(x, y, 16, 16, bg, 8);
            Ui.Add(b, Ui.Icon(2, 2, 12, icon, "#ffffff"));
            Ui.Add(card, b);
        }

        private static string StateContent(Ctx c, Ent e, object sc)
        {
            if (e == null) return "Not found";
            if (sc == null) return c.S.StateText(e);
            var parts = new List<string>();
            foreach (var k in sc is List<object> l ? l.Select(x => x as string) : new[] { sc as string })
            {
                if (k == null) continue;
                if (k == "state") parts.Add(c.S.StateText(e));
                else if (k == "last_changed" || k == "last-changed") parts.Add(Format.Relative(DateTimeOffset.FromUnixTimeMilliseconds((long)(e.LastChanged * 1000)).UtcDateTime, c.S.NowUtc));
                else if (k == "last_updated" || k == "last-updated") parts.Add(Format.Relative(DateTimeOffset.FromUnixTimeMilliseconds((long)(e.LastUpdated * 1000)).UtcDateTime, c.S.NowUtc));
                else if (Json.Has(e.Attr, k)) parts.Add(AttrText(e, k));
                else parts.Add(k);   // free text (HA allows custom strings)
            }
            return string.Join(" · ", parts);
        }

        private static string AttrText(Ent e, string k)
        {
            var v = Json.Get(e.Attr, k);
            if (v is double d) return Format.Number(d, null);
            if (v is bool b) return b ? "Yes" : "No";
            return v as string ?? Json.Write(v);
        }

        private static string Picture(Ctx c, Ent e)
        {
            var p = e?.A("entity_picture");
            if (string.IsNullOrEmpty(p)) return null;
            return p.StartsWith("http") ? p : c.S.Ha.Url + p;
        }

        // ---- button ----
        private static Dictionary<string, object> Button(Ctx c, Dictionary<string, object> cfg, double w, double h)
        {
            var P = c.P; var e = c.E(cfg); var card = Card(c, w, h);
            bool showName = Json.B(cfg, "show_name", true), showIcon = Json.B(cfg, "show_icon", true), showState = Json.B(cfg, "show_state", false);
            string name = Format.Name(e, Json.S(cfg, "name"));
            string col = Json.B(cfg, "state_color", true) ? (Format.StateColor(e) ?? P.Text) : P.Text;
            if (Theme.Color(Json.S(cfg, "color")) is string cc && Format.Active(e)) col = cc;
            double textH = (showName ? 20 : 0) + (showState ? 18 : 0);
            double s = Math.Max(16, Math.Min(w * 0.5, h - textH - 20));
            double top = (h - s - textH - 4) / 2;
            if (showIcon) Ui.Add(card, Ui.Icon((w - s) / 2, top, s, Format.Icon(e, Json.S(cfg, "icon"), c.S.Icons), col));
            double y = top + (showIcon ? s + 4 : 0);
            if (showName) { Ui.Add(card, Ui.Text(6, y, w - 12, 20, name, 14, P.Text, null, "center")); y += 20; }
            if (showState && e != null) Ui.Add(card, Ui.Text(6, y, w - 12, 18, c.S.StateText(e), 12, P.Secondary, null, "center"));
            return card;
        }

        // ---- entity / sensor ----
        private static Dictionary<string, object> EntityCard(Ctx c, Dictionary<string, object> cfg, double w, double h, bool graph)
        {
            var P = c.P; var e = c.E(cfg); var card = Card(c, w, h);
            string name = Format.Name(e, Json.S(cfg, "name"));
            string icon = Format.Icon(e, Json.S(cfg, "icon"), c.S.Icons);
            Ui.Add(card, Ui.Text(P.Pad + 4, P.Pad, w - 2 * P.Pad - 40, 22, name, 15, P.Secondary));
            Ui.Add(card, Ui.Icon(w - P.Pad - 26, P.Pad - 1, 24, icon, IconColor(c, e, Json.S(cfg, "color"), Json.B(cfg, "state_color", false))));
            string val, unit = Json.S(cfg, "unit") ?? e?.A("unit_of_measurement");
            if (Json.S(cfg, "attribute") is string at && e != null) val = AttrText(e, at) + (Json.S(cfg, "suffix") ?? "");
            else if (e != null && e.Num != null)
            {
                int? dp = c.S.Registry.TryGetValue(e.Id, out var rg) && Json.Get(rg, "dp") is double d ? (int)d : (int?)null;
                val = Format.Number(e.Num.Value, dp);
            }
            else { val = e == null ? "Not found" : c.S.StateText(e); unit = null; }
            double vy = P.Pad + 26;
            Ui.Add(card, Ui.Text(P.Pad + 4, vy, Ui.Width(val, 30) + 8, 38, val, 30, P.Text));
            if (!string.IsNullOrEmpty(unit)) Ui.Add(card, Ui.Text(P.Pad + 8 + Ui.Width(val, 30), vy + 10, w - 2 * P.Pad - Ui.Width(val, 30), 26, unit, 16, P.Secondary));
            if (graph && e != null && Json.S(cfg, "graph") != "none" && Json.Has(cfg, "graph"))
            {
                double hours = Json.N(cfg, "hours_to_show", 24);
                var (t, v) = c.S.History(e.Id, hours);
                double gy = vy + 42, gh = h - gy;
                if (gh > 12 && t.Count >= 2)
                {
                    double now = (c.S.NowUtc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                    var (gt, gv) = Smooth(t, v, now - hours * 3600, now, Math.Max(8, (int)(w / 6)), (int)Json.N(cfg, "detail", 1));
                    string col = Theme.Color(Json.S(cfg, "color")) ?? P.Accent;
                    Ui.Add(card, Ui.SvgImg(0, gy, w, gh, Svg.Graph(gt, gv, (int)Math.Round(w), (int)Math.Round(gh), col, 3, 0.18, null, null, now - hours * 3600, now, true)));
                }
            }
            return card;
        }

        // Average into buckets like the frontend's sensor graph (detail 1 = hourly-ish, 2 = finer); hold the last value.
        private static (List<double>, List<double>) Smooth(List<double> t, List<double> v, double from, double to, int buckets, int detail)
        {
            int n = detail >= 2 ? buckets : Math.Max(6, buckets / 3);
            var xs = new List<double>(); var ys = new List<double>();
            double step = (to - from) / n;
            int i = 0; double last = v[0];
            while (i < t.Count && t[i] < from) { last = v[i]; i++; }
            for (int b = 0; b < n; b++)
            {
                double a = from + b * step, z = a + step, sum = 0; int cnt = 0;
                while (i < t.Count && t[i] < z) { sum += v[i]; cnt++; last = v[i]; i++; }
                xs.Add(a + step / 2); ys.Add(cnt > 0 ? sum / cnt : last);
            }
            xs.Insert(0, from); ys.Insert(0, ys[0]); xs.Add(to); ys.Add(last);
            return (xs, ys);
        }

        // ---- gauge ----
        private static Dictionary<string, object> Gauge(Ctx c, Dictionary<string, object> cfg, double w, double h)
        {
            var P = c.P; var e = c.E(cfg); var card = Card(c, w, h);
            double min = Json.N(cfg, "min", 0), max = Json.N(cfg, "max", 100), v = e?.Num ?? min;
            string col = P.Accent;
            var sev = Json.O(cfg, "severity");
            if (sev != null)
            {
                // the segment with the highest threshold the value has reached (thresholds may be in any order)
                double best = double.NegativeInfinity;
                foreach (var (k, hex) in new[] { ("green", Theme.Named["green"]), ("yellow", Theme.Named["amber"]), ("red", Theme.Named["red"]) })
                    if (Json.Get(sev, k) is double th && v >= th && th >= best) { best = th; col = hex; }
            }
            double gw = Math.Min(w - 2 * P.Pad, (h - 50) * 100 / 56), gh = gw * 0.56;
            Ui.Add(card, Ui.SvgImg((w - gw) / 2, P.Pad, gw, gh, Svg.Gauge((v - min) / Math.Max(1e-9, max - min), col, "#ffffff22", 100)));
            string unit = Json.S(cfg, "unit") ?? e?.A("unit_of_measurement") ?? "";
            Ui.Add(card, Ui.Text(0, P.Pad + gh * 0.55, w, 30, (e == null ? "—" : Format.Number(v, null)) + (unit == "" ? "" : " " + unit), 22, P.Text, "medium", "center"));
            Ui.Add(card, Ui.Text(P.Pad, h - P.Pad - 22, w - 2 * P.Pad, 22, Format.Name(e, Json.S(cfg, "name")), 14, P.Secondary, null, "center"));
            return card;
        }

        // ---- entities ----
        private static List<Dictionary<string, object>> Rows(Ctx c, Dictionary<string, object> cfg)
        {
            var rows = new List<Dictionary<string, object>>();
            foreach (var r in Json.A(cfg, "entities") ?? new List<object>())
            {
                var row = r is string rs ? new Dictionary<string, object> { ["entity"] = rs } : r as Dictionary<string, object>;
                if (row == null) continue;
                string t = Json.S(row, "type");
                if (t == "conditional") { if (!Conditions.All(c.S, Json.Get(row, "conditions"), c.ViewW)) continue; row = Json.O(row, "row"); if (row == null) continue; t = Json.S(row, "type"); }
                if (!c.Visible(row)) continue;
                if (t == "buttons" || t == "button" || t == "call-service" || t == "perform-action" || t == "cast") continue;
                if (t == null && Json.S(row, "entity") is string eid && ActionDomains.Contains(eid.Split('.')[0])) continue;
                if (t != null && t.StartsWith("custom:") && Json.S(row, "entity") == null) { c.Unsupported(t); continue; }
                rows.Add(row);
            }
            return rows;
        }

        private static double RowHeight(Dictionary<string, object> row)
        {
            switch (Json.S(row, "type")) { case "divider": return 17; case "section": return Json.S(row, "label") != null ? 44 : 17; }
            return 40;
        }

        private static Dictionary<string, object> Entities(Ctx c, Dictionary<string, object> cfg, double w, double h)
        {
            var P = c.P; var card = Card(c, w, h);
            double y = P.Pad;
            Title(c, card, Json.S(cfg, "title"), w, ref y);
            bool sc = Json.B(cfg, "state_color", false);
            foreach (var row in Rows(c, cfg))
            {
                string t = Json.S(row, "type");
                double rh = RowHeight(row);
                if (t == "divider" || t == "section")
                {
                    if (t == "section" && Json.S(row, "label") is string lab) Ui.Add(card, Ui.Text(P.Pad + 4, y + 14, w - 2 * P.Pad, 24, lab, 14, P.Secondary, "medium"));
                    Ui.Add(card, Ui.Box(P.Pad, y + rh - 9, w - 2 * P.Pad, 1, P.Divider));
                    y += rh; continue;
                }
                if (t == "weblink" || t == "text")
                {
                    Ui.Add(card, Ui.Icon(P.Pad + 4, y + 8, 24, Json.S(row, "icon") ?? "mdi:link", P.Secondary));
                    Ui.Add(card, Ui.Text(P.Pad + 44, y, w - 2 * P.Pad - 44, rh, Json.S(row, "name") ?? Json.S(row, "url") ?? Json.S(row, "text") ?? "", 14, P.Text));
                    y += rh; continue;
                }
                var e = c.S.State(Json.S(row, "entity"));
                string name = Format.Name(e, Json.S(row, "name"));
                string val = t == "attribute" && e != null ? AttrText(e, Json.S(row, "attribute")) + (Json.S(row, "suffix") ?? "")
                           : StateContent(c, e, null);
                bool rsc = Json.B(row, "state_color", sc);
                string icol = rsc ? (Format.StateColor(e) ?? P.Inactive) : (Format.Active(e) && Format.StateColor(e) != null && e.Domain == "light" ? Format.StateColor(e) : "#c8c8c8");
                Ui.Add(card, Ui.Icon(P.Pad + 4, y + 8, 24, Format.Icon(e, Json.S(row, "icon"), c.S.Icons), icol));
                double vw = Math.Min(w * 0.45, Ui.Width(val, 14) + 8);
                Ui.Add(card, Ui.Text(P.Pad + 44, y, w - 2 * P.Pad - 52 - vw, rh, name, 14, P.Text));
                Ui.Add(card, Ui.Text(w - P.Pad - vw - 4, y, vw, rh, val, 14, P.Text, null, "right"));
                y += rh;
            }
            return card;
        }

        private static Dictionary<string, object> FilterAsCard(Ctx c, Dictionary<string, object> cfg)
        {
            var filters = Json.A(cfg, "state_filter") ?? new List<object>();
            var keep = new List<object>();
            foreach (var x in Json.A(cfg, "entities") ?? new List<object>())
            {
                string id = x is string xs ? xs : Json.S(x, "entity");
                var e = c.S.State(id);
                if (e == null) continue;
                var fl = Json.A(x as Dictionary<string, object>, "state_filter") ?? filters;
                bool ok = fl.Count == 0 || fl.Any(f => f is string fs ? fs == e.State : Conditions.One(c.S, new Dictionary<string, object>(f as Dictionary<string, object> ?? new Dictionary<string, object>()) { ["entity"] = id }, c.ViewW) || Json.S(f, "value") == e.State);
                if (ok) keep.Add(x);
            }
            var card = Json.O(cfg, "card") is Dictionary<string, object> inner ? new Dictionary<string, object>(inner) : new Dictionary<string, object> { ["type"] = "entities" };
            card["entities"] = keep;
            return card;
        }

        // ---- glance ----
        private static List<Dictionary<string, object>> Glance(Ctx c, Dictionary<string, object> cfg) =>
            (Json.A(cfg, "entities") ?? new List<object>()).Select(r => r is string rs ? new Dictionary<string, object> { ["entity"] = rs } : r as Dictionary<string, object>)
                .Where(r => r != null && c.Visible(r)).ToList();

        private static Dictionary<string, object> GlanceCard(Ctx c, Dictionary<string, object> cfg, double w, double h)
        {
            var P = c.P; var card = Card(c, w, h);
            double y = P.Pad;
            Title(c, card, Json.S(cfg, "title"), w, ref y);
            var items = Glance(c, cfg);
            int cols = Math.Max(1, (int)Json.N(cfg, "columns", Math.Min(5, Math.Max(1, items.Count))));
            double cw = (w - 2 * P.Pad) / cols;
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i]; var e = c.S.State(Json.S(it, "entity"));
                double x = P.Pad + (i % cols) * cw, yy = y + (i / cols) * 76;
                if (Json.B(cfg, "show_name", true)) Ui.Add(card, Ui.Text(x, yy, cw, 18, Format.Name(e, Json.S(it, "name")), 12, P.Secondary, null, "center"));
                if (Json.B(cfg, "show_icon", true)) Ui.Add(card, Ui.Icon(x + (cw - 26) / 2, yy + 20, 26, Format.Icon(e, Json.S(it, "icon"), c.S.Icons), Json.B(cfg, "state_color", true) ? (Format.StateColor(e) ?? P.Inactive) : "#c8c8c8"));
                if (Json.B(cfg, "show_state", true)) Ui.Add(card, Ui.Text(x, yy + 50, cw, 18, StateContent(c, e, null), 12, P.Text, null, "center"));
            }
            return card;
        }

        // ---- markdown ----
        private static readonly Regex Tags = new Regex("<[^>]+>"), Bold = new Regex(@"(\*\*|__)(.+?)\1"), Ital = new Regex(@"(?<![\w*])([*_])(?!\s)(.+?)(?<!\s)\1(?![\w*])"),
            Link = new Regex(@"\[([^\]]*)\]\([^)]*\)"), Code = new Regex("`([^`]*)`");

        // inline Markdown -> TV text markup (bold, italic); everything else plain. Text is escaped first.
        private static string Inline(string s)
        {
            s = s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
            s = Bold.Replace(s, "<font weight='bold'>$2</font>");
            s = Ital.Replace(s, "<font slant='italic'>$2</font>");
            return s;
        }

        private static readonly Regex MarkupTag = new Regex("<[^>]+>");
        private static string Plain(string markup) => MarkupTag.Replace(markup, "").Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&");

        private static List<(string text, double px, string weight, bool secondary, double after)> Markdown(Ctx c, Dictionary<string, object> cfg)
        {
            string src = Json.S(cfg, "content") ?? "";
            string text = c.S.Template(src, new Dictionary<string, object> { ["config"] = cfg }) ?? "";
            var blocks = new List<(string, double, string, bool, double)>();
            var para = new List<string>();
            void FlushPara() { if (para.Count > 0) { blocks.Add((string.Join(" ", para), 14, null, false, 14)); para.Clear(); } }
            foreach (var raw in text.Replace("\r", "").Split('\n'))
            {
                string line = Inline(Code.Replace(Link.Replace(Tags.Replace(raw, ""), "$1"), "$1").Replace("&nbsp;", " ").Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">"));
                string t = line.Trim();
                if (t.Length == 0) { FlushPara(); continue; }
                var hm = Regex.Match(t, @"^(#{1,6})\s+(.*)$");
                if (hm.Success)
                {
                    FlushPara();
                    int lvl = hm.Groups[1].Value.Length;
                    blocks.Add((hm.Groups[2].Value, lvl == 1 ? 24 : lvl == 2 ? 20 : 16, "medium", false, 6));
                    continue;
                }
                var lm = Regex.Match(t, @"^([-*+]|\d+\.)\s+(.*)$");
                if (lm.Success) { FlushPara(); blocks.Add(((lm.Groups[1].Value.EndsWith(".") ? lm.Groups[1].Value + " " : "      •  ") + lm.Groups[2].Value, 14, null, false, 1)); continue; }
                if (t.StartsWith(">")) { FlushPara(); blocks.Add((t.TrimStart('>', ' '), 14, null, true, 6)); continue; }
                if (Regex.IsMatch(t, @"^(-{3,}|\*{3,}|_{3,})$")) { FlushPara(); blocks.Add(("", 6, null, false, 6)); continue; }
                para.Add(t);
            }
            FlushPara();
            return blocks;
        }

        private static Dictionary<string, object> MarkdownCard(Ctx c, Dictionary<string, object> cfg, double w, double h)
        {
            var P = c.P;
            var card = Json.Has(cfg, "text_only") && Json.B(cfg, "text_only", false) ? Ui.Box(0, 0, w, h) : Card(c, w, h);
            double y = 16;
            if (Json.S(cfg, "title") is string mt) { Ui.Add(card, Ui.Text(16, 16, w - 32, 32, mt, 22, P.Text)); y += 48; }
            foreach (var b in Markdown(c, cfg))
            {
                double bh = Ui.Lines(Plain(b.text), b.px, w - 32) * b.px * 1.5;
                if (b.text.Length > 0)
                {
                    var tn = Ui.Text(16, y, w - 32, bh, b.text, b.px, b.secondary ? P.Secondary : P.Text, b.weight, "left", "top", true);
                    if (b.text.Contains("<")) tn["markup"] = true;
                    Ui.Add(card, tn);
                }
                y += bh + b.after;
            }
            return card;
        }

        // ---- heading ----
        private static Dictionary<string, object> Heading(Ctx c, Dictionary<string, object> cfg, double w, double h)
        {
            var P = c.P; var box = Ui.Box(0, 0, w, h);
            bool sub = Json.S(cfg, "heading_style") == "subtitle";
            double px = sub ? 14 : 18, x = 4;
            if (Json.S(cfg, "icon") is string ic) { Ui.Add(box, Ui.Icon(x, (h - 20) / 2, 20, ic, sub ? P.Secondary : P.Text)); x += 28; }
            string text = c.S.Template(Json.S(cfg, "heading") ?? "") ?? "";
            Ui.Add(box, Ui.Text(x, 0, w * 0.6, h, text, px, sub ? P.Secondary : P.Text, sub ? null : "medium"));
            // heading badges, right-aligned
            double rx = w - 4;
            foreach (var b in (Json.A(cfg, "badges") ?? new List<object>()).OfType<Dictionary<string, object>>().Reverse())
            {
                if (!c.Visible(b)) continue;
                var e = c.E(b);
                if (e == null) continue;
                string st = Json.B(b, "show_state", true) ? c.S.StateText(e) : "";
                double bw = 22 + Ui.Width(st, 13) + 6;
                rx -= bw;
                if (Json.B(b, "show_icon", true)) Ui.Add(box, Ui.Icon(rx, (h - 18) / 2, 18, Format.Icon(e, Json.S(b, "icon"), c.S.Icons), Theme.Color(Json.S(b, "color")) ?? Format.StateColor(e) ?? P.Secondary));
                Ui.Add(box, Ui.Text(rx + 22, 0, bw - 22, h, st, 13, P.Secondary));
                rx -= 8;
            }
            return box;
        }

        // ---- todo-list ----
        private static (List<Dictionary<string, object>> open, List<Dictionary<string, object>> done) TodoItems(Ctx c, Dictionary<string, object> cfg)
        {
            var items = c.S.Todo(Json.S(cfg, "entity")) ?? new List<Dictionary<string, object>>();
            var open = items.Where(i => Json.S(i, "status") != "completed").ToList();
            var done = Json.B(cfg, "hide_completed", false) ? new List<Dictionary<string, object>>() : items.Where(i => Json.S(i, "status") == "completed").ToList();
            return (open, done);
        }

        private static Dictionary<string, object> Todo(Ctx c, Dictionary<string, object> cfg, double w, double h)
        {
            var P = c.P; var card = Card(c, w, h);
            double y = P.Pad;
            Title(c, card, Json.S(cfg, "title"), w, ref y);
            var (open, done) = TodoItems(c, cfg);
            if (open.Count == 0) { Ui.Add(card, Ui.Text(P.Pad + 4, y, w - 2 * P.Pad, 36, c.S.Todo(Json.S(cfg, "entity")) == null ? "Loading…" : "Nothing to do", 14, P.Secondary)); y += 36; }
            void Item(Dictionary<string, object> it, bool completed)
            {
                Ui.Add(card, Ui.Icon(P.Pad + 4, y + 7, 22, completed ? "mdi:checkbox-marked" : "mdi:checkbox-blank-outline", completed ? P.Secondary : P.Text));
                string due = Json.S(it, "due");
                double dw = due != null ? 110 : 0;
                Ui.Add(card, Ui.Text(P.Pad + 36, y, w - 2 * P.Pad - 40 - dw, 36, Json.S(it, "summary") ?? "", 14, completed ? P.Secondary : P.Text));
                if (due != null) Ui.Add(card, Ui.Text(w - P.Pad - dw, y, dw - 4, 36, Due(c, due), 12, P.Secondary, null, "right"));
                y += 36;
            }
            foreach (var it in open) Item(it, false);
            if (done.Count > 0)
            {
                Ui.Add(card, Ui.Text(P.Pad + 4, y, w - 2 * P.Pad, 36, "Completed", 13, P.Secondary, "medium"));
                y += 36;
                foreach (var it in done) Item(it, true);
            }
            return card;
        }

        private static string Due(Ctx c, string due)
        {
            DateTime d;
            if (due.Length == 10 && DateTime.TryParseExact(due, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) { }
            else if (DateTimeOffset.TryParse(due, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto)) d = TimeZoneInfo.ConvertTime(dto, c.S.Tz).DateTime;
            else return due;
            var today = c.S.NowLocal.Date;
            string day = d.Date == today ? "Today" : d.Date == today.AddDays(1) ? "Tomorrow" : d.Date < today ? d.ToString("MMM d", CultureInfo.InvariantCulture) + " (late)" : d.ToString("ddd MMM d", CultureInfo.InvariantCulture);
            return due.Length > 10 ? day + " " + d.ToString("h:mm tt", CultureInfo.InvariantCulture).ToLowerInvariant() : day;
        }

        // ---- calendar: agenda of the coming days ----
        private static SortedDictionary<DateTime, List<CalEvent>> Agenda(Ctx c, Dictionary<string, object> cfg)
        {
            var cals = new List<(string, string)>();
            foreach (var x in Json.A(cfg, "entities") ?? new List<object>())
            {
                if (x is string xs) cals.Add((xs, null));
                else if (Json.S(x, "entity") is string id) cals.Add((id, Theme.Color(Json.S(x, "color"))));
            }
            string view = Json.S(cfg, "initial_view") ?? "listWeek";
            int days = Json.Get(cfg, "days") is double dd ? Math.Max(1, (int)dd) : view.Contains("Day") ? 1 : view.Contains("Month") ? 14 : 7;
            int max = (int)Json.N(cfg, "max_events", 12);
            var evs = c.S.Events(cals, days);
            var today = c.S.NowLocal.Date;
            var now = c.S.NowLocal;
            var byDay = new SortedDictionary<DateTime, List<CalEvent>>();
            int n = 0;
            foreach (var e in evs)
            {
                if (!e.AllDay && e.End < now) continue;
                var d = e.Start.Date < today ? today : e.Start.Date;
                if (n++ >= max) break;
                if (!byDay.TryGetValue(d, out var l)) byDay[d] = l = new List<CalEvent>();
                l.Add(e);
            }
            return byDay;
        }


        private static Dictionary<string, object> Calendar(Ctx c, Dictionary<string, object> cfg, double w, double h)
        {
            var P = c.P; var card = Card(c, w, h);
            double y = P.Pad;
            Title(c, card, Json.S(cfg, "title"), w, ref y);
            var days = Agenda(c, cfg);
            if (days.Count == 0) { Ui.Add(card, Ui.Text(P.Pad + 4, y, w - 2 * P.Pad, 32, "No upcoming events", 14, P.Secondary)); return card; }
            var calIds = (Json.A(cfg, "entities") ?? new List<object>()).Select(x => x is string xs ? xs : Json.S(x, "entity")).ToList();
            var today = c.S.NowLocal.Date;
            foreach (var kv in days)
            {
                string label = kv.Key == today ? "Today" : kv.Key == today.AddDays(1) ? "Tomorrow" : kv.Key.ToString("dddd, MMM d", CultureInfo.InvariantCulture);
                Ui.Add(card, Ui.Text(P.Pad + 4, y, w - 2 * P.Pad, 28, label, 14, P.Secondary, "medium"));
                y += 30;
                foreach (var e in kv.Value)
                {
                    int ci = Math.Max(0, calIds.FindIndex(id => Format.Name(c.S.State(id), null) == e.Calendar));
                    string col = e.Color ?? P.Series[ci % P.Series.Length];
                    Ui.Add(card, Ui.Box(P.Pad + 6, y + 9, 10, 10, col, 5));
                    string time = e.AllDay ? "All day" : e.Start.ToString("h:mm", CultureInfo.InvariantCulture) + e.Start.ToString("tt", CultureInfo.InvariantCulture).ToLowerInvariant().Substring(0, 1);
                    Ui.Add(card, Ui.Text(P.Pad + 24, y, 64, 28, time, 13, P.Secondary));
                    Ui.Add(card, Ui.Text(P.Pad + 92, y, w - 2 * P.Pad - 96, 28, e.Summary, 14, P.Text));
                    y += 28;
                }
                y += 6;
            }
            return card;
        }

        // ---- weather ----
        private static Dictionary<string, object> Weather(Ctx c, Dictionary<string, object> cfg, double w, double h)
        {
            var P = c.P; var e = c.E(cfg); var card = Card(c, w, h);
            double y = P.Pad;
            string unit = e?.A("temperature_unit") ?? "°";
            if (Json.B(cfg, "show_current", true) && e != null)
            {
                Ui.Add(card, Ui.Icon(P.Pad, y, 64, Format.WeatherIcon(e.State), "#ffd54f"));
                string temp = Json.Get(e.Attr, "temperature") is double t ? Format.Number(t, 0) + unit : "";
                Ui.Add(card, Ui.Text(P.Pad + 76, y, w * 0.4, 32, Format.State(e, null, c.S.Tz, c.S.NowUtc).Replace("Partlycloudy", "Partly cloudy").Replace("Clear-night", "Clear"), 16, P.Text, "medium"));
                Ui.Add(card, Ui.Text(P.Pad + 76, y + 32, w * 0.4, 24, Format.Name(e, Json.S(cfg, "name")), 13, P.Secondary));
                Ui.Add(card, Ui.Text(w - P.Pad - 140, y, 136, 40, temp, 30, P.Text, null, "right"));
                y += 72;
            }
            if (Json.B(cfg, "show_forecast", true) && e != null)
            {
                var fc = c.S.Forecasts(e.Id, Json.S(cfg, "forecast_type") ?? "daily");
                if (fc != null && fc.Count > 0)
                {
                    bool daily = (Json.S(cfg, "forecast_type") ?? "daily") != "hourly";
                    int n = Math.Min(fc.Count, Math.Max(1, (int)((w - 2 * P.Pad) / 70)));
                    n = Math.Min(n, (int)Json.N(cfg, "forecast_slots", 5));
                    double cw = (w - 2 * P.Pad) / n;
                    for (int i = 0; i < n; i++)
                    {
                        var f = fc[i]; double x = P.Pad + i * cw;
                        Ui.Add(card, Ui.Text(x, y, cw, 20, daily ? f.Time.ToString("ddd", CultureInfo.InvariantCulture) : f.Time.ToString("htt", CultureInfo.InvariantCulture).ToLowerInvariant(), 13, P.Secondary, null, "center"));
                        Ui.Add(card, Ui.Icon(x + (cw - 32) / 2, y + 22, 32, Format.WeatherIcon(f.Condition), "#ffd54f"));
                        Ui.Add(card, Ui.Text(x, y + 56, cw, 20, f.Hi != null ? Format.Number(f.Hi.Value, 0) + "°" : "", 14, P.Text, null, "center"));
                        if (f.Lo != null) Ui.Add(card, Ui.Text(x, y + 76, cw, 18, Format.Number(f.Lo.Value, 0) + "°", 12, P.Secondary, null, "center"));
                    }
                }
            }
            return card;
        }

        // ---- clock / better-moment ----
        private static Dictionary<string, object> Clock(Ctx c, Dictionary<string, object> cfg, double w, double h)
        {
            var P = c.P; var card = Card(c, w, h);
            var now = c.S.NowLocal;
            string tf = Json.S(cfg, "time_format") ?? "auto";
            string t = tf == "24" ? now.ToString("HH:mm", CultureInfo.InvariantCulture) : now.ToString("h:mm", CultureInfo.InvariantCulture);
            double px = Math.Min(h * 0.55, w / 4);
            double y = (h - px * 1.25) / 2;
            if (Json.S(cfg, "title") is string title) { Ui.Add(card, Ui.Text(0, 6, w, 18, title, 13, P.Secondary, null, "center")); y += 8; }
            Ui.Add(card, Ui.Text(0, y, w, px * 1.25, t + (tf == "24" ? "" : " " + now.ToString("tt", CultureInfo.InvariantCulture)), px, P.Text, "light", "center"));
            return card;
        }

        private static readonly Regex MomentTok = new Regex(@"\[[^\]]*\]|Do|dddd|ddd|dd|MMMM|MMM|MM|M|DD|D|YYYY|YY|HH|H|hh|h|mm|m|ss|s|A|a");

        public static string MomentFormat(DateTime t, string fmt)
        {
            return MomentTok.Replace(fmt ?? "h:mm a", m =>
            {
                string k = m.Value;
                if (k.StartsWith("[")) return k.Substring(1, k.Length - 2);
                switch (k)
                {
                    case "Do": { int d = t.Day; string suf = (d % 100 >= 11 && d % 100 <= 13) ? "th" : d % 10 == 1 ? "st" : d % 10 == 2 ? "nd" : d % 10 == 3 ? "rd" : "th"; return d + suf; }
                    case "dddd": return t.ToString("dddd", CultureInfo.InvariantCulture);
                    case "ddd": return t.ToString("ddd", CultureInfo.InvariantCulture);
                    case "dd": return t.ToString("ddd", CultureInfo.InvariantCulture).Substring(0, 2);
                    case "MMMM": return t.ToString("MMMM", CultureInfo.InvariantCulture);
                    case "MMM": return t.ToString("MMM", CultureInfo.InvariantCulture);
                    case "MM": return t.Month.ToString("00");
                    case "M": return t.Month.ToString();
                    case "DD": return t.Day.ToString("00");
                    case "D": return t.Day.ToString();
                    case "YYYY": return t.Year.ToString();
                    case "YY": return (t.Year % 100).ToString("00");
                    case "HH": return t.Hour.ToString("00");
                    case "H": return t.Hour.ToString();
                    case "hh": return ((t.Hour + 11) % 12 + 1).ToString("00");
                    case "h": return ((t.Hour + 11) % 12 + 1).ToString();
                    case "mm": return t.Minute.ToString("00");
                    case "m": return t.Minute.ToString();
                    case "ss": return t.Second.ToString("00");
                    case "s": return t.Second.ToString();
                    case "A": return t.Hour < 12 ? "AM" : "PM";
                    case "a": return t.Hour < 12 ? "am" : "pm";
                }
                return k;
            });
        }

        // better-moment-card: one line per `moment` entry; parentStyle font-size in em scales the line.
        public static Dictionary<string, object> Moment(Ctx c, Dictionary<string, object> cfg, double w, double h, bool badge)
        {
            var P = c.P; var box = Ui.Box(0, 0, w, h);
            var lines = (Json.A(cfg, "moment") ?? new List<object>()).OfType<Dictionary<string, object>>().ToList();
            if (lines.Count == 0) lines.Add(new Dictionary<string, object> { ["format"] = "h:mm a" });
            double y = 0, lh = h / lines.Count;
            foreach (var l in lines)
            {
                double em = 1;
                var m = Regex.Match(Json.S(l, "parentStyle") ?? "", @"font-size:\s*([\d.]+)em");
                if (m.Success) em = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                double px = Math.Min(lh * 0.8, (badge ? 16 : 20) * em);
                Ui.Add(box, Ui.Text(0, y, w, lh, MomentFormat(c.S.NowLocal, Json.S(l, "format")), px, P.Text, null, "center"));
                y += lh;
            }
            return box;
        }

        // ---- history graph ----
        private static readonly string[] Lines = { "#44739e", "#984ea3", "#00d2d5", "#ff7f00", "#af8d00", "#7f80cd", "#b3e900", "#c42e60" };

        private static Dictionary<string, object> HistoryGraph(Ctx c, Dictionary<string, object> cfg, double w, double h)
        {
            var P = c.P; var card = Card(c, w, h);
            double y = P.Pad;
            Title(c, card, Json.S(cfg, "title"), w, ref y);
            double hours = Json.N(cfg, "hours_to_show", 24), now = (c.S.NowUtc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            var ents = (Json.A(cfg, "entities") ?? new List<object>()).Select(x => x is string xs ? xs : Json.S(x, "entity")).Where(x => x != null).ToList();
            // one shared scale so lines compare
            var series = ents.Select(id => (id, h: c.S.History(id, hours))).Where(s => s.h.t.Count >= 2).ToList();
            double gh = h - y - P.Pad - 28;
            if (series.Count > 0 && gh > 10)
            {
                double lo = series.Min(s => s.h.v.Min()), hi = series.Max(s => s.h.v.Max());
                for (int i = 0; i < series.Count; i++)
                {
                    var (gt, gv) = Smooth(series[i].h.t, series[i].h.v, now - hours * 3600, now, Math.Max(8, (int)(w / 6)), 2);
                    Ui.Add(card, Ui.SvgImg(P.Pad, y, w - 2 * P.Pad, gh, Svg.Graph(gt, gv, (int)(w - 2 * P.Pad), (int)gh, Lines[i % Lines.Length], 2.5, 0, lo, hi, now - hours * 3600, now)));
                }
            }
            double lx = P.Pad + 4;
            for (int i = 0; i < ents.Count; i++)
            {
                string name = Format.Name(c.S.State(ents[i]), null);
                Ui.Add(card, Ui.Box(lx, h - P.Pad - 16, 10, 10, Lines[i % Lines.Length], 5));
                Ui.Add(card, Ui.Text(lx + 14, h - P.Pad - 22, Ui.Width(name, 12) + 8, 22, name, 12, P.Secondary));
                lx += 30 + Ui.Width(name, 12);
            }
            return card;
        }

        // ---- pictures and cameras ----
        private static string HaUrl(Ctx c, string u) => string.IsNullOrEmpty(u) ? null : u.StartsWith("http") ? u : c.S.Ha.Url + (u.StartsWith("/") ? u : "/" + u);

        private static Dictionary<string, object> Picture(Ctx c, Dictionary<string, object> cfg, double w, double h)
        {
            var P = c.P; var e = c.E(cfg) ?? c.S.State(Json.S(cfg, "camera_image"));
            var card = Ui.Box(0, 0, w, h, "#00000040", P.Radius);
            string src; int refresh = 0;
            if (e != null && e.Domain == "camera")
            {
                src = c.S.Ha.Url + "/api/camera_proxy/" + e.Id; refresh = 2000;
                var live = LiveCamera(c, e.Id, null, null, w, h, src);
                if (live != null) { Ui.Add(card, live); src = null; }
            }
            else src = HaUrl(c, Json.S(cfg, "image") ?? Picture(c, c.S.State(Json.S(cfg, "image_entity"))) ?? Picture(c, e));
            if (src != null) Ui.Add(card, Ui.Image(0, 0, w, h, src, P.Radius, refresh, Json.S(cfg, "fit_mode") == "contain" ? "contain" : "cover"));
            string title = Json.S(cfg, "title") ?? (T(cfg) == "picture-entity" && Json.B(cfg, "show_name", true) && e != null ? Format.Name(e, Json.S(cfg, "name")) : null);
            string state = T(cfg) == "picture-entity" && Json.B(cfg, "show_state", true) && e != null && e.Domain != "camera" ? c.S.StateText(e) : null;
            var glance = T(cfg) == "picture-glance" ? (Json.A(cfg, "entities") ?? new List<object>()).Select(x => x is string xs ? xs : Json.S(x, "entity")).Where(x => x != null).ToList() : new List<string>();
            if (title != null || state != null || glance.Count > 0)
            {
                var bar = Ui.Box(0, h - 48, w, 48, "#0000004d");
                if (title != null) Ui.Add(bar, Ui.Text(16, 0, w * 0.6, 48, title, 16, "#ffffff"));
                if (state != null) Ui.Add(bar, Ui.Text(w * 0.6, 0, w * 0.4 - 16, 48, state, 16, "#ffffff", null, "right"));
                double ix = w - 16 - 24;
                foreach (var id in Enumerable.Reverse(glance))
                {
                    var ge = c.S.State(id);
                    Ui.Add(bar, Ui.Icon(ix, 12, 24, Format.Icon(ge, null, c.S.Icons), Format.Active(ge) ? "#ffffff" : "#ffffffa0"));
                    ix -= 40;
                }
                Ui.Add(card, bar);
            }
            return card;
        }

        // advanced-camera-card: the selected camera as a refreshing snapshot. Honors remote_control.entities.camera and
        // `overrides` that delete cameras under conditions.
        private static Dictionary<string, object> AdvancedCamera(Ctx c, Dictionary<string, object> cfg, double w, double h)
        {
            var cams = (Json.A(cfg, "cameras") ?? new List<object>()).OfType<Dictionary<string, object>>().ToList();
            var removed = new HashSet<int>();
            foreach (var ov in (Json.A(cfg, "overrides") ?? new List<object>()).OfType<Dictionary<string, object>>())
                if (Conditions.All(c.S, Json.Get(ov, "conditions"), c.ViewW))
                    foreach (var d in Json.A(ov, "delete") ?? new List<object>())
                    {
                        var m = Regex.Match(d as string ?? "", @"^cameras\[(\d+)\]$");
                        if (m.Success) removed.Add(int.Parse(m.Groups[1].Value));
                    }
            var avail = cams.Select((cam, i) => (cam, i)).Where(x => !removed.Contains(x.i) && Json.S(x.cam, "camera_entity") != null).Select(x => x.cam).ToList();
            if (avail.Count == 0) return null;
            var pick = avail[0];
            string sel = c.S.State(Json.S(Json.O(Json.O(cfg, "remote_control"), "entities"), "camera"))?.State;
            if (sel != null)
                pick = avail.FirstOrDefault(cam => sel == Json.S(cam, "id") || sel == Json.S(cam, "camera_entity") || sel == Json.S(cam, "title") ||
                    string.Equals(sel, Format.Name(c.S.State(Json.S(cam, "camera_entity")), null), StringComparison.OrdinalIgnoreCase)) ?? pick;
            var card = Ui.Box(0, 0, w, h, "#00000040", c.P.Radius);
            string fit = Json.S(Json.O(Json.O(pick, "dimensions"), "layout"), "fit") == "contain" ? "contain" : "cover";
            string stream = Json.S(Json.O(pick, "go2rtc"), "stream"), host = HostOf(Json.S(Json.O(pick, "go2rtc"), "url") ?? Json.S(Json.O(pick, "frigate"), "url") ?? Json.S(Json.O(cfg, "frigate"), "url"));
            string snap = c.S.Ha.Url + "/api/camera_proxy/" + Json.S(pick, "camera_entity");
            var live = LiveCamera(c, Json.S(pick, "camera_entity"), stream, host, w, h, snap);
            Ui.Add(card, live ?? Ui.Image(0, 0, w, h, snap, c.P.Radius, 1000, fit));
            return card;
        }

        // A camera's live view through the video relay: go2rtc's RTSP restream when the card names one (fast start),
        // else Home Assistant's own stream for the entity. Null when the relay isn't available or the camera can't stream
        // (the card then shows snapshots). The TV applies its video limits and falls back to `snap` on its own.
        private static Dictionary<string, object> LiveCamera(Ctx c, string entity, string go2rtcStream, string go2rtcHost, double w, double h, string snap)
        {
            if (c.S.Measuring || c.S.Hidden) return null;                // measuring, or off screen: no streams
            if (c.S.Host.VideoUrl("probe", 160) == null) return null;   // relay not available right now
            string src = go2rtcStream != null && go2rtcHost != null ? "rtsp://" + go2rtcHost + ":8554/" + go2rtcStream : c.S.CameraStream(entity);
            if (src == null) return null;
            return new Dictionary<string, object>
            {
                ["type"] = "video", ["x"] = 0.0, ["y"] = 0.0, ["w"] = w, ["h"] = h, ["src"] = src, ["volume"] = 0.0, ["fit"] = "contain",
                ["fallback"] = new Dictionary<string, object> { ["src"] = snap, ["refresh_ms"] = 1000.0 },
            };
        }

        private static string HostOf(string u)
        {
            if (string.IsNullOrEmpty(u)) return null;
            if (!u.Contains("://")) u = "http://" + u;
            try { return new Uri(u).Host; } catch (Exception) { return null; }
        }

        // ---- stacks / grid ----
        private static Dictionary<string, object> Stack(Ctx c, Dictionary<string, object> cfg, double w, double h)
        {
            var kids = Kids(c, cfg);
            var box = Ui.Box(0, 0, w, h);
            string t = T(cfg);
            if (t == "horizontal-stack")
            {
                if (kids.Count == 0) return null;
                double cw = (w - Layout.Gap * (kids.Count - 1)) / kids.Count;
                for (int i = 0; i < kids.Count; i++) { var n = Draw(c, kids[i], cw, h); if (n == null) continue; n["x"] = i * (cw + Layout.Gap); n["y"] = 0.0; Ui.Add(box, n); }
            }
            else if (t == "vertical-stack")
            {
                double y = 0;
                foreach (var k in kids) { double kh = Height(c, k, w); var n = Draw(c, k, w, kh); if (n != null) { n["x"] = 0.0; n["y"] = y; Ui.Add(box, n); } y += kh + Layout.Gap; }
            }
            else
            {
                int cols = Math.Max(1, (int)Json.N(cfg, "columns", 3));
                double cw = (w - Layout.Gap * (cols - 1)) / cols, y = 0;
                if (Json.S(cfg, "title") is string title) { Ui.Add(box, Ui.Text(4, 0, w, 32, title, 18, c.P.Text, "medium")); y = 40; }
                double rh = Json.B(cfg, "square", true) ? cw : kids.Count == 0 ? 0 : kids.Max(k => Height(c, k, cw));
                for (int i = 0; i < kids.Count; i++)
                {
                    var n = Draw(c, kids[i], cw, rh);
                    if (n == null) continue;
                    n["x"] = (i % cols) * (cw + Layout.Gap); n["y"] = y + (i / cols) * (rh + Layout.Gap);
                    Ui.Add(box, n);
                }
            }
            return Json.A(box, "children") == null ? null : box;
        }

        // ---- mushroom ----
        private static Dictionary<string, object> Vars(Ctx c, Dictionary<string, object> cfg) =>
            new Dictionary<string, object> { ["entity"] = Json.S(cfg, "entity"), ["config"] = cfg, ["user"] = c.S.UserName };

        private static Dictionary<string, object> Mushroom(Ctx c, Dictionary<string, object> cfg, double w, double h)
        {
            var P = c.P; var e = c.E(cfg); var card = Card(c, w, h);
            bool template = T(cfg) == "custom:mushroom-template-card";
            var vars = Vars(c, cfg);
            string primary, secondary, icon, color;
            if (template)
            {
                primary = c.S.Template(Json.S(cfg, "primary"), vars);
                secondary = c.S.Template(Json.S(cfg, "secondary"), vars);
                icon = c.S.Template(Json.S(cfg, "icon"), vars);
                color = Theme.Color(c.S.Template(Json.S(cfg, "icon_color") ?? Json.S(cfg, "color"), vars));
                if (string.IsNullOrEmpty(icon) && e != null) icon = Format.Icon(e, null, c.S.Icons);
            }
            else
            {
                primary = Json.S(cfg, "primary_info") == "none" ? null : Format.Name(e, Json.S(cfg, "name"));
                string si = Json.S(cfg, "secondary_info");
                secondary = si == "none" || e == null ? null : si == "last-changed" ? Format.Relative(DateTimeOffset.FromUnixTimeMilliseconds((long)(e.LastChanged * 1000)).UtcDateTime, c.S.NowUtc) : c.S.StateText(e);
                icon = Format.Icon(e, Json.S(cfg, "icon"), c.S.Icons);
                color = MushroomColor(c, cfg, e) ?? Theme.Color(Json.S(cfg, "icon_color")) ?? (Format.Active(e) ? Format.StateColor(e) : null);
            }
            primary = primary?.Trim(); secondary = secondary == null ? null : Regex.Replace(secondary.Trim(), @"\s+", " ");
            bool hasIcon = !string.IsNullOrEmpty(icon) && Json.S(cfg, "icon_type") != "none";
            string ic = color ?? "#9e9e9e";
            if (Json.S(cfg, "layout") == "vertical")
            {
                double s = 40;
                if (hasIcon) Ui.Add(card, Ui.IconBadge((w - s) / 2, 12, s, icon, ic, color != null));
                if (!string.IsNullOrEmpty(primary)) Ui.Add(card, Ui.Text(8, 58, w - 16, 20, primary, 14, P.Text, "medium", "center"));
                if (!string.IsNullOrEmpty(secondary)) Ui.Add(card, Ui.Text(8, 78, w - 16, 18, secondary, 12, P.Secondary, null, "center"));
                return card;
            }
            double ib = 36, iy = (Math.Min(h, 56) - ib) / 2, tx = hasIcon ? 10 + ib + 10 : 14;
            string mpic = template ? null : MushroomPicture(c, cfg, e);
            if (mpic != null) Ui.Add(card, Ui.Image(10, iy, ib, ib, mpic, ib / 2));
            else if (hasIcon) Ui.Add(card, Ui.IconBadge(10, iy, ib, icon, ic, color != null));
            if (!string.IsNullOrEmpty(primary) && !string.IsNullOrEmpty(secondary))
            {
                Ui.Add(card, Ui.Text(tx, iy - 1, w - tx - 10, 20, primary, 14, P.Text, "medium"));
                Ui.Add(card, Ui.Text(tx, iy + 19, w - tx - 10, 18, secondary, 12, P.Secondary));
            }
            else Ui.Add(card, Ui.Text(tx, iy, w - tx - 10, ib, primary ?? secondary ?? "", 14, P.Text, "medium"));
            return card;
        }

        private static Dictionary<string, object> MushroomTitle(Ctx c, Dictionary<string, object> cfg, double w, double h)
        {
            var P = c.P; var box = Ui.Box(0, 0, w, h);
            double y = P.Pad;
            string a = Json.S(cfg, "alignment") ?? "left";
            if (Json.S(cfg, "title") is string t) { Ui.Add(box, Ui.Text(P.Pad, y, w - 2 * P.Pad, 32, c.S.Template(t) ?? "", 24, P.Text, "medium", a == "end" ? "right" : a)); y += 36; }
            if (Json.S(cfg, "subtitle") is string st) Ui.Add(box, Ui.Text(P.Pad, y, w - 2 * P.Pad, 22, c.S.Template(st) ?? "", 15, P.Secondary, null, a == "end" ? "right" : a));
            return box;
        }

        private static Dictionary<string, object> Chips(Ctx c, Dictionary<string, object> cfg, double w, double h)
        {
            var built = new List<Dictionary<string, object>>();
            foreach (var chip in (Json.A(cfg, "chips") ?? new List<object>()).OfType<Dictionary<string, object>>())
            {
                if (!c.Visible(chip)) continue;
                string t = Json.S(chip, "type");
                if (t == "conditional")
                {
                    if (!Conditions.All(c.S, Json.Get(chip, "conditions"), c.ViewW)) continue;
                    chip.TryGetValue("chip", out var inner);
                    if (!(inner is Dictionary<string, object> id)) continue;
                    t = Json.S(id, "type"); var n0 = Chip(c, id, t); if (n0 != null) built.Add(n0); continue;
                }
                var n = Chip(c, chip, t);
                if (n != null) built.Add(n);
            }
            if (built.Count == 0) return null;
            var box = Ui.Box(0, 0, w, h);
            double total = built.Sum(b => (double)b["w"]) + 8 * (built.Count - 1);
            string a = Json.S(cfg, "alignment") ?? "start";
            double x = a == "center" ? (w - total) / 2 : a == "end" ? w - total : 0;
            foreach (var b in built) { b["x"] = x; b["y"] = (h - 36) / 2; x += (double)b["w"] + 8; Ui.Add(box, b); }
            return box;
        }

        private static Dictionary<string, object> Chip(Ctx c, Dictionary<string, object> chip, string t)
        {
            var P = c.P; var e = c.E(chip);
            string icon = null, text = null, col = null;
            switch (t)
            {
                case "entity":
                    if (e == null) return null;
                    icon = Format.Icon(e, Json.S(chip, "icon"), c.S.Icons);
                    text = Json.S(chip, "content_info") == "name" ? Format.Name(e, Json.S(chip, "name")) : Json.S(chip, "content_info") == "none" ? null : c.S.StateText(e);
                    col = Theme.Color(Json.S(chip, "icon_color")) ?? Format.StateColor(e);
                    break;
                case "template":
                    var vars = Vars(c, chip);
                    icon = c.S.Template(Json.S(chip, "icon"), vars);
                    text = c.S.Template(Json.S(chip, "content"), vars);
                    col = Theme.Color(c.S.Template(Json.S(chip, "icon_color"), vars));
                    break;
                case "weather":
                    if (e == null) return null;
                    icon = Format.WeatherIcon(e.State);
                    var parts = new List<string>();
                    if (Json.B(chip, "show_temperature", false) && Json.Get(e.Attr, "temperature") is double tt) parts.Add(Format.Number(tt, 0) + (e.A("temperature_unit") ?? "°"));
                    if (Json.B(chip, "show_conditions", false)) parts.Add(c.S.StateText(e));
                    text = parts.Count > 0 ? string.Join(" / ", parts) : null;
                    col = "#ffd54f";
                    break;
                default:
                    return null;   // action / menu / back chips do nothing read-only
            }
            if (string.IsNullOrEmpty(icon) && string.IsNullOrEmpty(text)) return null;
            double w = 12 + (icon != null ? 20 : 0) + (text != null ? 6 + Ui.Width(text, 13) * 1.15 + 6 : 0) + 12;
            var b = Ui.Box(0, 0, w, 36, P.CardBg, 18);
            double x = 12;
            if (!string.IsNullOrEmpty(icon)) { Ui.Add(b, Ui.Icon(x, 8, 20, icon, col ?? P.Text)); x += 26; }
            if (!string.IsNullOrEmpty(text)) Ui.Add(b, Ui.Text(x, 0, w - x - 8, 36, text, 13, P.Text, "medium"));
            return b;
        }
    }

    // View badges (the row above the sections).
    internal static class Badges
    {
        public static Dictionary<string, object> Draw(Ctx c, Dictionary<string, object> cfg)
        {
            var P = c.P;
            string t = (Json.S(cfg, "type") ?? "entity").ToLowerInvariant();
            if (t == "custom:better-moment-card")
            {
                var lines = (Json.A(cfg, "moment") ?? new List<object>()).OfType<Dictionary<string, object>>().ToList();
                double em = 1;
                var m = Regex.Match(Json.S(lines.FirstOrDefault(), "parentStyle") ?? "", @"font-size:\s*([\d.]+)em");
                if (m.Success) em = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                double px = 14 * em;
                string s = Cards.MomentFormat(c.S.NowLocal, Json.S(lines.FirstOrDefault(), "format"));
                double bh = Math.Max(36, px * 1.25);
                var box = Ui.Box(0, 0, Ui.Width(s, px) + 28, bh, P.CardBg, bh / 2);
                Ui.Add(box, Ui.Text(0, 0, (double)box["w"], bh, s, px, P.Text, null, "center"));
                return box;
            }
            if (t == "custom:mushroom-template-badge")
            {
                var vars = new Dictionary<string, object> { ["entity"] = Json.S(cfg, "entity"), ["config"] = cfg, ["user"] = c.S.UserName };
                string content = c.S.Template(Json.S(cfg, "content"), vars);
                string label = c.S.Template(Json.S(cfg, "label"), vars);
                string icon = c.S.Template(Json.S(cfg, "icon"), vars);
                string col = Theme.Color(c.S.Template(Json.S(cfg, "color"), vars)) ?? P.Text;
                return Pill(c, icon, null, col, label, content);
            }
            if (t != "entity") { c.Unsupported("badge " + t); return null; }
            var e = c.E(cfg);
            if (e == null) return null;
            if (ActionDomain(e)) return null;
            bool showName = Json.B(cfg, "show_name", false), showState = Json.B(cfg, "show_state", true), showIcon = Json.B(cfg, "show_icon", true);
            string name = showName ? Format.Name(e, Json.S(cfg, "name")) : null;
            string state = showState ? (Json.S(cfg, "state_content") is string sc && !Json.Has(e.Attr, sc) && sc != "state" ? sc : c.S.StateText(e)) : null;
            string pic = Json.B(cfg, "show_entity_picture", false) ? (e.A("entity_picture") is string p ? (p.StartsWith("http") ? p : c.S.Ha.Url + p) : null) : null;
            string color = Theme.Color(Json.S(cfg, "color")) ?? Format.StateColor(e) ?? P.Secondary;
            return Pill(c, showIcon ? Format.Icon(e, Json.S(cfg, "icon"), c.S.Icons) : null, pic, color, name, state);
        }

        private static bool ActionDomain(Ent e) => e.Domain == "button" || e.Domain == "input_button" || e.Domain == "script" || e.Domain == "scene";

        // HA's badge: a pill with an icon (or picture) and one or two lines (label above, value below).
        private static Dictionary<string, object> Pill(Ctx c, string icon, string pic, string color, string label, string value)
        {
            var P = c.P;
            bool two = !string.IsNullOrEmpty(label) && !string.IsNullOrEmpty(value);
            double tw = Math.Max(Ui.Width(label, 10), Ui.Width(value, 12));
            bool hasIcon = !string.IsNullOrEmpty(icon) || pic != null;
            bool hasText = !string.IsNullOrEmpty(label) || !string.IsNullOrEmpty(value);
            double w = hasText ? (hasIcon ? 8 + 18 + 6 : 12) + tw + 12 : 36, h = 36;
            var b = Ui.Box(0, 0, w, h, P.CardBg, 18);
            double x = hasText ? 8 : 9;
            if (pic != null) { Ui.Add(b, Ui.Image(x - 2, 7, 22, 22, pic, 11)); x += 24; }
            else if (!string.IsNullOrEmpty(icon)) { Ui.Add(b, Ui.Icon(x, 9, 18, icon, color)); x += 24; }
            if (two)
            {
                Ui.Add(b, Ui.Text(x, 4, w - x - 8, 13, label, 10, P.Secondary, "medium"));
                Ui.Add(b, Ui.Text(x, 16, w - x - 8, 16, value, 12, P.Text, "medium"));
            }
            else if (hasText) Ui.Add(b, Ui.Text(x, 0, w - x - 8, h, label ?? value, 12, P.Text, "medium"));
            return b;
        }
    }
}
