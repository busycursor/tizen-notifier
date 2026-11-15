// Custom cards common on wall displays: layout-card (grid / masonry / horizontal / vertical layouts), auto-entities,
// week-planner-card, atomic-calendar-revive, calendar-card-pro, stack-in-card variants, weather-chart-card. Pure C#.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace TizenNotifier
{
    internal static partial class Cards
    {
        private static readonly HashSet<string> KnownCustom = new HashSet<string>
        {
            "custom:layout-card", "custom:auto-entities", "custom:week-planner-card", "custom:atomic-calendar-revive",
            "custom:calendar-card-pro", "custom:stack-in-card", "custom:vertical-stack-in-card", "custom:horizontal-stack-in-card",
            "custom:weather-chart-card",
        };

        // ---------- shared: upcoming events grouped by day ----------
        private static SortedDictionary<DateTime, List<CalEvent>> ByDay(Ctx c, object entities, int days, int max)
        {
            var cals = Cals(new Dictionary<string, object> { ["entities"] = entities });
            var now = c.S.NowLocal; var today = now.Date;
            var map = new SortedDictionary<DateTime, List<CalEvent>>();
            int n = 0;
            foreach (var e in c.S.Events(cals, days))
            {
                if (!e.AllDay && e.End < now) continue;
                var d = e.Start.Date < today ? today : e.Start.Date;
                if (n++ >= max) break;
                if (!map.TryGetValue(d, out var l)) map[d] = l = new List<CalEvent>();
                l.Add(e);
            }
            return map;
        }

        private static string Ago(Ctx c, DateTime local)
        {
            var utc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), c.S.Tz);
            return Format.Relative(utc, c.S.NowUtc);
        }

        // ---------- atomic-calendar-revive (event mode): date block on the left, countdowns, progress for what's on now ----------
        private static double AtomicHeight(Ctx c, Dictionary<string, object> cfg)
        {
            var days = ByDay(c, Json.Get(cfg, "entities"), (int)Json.N(cfg, "maxDaysToShow", 7), (int)Json.N(cfg, "maxEventCount", 12));
            double h = 2 * c.P.Pad + ((Json.S(cfg, "name") ?? Json.S(cfg, "title")) != null ? 40 : 0);
            if (days.Count == 0) return h + 40;
            foreach (var d in days) h += Math.Max(56, d.Value.Count * 50) + 10;
            return h;
        }

        private static Dictionary<string, object> Atomic(Ctx c, Dictionary<string, object> cfg, double w, double h)
        {
            var P = c.P; var card = Card(c, w, h);
            double y = P.Pad;
            Title(c, card, Json.S(cfg, "name") ?? Json.S(cfg, "title"), w, ref y);
            var days = ByDay(c, Json.Get(cfg, "entities"), (int)Json.N(cfg, "maxDaysToShow", 7), (int)Json.N(cfg, "maxEventCount", 12));
            if (days.Count == 0) { Ui.Add(card, Ui.Text(P.Pad + 4, y, w - 2 * P.Pad, 40, Json.S(cfg, "noEventsForNextDaysText") ?? "No events in the next days", 14, P.Secondary)); return card; }
            var now = c.S.NowLocal; var today = now.Date;
            foreach (var kv in days)
            {
                double blockH = Math.Max(56, kv.Value.Count * 50);
                bool isToday = kv.Key == today;
                Ui.Add(card, Ui.Text(P.Pad, y, 56, 30, kv.Key.Day.ToString(), 26, isToday ? P.Accent : P.Text, "light", "center"));
                Ui.Add(card, Ui.Text(P.Pad, y + 30, 56, 18, kv.Key.ToString("ddd", CultureInfo.InvariantCulture).ToUpperInvariant(), 11, P.Secondary, "medium", "center"));
                double ey = y, x = P.Pad + 68, tw = w - x - P.Pad;
                foreach (var e in kv.Value)
                {
                    bool on = !e.AllDay && e.Start <= now && e.End > now;
                    string col = e.Color ?? CalColor(new Dictionary<string, object> { ["entities"] = Json.Get(cfg, "entities") }, e);
                    Ui.Add(card, Ui.Box(x, ey + 4, 3, 40, col, 2));
                    string time = e.AllDay ? "All day" : e.Start.ToString("h:mm tt", CultureInfo.InvariantCulture).ToLowerInvariant() + " – " + e.End.ToString("h:mm tt", CultureInfo.InvariantCulture).ToLowerInvariant();
                    Ui.Add(card, Ui.Text(x + 12, ey + 2, tw - 130, 18, time, 12, P.Secondary));
                    if (!e.AllDay && !on && e.Start > now) Ui.Add(card, Ui.Text(w - P.Pad - 120, ey + 2, 116, 18, Ago(c, e.Start), 12, P.Accent, null, "right"));
                    Ui.Add(card, Ui.Text(x + 12, ey + 20, tw - 16, 22, e.Summary, 15, P.Text, on ? "medium" : null));
                    if (on)
                    {
                        double f = (now - e.Start).TotalSeconds / Math.Max(1, (e.End - e.Start).TotalSeconds);
                        Ui.Add(card, Ui.Box(x + 12, ey + 44, tw - 16, 3, "#ffffff22", 2));
                        Ui.Add(card, Ui.Box(x + 12, ey + 44, Math.Max(2, (tw - 16) * f), 3, col, 2));
                    }
                    ey += 50;
                }
                y += blockH + 10;
            }
            return card;
        }

        // ---------- calendar-card-pro: date column, one compact row per event with its calendar's colour ----------
        private static double ProHeight(Ctx c, Dictionary<string, object> cfg)
        {
            var days = ByDay(c, Json.Get(cfg, "entities"), (int)Json.N(cfg, "days_to_show", 3), (int)Json.N(cfg, "max_events_to_show", 12));
            double h = 2 * c.P.Pad + (Json.S(cfg, "title") != null ? 40 : 0);
            if (days.Count == 0) return h + 40;
            foreach (var d in days) h += d.Value.Count * 44 + 8;
            return h;
        }

        private static Dictionary<string, object> Pro(Ctx c, Dictionary<string, object> cfg, double w, double h)
        {
            var P = c.P; var card = Card(c, w, h);
            double y = P.Pad;
            Title(c, card, Json.S(cfg, "title"), w, ref y);
            var days = ByDay(c, Json.Get(cfg, "entities"), (int)Json.N(cfg, "days_to_show", 3), (int)Json.N(cfg, "max_events_to_show", 12));
            if (days.Count == 0) { Ui.Add(card, Ui.Text(P.Pad + 4, y, w - 2 * P.Pad, 40, "No upcoming events", 14, P.Secondary)); return card; }
            var today = c.S.NowLocal.Date;
            foreach (var kv in days)
            {
                bool isToday = kv.Key == today;
                double blockH = kv.Value.Count * 44;
                // date column: weekday, day number, month
                Ui.Add(card, Ui.Text(P.Pad, y, 52, 16, kv.Key.ToString("ddd", CultureInfo.InvariantCulture), 11, isToday ? P.Accent : P.Secondary, "medium", "center"));
                Ui.Add(card, Ui.Text(P.Pad, y + 14, 52, 26, kv.Key.Day.ToString(), 22, isToday ? P.Accent : P.Text, "medium", "center"));
                Ui.Add(card, Ui.Text(P.Pad, y + 38, 52, 14, kv.Key.ToString("MMM", CultureInfo.InvariantCulture), 10, P.Secondary, null, "center"));
                double ey = y, x = P.Pad + 60, tw = w - x - P.Pad;
                foreach (var e in kv.Value)
                {
                    string col = e.Color ?? CalColor(new Dictionary<string, object> { ["entities"] = Json.Get(cfg, "entities") }, e);
                    var row = Ui.Box(x, ey + 2, tw, 40, Theme.WithAlpha(col, 0.12), 6);
                    Ui.Add(row, Ui.Box(0, 0, 4, 40, col, 2));
                    Ui.Add(row, Ui.Text(12, 2, tw - 20, 20, e.Summary, 14, P.Text));
                    string time = e.AllDay ? "All day" : e.Start.ToString("h:mm tt", CultureInfo.InvariantCulture).ToLowerInvariant() + (e.End > e.Start ? " – " + e.End.ToString("h:mm tt", CultureInfo.InvariantCulture).ToLowerInvariant() : "");
                    Ui.Add(row, Ui.Text(12, 21, tw - 20, 17, time, 11, P.Secondary));
                    Ui.Add(card, row);
                    ey += 44;
                }
                y += Math.Max(blockH, 52) + 8;
                Ui.Add(card, Ui.Box(P.Pad, y - 5, w - 2 * P.Pad, 1, P.Divider));
            }
            return card;
        }

        // Rewrites cards that are another card in disguise (auto-entities -> its inner card with the matched entities,
        // the calendar customs -> calendar). Called by Effective.
        private static Dictionary<string, object> Translate(Ctx c, Dictionary<string, object> cfg)
        {
            switch (T(cfg))
            {
                case "custom:auto-entities": return AutoEntities(c, cfg);
                case "custom:atomic-calendar-revive":
                    if ((Json.S(cfg, "defaultMode") ?? "Event").ToLowerInvariant() == "calendar")
                    {
                        var n = new Dictionary<string, object> { ["type"] = "calendar", ["entities"] = Json.Get(cfg, "entities"), ["title"] = Json.Get(cfg, "name") ?? Json.Get(cfg, "title"), ["initial_view"] = "dayGridMonth" };
                        CopyLayout(cfg, n); return n;
                    }
                    return cfg;   // event mode: its own list (below)
            }
            return cfg;
        }

        private static void CopyLayout(Dictionary<string, object> from, Dictionary<string, object> to)
        {
            foreach (var k in new[] { "grid_options", "layout_options", "view_layout", "visibility" }) if (Json.Has(from, k)) to[k] = from[k];
        }

        private static (int, int)? DefaultCustom(Dictionary<string, object> cfg)
        {
            switch (T(cfg))
            {
                case "custom:layout-card": case "custom:week-planner-card": case "custom:stack-in-card": case "custom:vertical-stack-in-card":
                case "custom:horizontal-stack-in-card": case "custom:weather-chart-card":
                case "custom:atomic-calendar-revive": case "custom:calendar-card-pro":
                    return (0, 0);
            }
            return null;
        }

        private static double? HeightCustom(Ctx c, Dictionary<string, object> cfg, double w)
        {
            switch (T(cfg))
            {
                case "custom:layout-card": return LayoutCard(c, cfg, w, null).h;
                case "custom:week-planner-card":
                {
                    var days = WeekDays(c, cfg);
                    int most = days.Count == 0 ? 1 : Math.Max(1, days.Max(d => d.Value.Count));
                    return 2 * c.P.Pad + (Json.S(cfg, "title") != null ? 40 : 0) + 56 + most * 46;
                }
                case "custom:stack-in-card": case "custom:vertical-stack-in-card": case "custom:horizontal-stack-in-card":
                    return Height(c, StackOf(cfg), w);
                case "custom:weather-chart-card": return 2 * c.P.Pad + 70 + 200;
                case "custom:atomic-calendar-revive": return AtomicHeight(c, cfg);
                case "custom:calendar-card-pro": return ProHeight(c, cfg);
            }
            return null;
        }

        private static Dictionary<string, object> DrawCustom(Ctx c, Dictionary<string, object> cfg, double w, double h)
        {
            switch (T(cfg))
            {
                case "custom:layout-card": { var box = Ui.Box(0, 0, w, h); LayoutCard(c, cfg, w, box); return Json.A(box, "children") == null ? null : box; }
                case "custom:week-planner-card": return WeekPlanner(c, cfg, w, h);
                case "custom:stack-in-card": case "custom:vertical-stack-in-card": case "custom:horizontal-stack-in-card":
                {
                    var inner = Draw(c, StackOf(cfg), w, h);
                    if (inner == null) return null;
                    Unbox(inner, 0);
                    var card = Card(c, w, h);
                    Ui.Add(card, inner);
                    return card;
                }
                case "custom:weather-chart-card": return WeatherChart(c, cfg, w, h);
                case "custom:atomic-calendar-revive": return Atomic(c, cfg, w, h);
                case "custom:calendar-card-pro": return Pro(c, cfg, w, h);
            }
            return null;
        }

        private static bool HasCustom(string t) => KnownCustom.Contains(t);

        // ---------- stack-in-card: one background around the stacked cards, theirs removed ----------
        private static Dictionary<string, object> StackOf(Dictionary<string, object> cfg)
        {
            string t = T(cfg);
            string mode = t == "custom:horizontal-stack-in-card" || Json.S(cfg, "mode") == "horizontal" ? "horizontal-stack" : "vertical-stack";
            return new Dictionary<string, object> { ["type"] = mode, ["cards"] = Json.Get(cfg, "cards") ?? new List<object>() };
        }

        // Drop the card backgrounds of the stacked cards (the stack's direct children), keep everything inside them.
        private static void Unbox(Dictionary<string, object> stack, int depth)
        {
            foreach (var k in Json.A(stack, "children") ?? new List<object>())
                if (k is Dictionary<string, object> kd && Json.S(kd, "type") == "box") { kd.Remove("bg"); kd.Remove("radius"); }
        }

        // ---------- auto-entities ----------
        private static readonly Dictionary<string, Regex> Globs = new Dictionary<string, Regex>();
        private static bool Match(string pattern, string value)
        {
            if (pattern == null) return true;
            if (value == null) return false;
            if (pattern.Length > 2 && pattern.StartsWith("/") && pattern.EndsWith("/"))
            {
                if (!Globs.TryGetValue(pattern, out var re)) Globs[pattern] = re = new Regex(pattern.Substring(1, pattern.Length - 2));
                return re.IsMatch(value);
            }
            if (pattern.Contains("*"))
            {
                if (!Globs.TryGetValue(pattern, out var re)) Globs[pattern] = re = new Regex("^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase);
                return re.IsMatch(value);
            }
            return string.Equals(pattern, value, StringComparison.OrdinalIgnoreCase);
        }

        // auto-entities state filters may compare: "> 20", "<= 5", "!= off", "on".
        private static bool MatchState(object want, string state)
        {
            if (want == null) return true;
            string s = want is string ws ? ws.Trim() : Json.Write(want).Trim('"');
            foreach (var op in new[] { ">=", "<=", "!=", ">", "<", "==" })
                if (s.StartsWith(op))
                {
                    string rhs = s.Substring(op.Length).Trim();
                    bool numeric = double.TryParse(rhs, NumberStyles.Float, CultureInfo.InvariantCulture, out var b) & double.TryParse(state, NumberStyles.Float, CultureInfo.InvariantCulture, out var a);
                    switch (op)
                    {
                        case ">=": return numeric && a >= b;
                        case "<=": return numeric && a <= b;
                        case ">": return numeric && a > b;
                        case "<": return numeric && a < b;
                        case "!=": return !Match(rhs, state);
                        case "==": return Match(rhs, state);
                    }
                }
            return Match(s, state);
        }

        private static bool Filter(Ctx c, Dictionary<string, object> f, string id)
        {
            var e = c.S.State(id);
            if (e == null) return false;
            c.S.Registry.TryGetValue(id, out var reg);
            foreach (var kv in f)
            {
                switch (kv.Key)
                {
                    case "domain": if (!Match(kv.Value as string, e.Domain)) return false; break;
                    case "entity_id": if (!Match(kv.Value as string, id)) return false; break;
                    case "state": if (!MatchState(kv.Value, e.State)) return false; break;
                    case "name": if (!Match(kv.Value as string, e.A("friendly_name"))) return false; break;
                    case "area": if (!Match(kv.Value as string, c.S.AreaOf(id)) && !(c.S.AreaOf(id) is string aid && c.S.Areas.TryGetValue(aid, out var ar) && Match(kv.Value as string, Json.S(ar, "name")))) return false; break;
                    case "integration": if (!Match(kv.Value as string, Json.S(reg, "pl"))) return false; break;
                    case "attributes":
                        if (kv.Value is Dictionary<string, object> attrs)
                            foreach (var a in attrs) if (!MatchState(a.Value, Json.Get(e.Attr, a.Key) is object av ? (av is string avs ? avs : Json.Write(av).Trim('"')) : null)) return false;
                        break;
                    case "not": if (kv.Value is Dictionary<string, object> nf && Filter(c, nf, id)) return false; break;
                    case "options": case "sort": break;
                }
            }
            return true;
        }

        private static Dictionary<string, object> AutoEntities(Ctx c, Dictionary<string, object> cfg)
        {
            var includes = (Json.A(Json.O(cfg, "filter"), "include") ?? new List<object>()).OfType<Dictionary<string, object>>().ToList();
            var excludes = (Json.A(Json.O(cfg, "filter"), "exclude") ?? new List<object>()).OfType<Dictionary<string, object>>().ToList();
            var rows = new List<object>(); var seen = new HashSet<string>();
            // listed entities first, then filter matches
            foreach (var x in Json.A(cfg, "entities") ?? new List<object>())
            {
                string id = x is string xs ? xs : Json.S(x, "entity");
                if (id != null && seen.Add(id)) rows.Add(x);
            }
            var all = c.S.AllEntityIds();
            foreach (var inc in includes)
            {
                var opts = Json.O(inc, "options");
                foreach (var id in all)
                {
                    if (seen.Contains(id) || !Filter(c, inc, id) || excludes.Any(ex => Filter(c, ex, id))) continue;
                    seen.Add(id);
                    if (opts != null) { var row = new Dictionary<string, object>(opts) { ["entity"] = id }; rows.Add(row); }
                    else rows.Add(id);
                }
            }
            var sort = Json.O(cfg, "sort");
            if (sort != null)
            {
                string method = Json.S(sort, "method") ?? "none";
                Func<object, string> idOf = x => x is string xs ? xs : Json.S(x, "entity");
                IEnumerable<object> sorted = rows;
                if (method == "name") sorted = rows.OrderBy(x => Format.Name(c.S.State(idOf(x)), null), StringComparer.OrdinalIgnoreCase);
                else if (method == "state") sorted = rows.OrderBy(x => c.S.State(idOf(x))?.Num ?? double.MaxValue).ThenBy(x => c.S.State(idOf(x))?.State);
                else if (method == "last_changed") sorted = rows.OrderByDescending(x => c.S.State(idOf(x))?.LastChanged ?? 0);
                else if (method == "entity_id") sorted = rows.OrderBy(x => idOf(x));
                if (Json.B(sort, "reverse", false)) sorted = sorted.Reverse();
                rows = sorted.ToList();
                if (Json.Get(sort, "count") is double cnt) rows = rows.Take((int)cnt).ToList();
            }
            if (rows.Count == 0 && !Json.B(cfg, "show_empty", true)) return null;
            var card = Json.O(cfg, "card") is Dictionary<string, object> inner ? new Dictionary<string, object>(inner) : new Dictionary<string, object> { ["type"] = "entities" };
            string key = Json.S(cfg, "card_param") ?? (T(card) == "custom:mushroom-chips-card" ? "chips" : "entities");
            if (key == "cards") card[key] = rows.Select(x => (object)new Dictionary<string, object> { ["type"] = "tile", ["entity"] = x is string xs ? xs : Json.S(x, "entity") }).ToList();
            else card[key] = rows;
            CopyLayout(cfg, card);
            return card;
        }

        // ---------- week-planner-card: a column per day ----------
        private static SortedDictionary<DateTime, List<CalEvent>> WeekDays(Ctx c, Dictionary<string, object> cfg)
        {
            var cals = new List<(string, string)>();
            foreach (var x in Json.A(cfg, "calendars") ?? Json.A(cfg, "entities") ?? new List<object>())
            {
                if (x is string xs) cals.Add((xs, null));
                else if (Json.S(x, "entity") is string id) cals.Add((id, Theme.Color(Json.S(x, "color"))));
            }
            int days = (int)Json.N(cfg, "days", 7);
            var today = c.S.NowLocal.Date;
            string startAt = (Json.S(cfg, "startingDay") ?? "today").ToLowerInvariant();
            var start = today;
            var dows = new[] { "sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday" };
            int want = Array.IndexOf(dows, startAt);
            if (want >= 0) start = today.AddDays(-(((int)today.DayOfWeek - want + 7) % 7));
            else if (startAt == "yesterday") start = today.AddDays(-1);
            var evs = c.S.Events(cals, days, start);
            var map = new SortedDictionary<DateTime, List<CalEvent>>();
            for (int i = 0; i < days; i++) map[start.AddDays(i)] = new List<CalEvent>();
            foreach (var e in evs)
                for (var d = e.Start.Date; d < (e.AllDay ? e.End : e.End.Date.AddDays(e.End.TimeOfDay > TimeSpan.Zero ? 1 : 0)) || d == e.Start.Date; d = d.AddDays(1))
                {
                    if (map.TryGetValue(d, out var l)) l.Add(e);
                    if (!e.AllDay || d >= e.End.AddDays(-1)) break;
                }
            return map;
        }

        private static Dictionary<string, object> WeekPlanner(Ctx c, Dictionary<string, object> cfg, double w, double h)
        {
            var P = c.P; var card = Card(c, w, h);
            double y = P.Pad;
            Title(c, card, Json.S(cfg, "title"), w, ref y);
            var days = WeekDays(c, cfg);
            int n = Math.Max(1, days.Count);
            double cw = (w - 2 * P.Pad - (n - 1) * 6) / n;
            var today = c.S.NowLocal.Date;
            int i = 0;
            foreach (var kv in days)
            {
                double x = P.Pad + i * (cw + 6);
                bool isToday = kv.Key == today;
                Ui.Add(card, Ui.Box(x, y, cw, h - y - P.Pad, isToday ? Theme.WithAlpha(P.Accent, 0.12) : "#ffffff0a", 8));
                Ui.Add(card, Ui.Text(x + 8, y + 4, cw - 16, 22, kv.Key.ToString("ddd", CultureInfo.InvariantCulture).ToUpperInvariant(), 12, isToday ? P.Accent : P.Secondary, "medium"));
                Ui.Add(card, Ui.Text(x + 8, y + 22, cw - 16, 28, kv.Key.Day.ToString(), 22, isToday ? P.Accent : P.Text, isToday ? "bold" : "light"));
                double ey = y + 56;
                foreach (var e in kv.Value.OrderBy(e => e.AllDay ? 0 : 1).ThenBy(e => e.Start))
                {
                    if (ey + 42 > h - P.Pad) break;
                    string col = e.Color ?? CalColors[Math.Max(0, Cals(new Dictionary<string, object> { ["entities"] = Json.Get(cfg, "calendars") ?? Json.Get(cfg, "entities") }).Select(z => z.Item1).ToList().IndexOf(e.CalendarId)) % CalColors.Length];
                    var chip = Ui.Box(x + 4, ey, cw - 8, 40, Theme.WithAlpha(col, 0.35), 6);
                    Ui.Add(chip, Ui.Box(0, 0, 4, 40, col, 2));
                    Ui.Add(chip, Ui.Text(10, 2, cw - 22, 18, e.AllDay ? "All day" : e.Start.ToString("h:mm tt", CultureInfo.InvariantCulture).ToLowerInvariant(), 11, "#ffffffb0"));
                    Ui.Add(chip, Ui.Text(10, 18, cw - 22, 20, e.Summary, 13, "#ffffff"));
                    Ui.Add(card, chip);
                    ey += 46;
                }
                i++;
            }
            return card;
        }

        // ---------- weather-chart-card ----------
        private static Dictionary<string, object> WeatherChart(Ctx c, Dictionary<string, object> cfg, double w, double h)
        {
            var P = c.P; var e = c.E(cfg); var card = Card(c, w, h);
            if (e == null) return card;
            string unit = e.A("temperature_unit") ?? "°";
            double y = P.Pad;
            if (Json.B(cfg, "show_main", true))
            {
                Ui.Add(card, Ui.Icon(P.Pad, y, 56, Format.WeatherIcon(e.State), "#ffd54f"));
                string temp = Json.Get(e.Attr, "temperature") is double t ? Format.Number(t, 0) + unit : "";
                Ui.Add(card, Ui.Text(P.Pad + 68, y + 2, 200, 40, temp, 32, P.Text));
                Ui.Add(card, Ui.Text(P.Pad + 68, y + 38, w * 0.5, 22, Format.State(e, null, c.S.Tz, c.S.NowUtc).Replace("Partlycloudy", "Partly cloudy").Replace("Clear-night", "Clear"), 14, P.Secondary));
                y += 70;
            }
            string type = Json.S(Json.O(cfg, "forecast"), "type") ?? "daily";
            var fc = c.S.Forecasts(e.Id, type == "hourly" ? "hourly" : "daily") ?? new List<Forecast>();
            int n = Math.Min(fc.Count, (int)Json.N(Json.O(cfg, "forecast"), "number_of_forecasts", type == "hourly" ? 12 : 7));
            if (n < 2) return card;
            var f = fc.Take(n).ToList();
            double gx = P.Pad, gw = w - 2 * P.Pad, top = y + 26, gh = h - top - P.Pad - 26;
            var xs = Enumerable.Range(0, n).Select(i => (double)i).ToList();
            var hi = f.Select(x => x.Hi ?? 0).ToList();
            var lo = f.Select(x => x.Lo ?? x.Hi ?? 0).ToList();
            double mn = Math.Min(hi.Min(), lo.Min()) - 2, mx = Math.Max(hi.Max(), lo.Max()) + 2;
            var pp = f.Select(x => x.Precip ?? 0).ToList();
            double colW = gw / n;
            // precipitation probability bars under the lines
            Ui.Add(card, Ui.SvgImg(gx, top, gw, gh, Svg.Bars(pp.Select(v => v / 100.0 * (mx - mn) * 0.5).ToList(), (int)gw, (int)gh, Theme.WithAlpha(Theme.Named["light-blue"], 0.35))));
            // lines through the column centres
            var cx = xs.Select(i => i + 0.5).ToList();
            Ui.Add(card, Ui.SvgImg(gx, top, gw, gh, Svg.Graph(cx, hi, (int)gw, (int)gh, Theme.Named["orange"], 3, 0, mn, mx, 0, n)));
            if (f.Any(x => x.Lo != null)) Ui.Add(card, Ui.SvgImg(gx, top, gw, gh, Svg.Graph(cx, lo, (int)gw, (int)gh, Theme.Named["light-blue"], 3, 0, mn, mx, 0, n)));
            for (int i = 0; i < n; i++)
            {
                double x = gx + i * colW;
                string label = type == "hourly" ? f[i].Time.ToString("h tt", CultureInfo.InvariantCulture).ToLowerInvariant() : f[i].Time.ToString("ddd", CultureInfo.InvariantCulture);
                Ui.Add(card, Ui.Text(x, y, colW, 22, label, 12, P.Secondary, null, "center"));
                Ui.Add(card, Ui.Icon(x + (colW - 22) / 2, h - P.Pad - 24, 22, Format.WeatherIcon(f[i].Condition), "#ffd54f"));
                double yHi = top + (1 - (hi[i] - mn) / (mx - mn)) * gh;
                Ui.Add(card, Ui.Text(x, yHi - 22, colW, 18, Format.Number(hi[i], 0) + "°", 12, P.Text, null, "center"));
                if (f[i].Lo != null) { double yLo = top + (1 - (lo[i] - mn) / (mx - mn)) * gh; Ui.Add(card, Ui.Text(x, yLo + 4, colW, 18, Format.Number(lo[i], 0) + "°", 11, P.Secondary, null, "center")); }
            }
            return card;
        }

        // ---------- layout-card ----------
        // A layout-card (or a custom:*-layout view): places its cards and returns their height; draws into `into` if given.
        public static (double h, int n) RunLayout(Ctx c, Dictionary<string, object> cfg, double w, Dictionary<string, object> into) => LayoutCard(c, cfg, w, into);

        private static (double h, int n) LayoutCard(Ctx c, Dictionary<string, object> cfg, double w, Dictionary<string, object> into)
        {
            string lt = (Json.S(cfg, "layout_type") ?? Json.S(cfg, "type") ?? "masonry").ToLowerInvariant().Replace("custom:", "").Replace("-layout", "");
            var cards = (Json.A(cfg, "cards") ?? new List<object>()).OfType<Dictionary<string, object>>().ToList();
            var layout = Json.O(cfg, "layout") ?? new Dictionary<string, object>();
            switch (lt)
            {
                case "grid": return LayoutEngine.Grid(c, cards, layout, w, into);
                case "horizontal": return LayoutEngine.Horizontal(c, cards, layout, w, into);
                case "vertical": return LayoutEngine.Vertical(c, cards, w, into);
                default: return LayoutEngine.Masonry(c, cards, layout, w, into);
            }
        }

        // ---------- Mushroom details ----------
        // person card: photo instead of the icon; light card: the light's colour; lock/cover/climate: state wording.
        private static string MushroomPicture(Ctx c, Dictionary<string, object> cfg, Ent e) =>
            Json.B(cfg, "use_entity_picture", T(cfg) == "custom:mushroom-person-card") ? Picture(c, e) : null;

        private static string MushroomColor(Ctx c, Dictionary<string, object> cfg, Ent e)
        {
            if (T(cfg) == "custom:mushroom-light-card" && e != null && e.State == "on" && Json.B(cfg, "use_light_color", true)) return RgbOf(e) ?? Theme.Named["amber"];
            return null;
        }
    }

    // Placement for layout-card and the custom:*-layout views (subset of CSS grid: column templates with px / fr / auto /
    // repeat(), named areas, grid-column / grid-row spans, gaps; masonry by column count; horizontal; vertical).
    internal static class LayoutEngine
    {
        private static double Px(string v, double def)
        {
            if (v == null) return def;
            var m = Regex.Match(v, @"([\d.]+)\s*(px)?");
            return m.Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : def;
        }

        private static List<string> Tokens(string tpl)
        {
            var outp = new List<string>();
            if (string.IsNullOrWhiteSpace(tpl)) return outp;
            tpl = Regex.Replace(tpl, @"repeat\(\s*(\d+)\s*,\s*([^)]+?)\s*\)", m => string.Join(" ", Enumerable.Repeat(m.Groups[2].Value.Replace(" ", ""), int.Parse(m.Groups[1].Value))));
            tpl = Regex.Replace(tpl, @"minmax\([^)]*\)", "1fr");
            foreach (var t in tpl.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)) outp.Add(t);
            return outp;
        }

        private static List<double> Sizes(List<string> toks, double total, double gap)
        {
            double fixedSum = 0, fr = 0;
            foreach (var t in toks) { if (t.EndsWith("fr")) fr += Px(t, 1); else if (t.EndsWith("px")) fixedSum += Px(t, 0); else if (t.EndsWith("%")) fixedSum += total * Px(t, 0) / 100; else fr += 1; }
            double free = Math.Max(0, total - fixedSum - gap * Math.Max(0, toks.Count - 1));
            return toks.Select(t => t.EndsWith("px") ? Px(t, 0) : t.EndsWith("%") ? total * Px(t, 0) / 100 : free * (t.EndsWith("fr") ? Px(t, 1) : 1) / Math.Max(1e-9, fr)).ToList();
        }

        private static (Dictionary<string, object> eff, Dictionary<string, object> vl) Prep(Ctx c, Dictionary<string, object> cfg)
        {
            if (!c.Visible(cfg)) return (null, null);
            var eff = Cards.Effective(c, cfg);
            return (eff, Json.O(cfg, "view_layout") ?? new Dictionary<string, object>());
        }

        public static (double, int) Grid(Ctx c, List<Dictionary<string, object>> cards, Dictionary<string, object> layout, double W, Dictionary<string, object> into)
        {
            double gap = Px(Json.S(layout, "grid-gap") ?? Json.S(layout, "gap"), 8), cgap = Px(Json.S(layout, "grid-column-gap") ?? Json.S(layout, "column-gap"), gap), rgap = Px(Json.S(layout, "grid-row-gap") ?? Json.S(layout, "row-gap"), gap);
            // named areas
            var areas = new Dictionary<string, (int r, int c, int rs, int cs)>();
            var areaRows = Regex.Matches(Json.S(layout, "grid-template-areas") ?? "", "\"([^\"]*)\"").Cast<Match>().Select(m => m.Groups[1].Value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)).ToList();
            for (int r = 0; r < areaRows.Count; r++)
                for (int k = 0; k < areaRows[r].Length; k++)
                {
                    string nm = areaRows[r][k];
                    if (nm == ".") continue;
                    if (!areas.TryGetValue(nm, out var a)) areas[nm] = (r, k, 1, 1);
                    else areas[nm] = (a.r, a.c, Math.Max(a.rs, r - a.r + 1), Math.Max(a.cs, k - a.c + 1));
                }
            var colToks = Tokens(Json.S(layout, "grid-template-columns"));
            int cols = Math.Max(Math.Max(colToks.Count, areaRows.Count > 0 ? areaRows.Max(x => x.Length) : 0), 1);
            while (colToks.Count < cols) colToks.Add("1fr");
            var colW = Sizes(colToks, W, cgap);
            var colX = new List<double>(); double cx = 0; foreach (var cw in colW) { colX.Add(cx); cx += cw + cgap; }
            // place
            var placed = new List<(Dictionary<string, object> eff, int r, int c, int rs, int cs)>();
            var occ = new List<bool[]>();
            bool Free(int r, int k, int rs, int cs) { for (int rr = r; rr < r + rs; rr++) { while (occ.Count <= rr) occ.Add(new bool[cols]); for (int kk = k; kk < k + cs; kk++) if (kk >= cols || occ[rr][kk]) return false; } return true; }
            void Take(int r, int k, int rs, int cs) { for (int rr = r; rr < r + rs; rr++) { while (occ.Count <= rr) occ.Add(new bool[cols]); for (int kk = k; kk < k + cs; kk++) occ[rr][kk] = true; } }
            int Span(string v) { var m = Regex.Match(v ?? "", @"span\s+(\d+)"); return m.Success ? int.Parse(m.Groups[1].Value) : 1; }
            int Start(string v) { var m = Regex.Match(v ?? "", @"^\s*(\d+)"); return m.Success ? int.Parse(m.Groups[1].Value) - 1 : -1; }
            foreach (var cfg in cards)
            {
                var (eff, vl) = Prep(c, cfg);
                if (eff == null) continue;
                string ga = Json.S(vl, "grid-area");
                if (ga != null && areas.TryGetValue(ga, out var a)) { placed.Add((eff, a.r, a.c, a.rs, a.cs)); Take(a.r, a.c, a.rs, a.cs); continue; }
                int cs = Math.Min(cols, Span(Json.S(vl, "grid-column"))), rs = Span(Json.S(vl, "grid-row"));
                int c0 = Start(Json.S(vl, "grid-column")), r0 = Start(Json.S(vl, "grid-row"));
                int pr = -1, pc = -1;
                for (int r = Math.Max(0, r0); pr < 0; r++)
                    for (int k = c0 >= 0 ? c0 : 0; k + cs <= cols && (c0 < 0 || k == c0); k++)
                        if (Free(r, k, rs, cs)) { pr = r; pc = k; break; }
                Take(pr, pc, rs, cs);
                placed.Add((eff, pr, pc, rs, cs));
            }
            int rows = placed.Count == 0 ? 0 : placed.Max(p => p.r + p.rs);
            // row heights: fixed from grid-template-rows where given, else from the cards in them
            var rowToks = Tokens(Json.S(layout, "grid-template-rows"));
            var rowH = new double[rows];
            for (int r = 0; r < rows; r++) if (r < rowToks.Count && rowToks[r].EndsWith("px")) rowH[r] = Px(rowToks[r], 0);
            double Wd(int k, int cs) => colX[k + cs - 1] + colW[k + cs - 1] - colX[k];
            foreach (var p in placed.OrderBy(p => p.rs))
            {
                double need = Cards.Height(c, p.eff, Wd(p.c, p.cs));
                double have = Enumerable.Range(p.r, p.rs).Sum(r => rowH[r]) + (p.rs - 1) * rgap;
                if (need > have) rowH[p.r + p.rs - 1] += need - have;
            }
            var rowY = new double[rows]; double ry = 0; for (int r = 0; r < rows; r++) { rowY[r] = ry; ry += rowH[r] + rgap; }
            if (into != null)
                foreach (var p in placed)
                {
                    double w = Wd(p.c, p.cs), h = Enumerable.Range(p.r, p.rs).Sum(r => rowH[r]) + (p.rs - 1) * rgap;
                    var n = Cards.Draw(c, p.eff, w, h);
                    if (n == null) continue;
                    n["x"] = colX[p.c]; n["y"] = rowY[p.r];
                    Ui.Add(into, n);
                }
            return (rows == 0 ? 0 : ry - rgap, placed.Count);
        }

        public static (double, int) Masonry(Ctx c, List<Dictionary<string, object>> cards, Dictionary<string, object> layout, double W, Dictionary<string, object> into)
        {
            double minW = Px(Json.S(layout, "width") ?? (Json.Get(layout, "width") is double wd ? wd.ToString(CultureInfo.InvariantCulture) : null), 300);
            int maxCols = (int)Json.N(layout, "max_cols", 4);
            int n = Math.Max(1, Math.Min(maxCols, (int)Math.Floor((W + 8) / (minW + 8))));
            double gap = 8, colW = (W - gap * (n - 1)) / n;
            var heights = new double[n]; int count = 0;
            foreach (var cfg in cards)
            {
                var (eff, vl) = Prep(c, cfg);
                if (eff == null) continue;
                int col = Json.Get(vl, "column") is double vc ? Math.Max(0, Math.Min(n - 1, (int)vc - 1)) : Array.IndexOf(heights, heights.Min());
                double h = Cards.Height(c, eff, colW);
                if (into != null)
                {
                    var node = Cards.Draw(c, eff, colW, h);
                    if (node != null) { node["x"] = col * (colW + gap); node["y"] = heights[col]; Ui.Add(into, node); }
                }
                heights[col] += h + gap; count++;
            }
            return (Math.Max(0, heights.Max() - gap), count);
        }

        public static (double, int) Horizontal(Ctx c, List<Dictionary<string, object>> cards, Dictionary<string, object> layout, double W, Dictionary<string, object> into)
        {
            var effs = cards.Select(x => Prep(c, x).eff).Where(x => x != null).ToList();
            if (effs.Count == 0) return (0, 0);
            int n = Math.Min(effs.Count, (int)Json.N(layout, "max_cols", effs.Count));
            double gap = 8, colW = (W - gap * (n - 1)) / n;
            var heights = new double[n];
            for (int i = 0; i < effs.Count; i++)
            {
                int col = i % n;
                double h = Cards.Height(c, effs[i], colW);
                if (into != null) { var node = Cards.Draw(c, effs[i], colW, h); if (node != null) { node["x"] = col * (colW + gap); node["y"] = heights[col]; Ui.Add(into, node); } }
                heights[col] += h + gap;
            }
            return (heights.Max() - gap, effs.Count);
        }

        public static (double, int) Vertical(Ctx c, List<Dictionary<string, object>> cards, double W, Dictionary<string, object> into)
        {
            double y = 0; int count = 0;
            foreach (var cfg in cards)
            {
                var eff = Prep(c, cfg).eff;
                if (eff == null) continue;
                double h = Cards.Height(c, eff, W);
                if (into != null) { var node = Cards.Draw(c, eff, W, h); if (node != null) { node["x"] = 0.0; node["y"] = y; Ui.Add(into, node); } }
                y += h + 8; count++;
            }
            return (Math.Max(0, y - 8), count);
        }
    }
}
