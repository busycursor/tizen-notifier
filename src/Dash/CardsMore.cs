// More Lovelace cards: calendar month and day views, media control, thermostat, light, alarm panel, area, logbook,
// statistic, statistics-graph, and the custom mini-graph-card and clock-weather-card. Pure C#, read-only.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace TizenNotifier
{
    internal static partial class Cards
    {
        private static readonly HashSet<string> KnownMore = new HashSet<string>
        {
            "media-control", "thermostat", "light", "alarm-panel", "area", "logbook", "statistic", "humidifier",
            "custom:mini-graph-card", "custom:clock-weather-card",
        };

        private static (int, int)? DefaultMore(Dictionary<string, object> cfg)
        {
            switch (T(cfg))
            {
                case "calendar": return (12, 0);
                case "media-control": return (0, 0);
                case "thermostat": case "humidifier": return (12, 0);
                case "light": return (12, 0);
                case "alarm-panel": return (6, 2);
                case "area": return Json.S(cfg, "aspect_ratio") != null ? (6, 0) : (6, 3);
                case "logbook": return (0, 6);
                case "statistic": return (6, 2);
                case "custom:mini-graph-card": return (0, 0);
                case "custom:clock-weather-card": return (0, 0);
            }
            return null;
        }

        private static double? HeightMore(Ctx c, Dictionary<string, object> cfg, double w)
        {
            var P = c.P;
            switch (T(cfg))
            {
                case "calendar":
                {
                    string v = Json.S(cfg, "initial_view") ?? "dayGridMonth";
                    double title = Json.S(cfg, "title") != null ? 40 : 0;
                    // FullCalendar's day grid keeps a 1.2 aspect ratio (452 wide -> 376 tall in the frontend)
                    if (v == "dayGridMonth" || v == "dayGridDay") return w / 1.2 + title;
                    return null;   // listWeek: the agenda (Cards.cs)
                }
                case "media-control": return 145;
                case "thermostat": case "humidifier": return 2 * P.Pad + 28 + Math.Min(w * 0.59, 260);   // 440 wide -> 312
                case "light": return 2 * P.Pad + 40 + Math.Min(w * 0.455, 200);                            // 440 wide -> 264
                case "alarm-panel": return 120;
                case "area": return (Json.S(cfg, "aspect_ratio") != null ? w / Aspect(cfg) : w * 0.6) + 56;
                case "logbook":
                {
                    var rows = c.S.Logbook(LogbookIds(cfg), Json.N(cfg, "hours_to_show", 24)) ?? new List<Dictionary<string, object>>();
                    int n = Math.Min(rows.Count, (int)Json.N(cfg, "max_entries", 8));
                    return 2 * P.Pad + (Json.S(cfg, "title") != null ? 38 : 0) + 30 + Math.Max(1, n) * 62;
                }
                case "statistic": return 120;
                case "custom:mini-graph-card":
                    return P.Pad + 24 + 46 + (Json.B(Json.O(cfg, "show"), "extrema", false) ? 66 : 0) + (Json.B(Json.O(cfg, "show"), "legend", true) && MiniEntities(cfg).Count > 1 ? 26 : 0) + Json.N(cfg, "height", 100) + 30;
                case "custom:clock-weather-card":
                {
                    int rows = Json.B(cfg, "hide_forecast_section", false) ? 0 : (int)Json.N(cfg, "forecast_rows", 5);
                    return 2 * P.Pad + (Json.B(cfg, "hide_today_section", false) ? 0 : 120) + rows * 34;
                }
            }
            return null;
        }

        private static Dictionary<string, object> DrawMore(Ctx c, Dictionary<string, object> cfg, double w, double h)
        {
            switch (T(cfg))
            {
                case "calendar":
                {
                    string v = Json.S(cfg, "initial_view") ?? "dayGridMonth";
                    if (v == "dayGridMonth") return Month(c, cfg, w, h);
                    if (v == "dayGridDay") return Day(c, cfg, w, h);
                    return null;
                }
                case "media-control": return Media(c, cfg, w, h);
                case "thermostat": case "humidifier": return Thermostat(c, cfg, w, h);
                case "light": return Light(c, cfg, w, h);
                case "alarm-panel": return Alarm(c, cfg, w, h);
                case "area": return Area(c, cfg, w, h);
                case "logbook": return LogbookCard(c, cfg, w, h);
                case "statistic": return Statistic(c, cfg, w, h);
                case "statistics-graph": return StatisticsGraph(c, cfg, w, h);
                case "custom:mini-graph-card": return MiniGraph(c, cfg, w, h);
                case "custom:clock-weather-card": return ClockWeather(c, cfg, w, h);
            }
            return null;
        }

        private static bool HasMore(string t) => KnownMore.Contains(t);

        // ---------- calendar: month grid ----------
        private static List<(string, string)> Cals(Dictionary<string, object> cfg)
        {
            var cals = new List<(string, string)>();
            foreach (var x in Json.A(cfg, "entities") ?? new List<object>())
            {
                if (x is string xs) cals.Add((xs, null));
                else if (Json.S(x, "entity") is string id) cals.Add((id, Theme.Color(Json.S(x, "color"))));
            }
            return cals;
        }

        private static string CalColor(Dictionary<string, object> cfg, CalEvent e)
        {
            if (e.Color != null) return e.Color;
            var ids = Cals(cfg).Select(x => x.Item1).ToList();
            int i = Math.Max(0, ids.IndexOf(e.CalendarId));
            return CalColors[i % CalColors.Length];
        }

        private static Dictionary<string, object> Month(Ctx c, Dictionary<string, object> cfg, double w, double h)
        {
            var today = c.S.NowLocal.Date;
            var first = new DateTime(today.Year, today.Month, 1);
            var gridStart = first.AddDays(-(int)first.DayOfWeek);   // weeks start on Sunday
            return DayGrid(c, cfg, w, h, gridStart, 7, 6, first.ToString("MMMM yyyy", CultureInfo.InvariantCulture), d => d.Month == first.Month);
        }

        private static Dictionary<string, object> Day(Ctx c, Dictionary<string, object> cfg, double w, double h)
        {
            var today = c.S.NowLocal.Date;
            return DayGrid(c, cfg, w, h, today, 1, 1, today.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture), d => true);
        }

        private static List<CalEvent> DayEvents(Ctx c, Dictionary<string, object> cfg, DateTime day) =>
            c.S.Events(Cals(cfg), 1, day).Where(e => e.AllDay ? (e.Start <= day && e.End > day) : e.Start.Date == day).ToList();

        private static string Esc(string s) => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

        // The frontend's day grid (FullCalendar dayGrid): weekday header, day numbers centred at the top of each cell (today
        // in a circle), all-day and multi-day events as bars spanning their days within each week row, timed events as
        // "• time title" lines, "+N more" when a cell is full. Rows share the height.
        private static Dictionary<string, object> DayGrid(Ctx c, Dictionary<string, object> cfg, double w, double h, DateTime start, int cols, int rows,
            string heading, Func<DateTime, bool> inRange)
        {
            var P = c.P; var card = Card(c, w, h);
            double y = P.Pad;
            if (Json.S(cfg, "title") is string t) { Ui.Add(card, Ui.Text(P.Pad + 4, y, w - 2 * P.Pad, 30, t, 22, P.Text)); y += 36; }
            bool wide = w > 700;
            Ui.Add(card, Ui.Text(P.Pad + 4, y, w - 2 * P.Pad - 8, 32, heading, wide ? 22 : 20, P.Text, null, wide ? "center" : "left"));
            y += 40;
            double gx = P.Pad / 2, gw = w - P.Pad, cw = gw / cols;
            for (int d = 0; d < cols; d++)
                Ui.Add(card, Ui.Text(gx + d * cw, y, cw, 22, start.AddDays(d).ToString(cols == 1 ? "dddd" : "ddd", CultureInfo.InvariantCulture).ToUpperInvariant(), 11, P.Secondary, "medium", "center"));
            y += 22;
            double gy = y, ch = (h - y - P.Pad / 2) / rows;
            Ui.Add(card, Ui.Box(gx, gy, gw, 1, P.Divider));
            for (int d = 1; d < cols; d++) Ui.Add(card, Ui.Box(gx + d * cw, gy - 22, 1, rows * ch + 22, P.Divider));
            var evs = c.S.Events(Cals(cfg), cols * rows, start);
            var today = c.S.NowLocal.Date;
            const double lh = 24;
            int levels = Math.Max(1, (int)((ch - 32) / lh));
            for (int r = 0; r < rows; r++)
            {
                double ry = gy + r * ch;
                if (r > 0) Ui.Add(card, Ui.Box(gx, ry, gw, 1, P.Divider));
                var ws = start.AddDays(r * cols); var we = ws.AddDays(cols);
                for (int d = 0; d < cols && cols > 1; d++)   // the day view has no day numbers
                {
                    var day = ws.AddDays(d);
                    double cx = gx + d * cw + cw / 2;
                    if (day == today) { Ui.Add(card, Ui.Box(cx - 13, ry + 4, 26, 26, P.Accent, 13)); Ui.Add(card, Ui.Text(cx - 13, ry + 4, 26, 26, day.Day.ToString(), 13, "#ffffff", "medium", "center")); }
                    else Ui.Add(card, Ui.Text(cx - 20, ry + 4, 40, 26, day.Day.ToString(), 13, inRange(day) ? P.Text : P.Secondary, null, "center"));
                }
                var used = new List<bool[]>();
                for (int l = 0; l < 64; l++) used.Add(new bool[cols]);
                var hidden = new int[cols];
                // bars: all-day or multi-day events clipped to this row, each in the lowest free level
                var bars = evs.Where(e => (e.AllDay || e.End.Date > e.Start.Date) && e.Start < we && e.End > ws)
                              .OrderBy(e => e.Start).ThenByDescending(e => e.End - e.Start).ToList();
                foreach (var e in bars)
                {
                    int s0 = Math.Max(0, (int)(e.Start.Date - ws).TotalDays);
                    var endEx = e.AllDay ? e.End.Date : e.End.Date.AddDays(1);
                    int s1 = Math.Min(cols, (int)Math.Ceiling((endEx - ws).TotalDays));
                    if (s1 <= s0) continue;
                    int lvl = 0;
                    while (lvl < 63 && Enumerable.Range(s0, s1 - s0).Any(k => used[lvl][k])) lvl++;
                    for (int k = s0; k < s1; k++) used[lvl][k] = true;
                    if (lvl >= levels - 1) { for (int k = s0; k < s1; k++) hidden[k]++; continue; }
                    double bx = gx + s0 * cw + 2, bw = (s1 - s0) * cw - 4, by = ry + 32 + lvl * lh;
                    var bar = Ui.Box(bx, by, bw, lh - 3, CalColor(cfg, e), 4);
                    Ui.Add(bar, Ui.Text(5, 0, bw - 10, lh - 3, e.Summary, 11, "#1b1b1b"));
                    Ui.Add(card, bar);
                }
                // timed events: per day, in that day's next free levels
                for (int d = 0; d < cols; d++)
                {
                    var day = ws.AddDays(d);
                    foreach (var e in evs.Where(e => !e.AllDay && e.End.Date <= e.Start.Date && e.Start.Date == day).OrderBy(e => e.Start))
                    {
                        int lvl = 0;
                        while (lvl < 63 && used[lvl][d]) lvl++;
                        used[lvl][d] = true;
                        if (lvl >= levels - 1) { hidden[d]++; continue; }
                        double x = gx + d * cw + 4, ty = ry + 32 + lvl * lh, tw = cw - 8;
                        Ui.Add(card, Ui.Box(x + 2, ty + 7, 7, 7, CalColor(cfg, e), 4));
                        string time = e.Start.ToString(e.Start.Minute == 0 ? "h tt" : "h:mm tt", CultureInfo.InvariantCulture);
                        var tn = Ui.Text(x + 14, ty, tw - 14, lh - 3, time + " <font weight='bold'>" + Esc(e.Summary) + "</font>", 11, P.Text);
                        tn["markup"] = true;
                        Ui.Add(card, tn);
                    }
                    if (hidden[d] > 0) Ui.Add(card, Ui.Text(gx + d * cw + 4, ry + 32 + (levels - 1) * lh, cw - 8, lh - 3, "+" + hidden[d] + " more", 11, P.Secondary));
                }
            }
            return card;
        }

        // ---------- media control ----------
        private static Dictionary<string, object> Media(Ctx c, Dictionary<string, object> cfg, double w, double h)
        {
            var P = c.P; var e = c.E(cfg); var card = Card(c, w, h);
            if (e == null) return card;
            string art = e.State != "off" ? Picture(c, e) : null;
            double tx = P.Pad + 4;
            if (art != null)
            {
                // the frontend: artwork against the right edge at full height, fading into the card on its left
                double aw = Math.Min(w * 0.45, h * 1.1);
                Ui.Add(card, Ui.Image(w - aw, 0, aw, h, art, P.Radius));
                var fade = Ui.Box(w - aw - 1, 0, aw * 0.75, h);
                fade["gradient"] = new Dictionary<string, object> { ["colors"] = new List<object> { P.CardBg, Theme.WithAlpha(P.CardBg, 0) }, ["angle"] = 0.0 };
                Ui.Add(card, fade);
            }
            double tw = w - 2 * P.Pad - (art != null ? Math.Min(w * 0.45, h * 1.1) * 0.5 : 0) - 4;
            Ui.Add(card, Ui.Icon(tx, P.Pad + 2, 20, Format.Icon(e, Json.S(cfg, "icon"), c.S.Icons), Format.StateColor(e) ?? P.Secondary));
            Ui.Add(card, Ui.Text(tx + 28, P.Pad, tw - 28, 24, Format.Name(e, Json.S(cfg, "name")) + (e.A("app_name") is string app ? " · " + app : ""), 13, P.Secondary));
            string title = e.A("media_title"), artist = e.A("media_artist") ?? e.A("media_series_title") ?? e.A("media_album_name");
            if (e.State == "off" || e.State == "standby" || (title == null && artist == null))
                Ui.Add(card, Ui.Text(tx, P.Pad + 34, tw, 30, c.S.StateText(e), 18, P.Text));
            else
            {
                Ui.Add(card, Ui.Text(tx, P.Pad + 34, tw, 30, title ?? "", 18, P.Text));
                if (artist != null) Ui.Add(card, Ui.Text(tx, P.Pad + 62, tw, 24, artist, 14, P.Secondary));
            }
            // progress (position as of position_updated_at, advancing while playing)
            if (Json.Get(e.Attr, "media_duration") is double dur && dur > 0 && Json.Get(e.Attr, "media_position") is double pos)
            {
                double p = pos;
                if (e.State == "playing" && DateTime.TryParse(e.A("media_position_updated_at"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at))
                    p += (c.S.NowUtc - at).TotalSeconds;
                double f = Math.Max(0, Math.Min(1, p / dur));
                Ui.Add(card, Ui.Box(tx, h - P.Pad - 4, tw, 4, "#ffffff26", 2));
                Ui.Add(card, Ui.Box(tx, h - P.Pad - 4, Math.Max(1, tw * f), 4, P.Accent, 2));
            }
            return card;
        }

        // ---------- thermostat / humidifier ----------
        private static Dictionary<string, object> Thermostat(Ctx c, Dictionary<string, object> cfg, double w, double h)
        {
            var P = c.P; var e = c.E(cfg); var card = Card(c, w, h);
            if (e == null) return card;
            bool hum = e.Domain == "humidifier";
            double min = Json.N(e.Attr, hum ? "min_humidity" : "min_temp", hum ? 0 : 45), max = Json.N(e.Attr, hum ? "max_humidity" : "max_temp", hum ? 100 : 95);
            double? target = Json.Get(e.Attr, hum ? "humidity" : "temperature") as double?;
            double? cur = Json.Get(e.Attr, hum ? "current_humidity" : "current_temperature") as double?;
            string col = Format.StateColor(e) ?? P.Inactive;
            Ui.Add(card, Ui.Text(P.Pad, P.Pad, w - 2 * P.Pad, 24, Format.Name(e, Json.S(cfg, "name")), 16, P.Text, null, "center"));
            double s = Math.Min(w * 0.59, h - 2 * P.Pad - 28);
            double rx = (w - s) / 2, ry = P.Pad + 28;
            double f = target != null ? (target.Value - min) / Math.Max(1e-9, max - min) : 0;
            Ui.Add(card, Ui.SvgImg(rx, ry, s, s, Svg.Ring(f, col, "#ffffff22", 0.06)));
            // the target's handle on the ring
            double a = (135 + 270 * Math.Max(0, Math.Min(1, f))) * Math.PI / 180, hr = s * 0.42;
            Ui.Add(card, Ui.Box(rx + s / 2 + hr * Math.Cos(a) - 8, ry + s / 2 + hr * Math.Sin(a) - 8, 16, 16, "#ffffff", 8));
            string label = hum ? "Humidity" : e.A("hvac_action") is string act ? Cap(act) : Cap(e.State);
            Ui.Add(card, Ui.Text(rx, ry + s * 0.27, s, s * 0.1, label, Math.Max(13, s * 0.055), P.Text, "medium", "center"));
            string big = target != null ? Format.Number(target.Value, null) : "—";
            double bpx = Math.Max(24, s * 0.2);
            Ui.Add(card, Ui.Text(rx, ry + s * 0.37, s, bpx * 1.2, big, bpx, P.Text, "light", "center"));
            Ui.Add(card, Ui.Text(rx + s / 2 + Ui.Width(big, bpx) / 2 + 2, ry + s * 0.39, 40, bpx * 0.5, hum ? "%" : Format.TempUnit, bpx * 0.32, P.Text));
            if (cur != null)
            {
                string now = Format.Number(cur.Value, null) + " " + (hum ? "%" : Format.TempUnit);
                double nw = Ui.Width(now, 15) + 22;
                Ui.Add(card, Ui.Icon(rx + (s - nw) / 2, ry + s * 0.69 + 2, 16, hum ? "mdi:water-percent" : "mdi:thermometer", P.Text));
                Ui.Add(card, Ui.Text(rx + (s - nw) / 2 + 20, ry + s * 0.69, nw, 20, now, 15, P.Text));
            }
            return card;
        }

        private static string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1).Replace('_', ' ');

        // ---------- light ----------
        private static Dictionary<string, object> Light(Ctx c, Dictionary<string, object> cfg, double w, double h)
        {
            var P = c.P; var e = c.E(cfg); var card = Card(c, w, h);
            if (e == null) return card;
            double br = Json.Get(e.Attr, "brightness") is double b ? b / 255 : (e.State == "on" ? 1 : 0);
            string col = e.State == "on" ? (RgbOf(e) ?? Theme.Named["amber"]) : P.Inactive;
            double s = Math.Min(w * 0.455, h - 40 - 2 * P.Pad);
            double rx = (w - s) / 2, ry = P.Pad;
            Ui.Add(card, Ui.SvgImg(rx, ry, s, s, Svg.Ring(br, P.Accent, "#ffffff33", 0.07)));
            double a = (135 + 270 * br) * Math.PI / 180, hr = s * 0.42;
            if (e.State == "on") Ui.Add(card, Ui.Box(rx + s / 2 + hr * Math.Cos(a) - 6, ry + s / 2 + hr * Math.Sin(a) - 6, 12, 12, "#ffffff", 6));
            Ui.Add(card, Ui.Icon(rx + s * 0.27, ry + s * 0.25, s * 0.46, Format.Icon(e, Json.S(cfg, "icon"), c.S.Icons), col));
            Ui.Add(card, Ui.Text(P.Pad, ry + s + 6, w - 2 * P.Pad, 30, Format.Name(e, Json.S(cfg, "name")), 17, P.Text, null, "center"));
            return card;
        }

        private static string RgbOf(Ent e)
        {
            if (Json.Get(e.Attr, "rgb_color") is List<object> l && l.Count >= 3 && l.All(x => x is double))
                return "#" + string.Concat(l.Take(3).Select(x => ((int)(double)x).ToString("x2")));
            return null;
        }

        // ---------- alarm panel (state only; no keypad) ----------
        private static Dictionary<string, object> Alarm(Ctx c, Dictionary<string, object> cfg, double w, double h)
        {
            var P = c.P; var e = c.E(cfg); var card = Card(c, w, h);
            if (e == null) return card;
            string col = Format.StateColor(e) ?? P.Inactive;
            string icon = e.State == "disarmed" ? "mdi:shield-off-outline" : e.State == "triggered" ? "mdi:bell-ring" : e.State.StartsWith("arm") ? "mdi:shield-lock" : "mdi:shield-outline";
            Ui.Add(card, Ui.IconBadge(P.Pad, (h - 56) / 2, 56, icon, col, e.State != "disarmed"));
            Ui.Add(card, Ui.Text(P.Pad + 70, h / 2 - 28, w - 2 * P.Pad - 70, 24, Format.Name(e, Json.S(cfg, "name")), 14, P.Secondary));
            Ui.Add(card, Ui.Text(P.Pad + 70, h / 2 - 4, w - 2 * P.Pad - 70, 32, Cap(e.State), 22, P.Text, "medium"));
            return card;
        }

        // ---------- area ----------
        private static Dictionary<string, object> Area(Ctx c, Dictionary<string, object> cfg, double w, double h)
        {
            var P = c.P;
            string id = Json.S(cfg, "area");
            if (id == null || !c.S.Areas.TryGetValue(id, out var area)) { c.Unsupported("area (unknown area)"); return null; }
            var card = Card(c, w, h);
            double foot = 56, ph = h - foot;
            if (Json.S(area, "picture") is string pic) Ui.Add(card, Ui.Image(0, 0, w, ph, c.S.Ha.Url + pic, P.Radius));
            else Ui.Add(card, Ui.Icon((w - ph * 0.5) / 2, ph * 0.25, ph * 0.5, Json.S(area, "icon") ?? "mdi:texture-box", "#ffffff40"));
            var ents = c.S.EntitiesIn(id).Select(c.S.State).Where(x => x != null).ToList();
            string Sensor(string field, string dc)
            {
                var e = c.S.State(Json.S(area, field)) ?? ents.FirstOrDefault(x => x.Domain == "sensor" && x.A("device_class") == dc);
                return e == null || Format.Unavailable(e) ? null : c.S.StateText(e);
            }
            var bits = new[] { Sensor("temperature_entity_id", "temperature"), Sensor("humidity_entity_id", "humidity") }.Where(x => x != null).ToList();
            Ui.Add(card, Ui.Icon(P.Pad + 4, ph + 16, 24, Json.S(area, "icon") ?? "mdi:texture-box", P.Accent));
            Ui.Add(card, Ui.Text(P.Pad + 40, ph, w - 2 * P.Pad - 40, bits.Count > 0 ? 34 : foot, Json.S(area, "name") ?? id, 14, P.Text, "medium"));
            if (bits.Count > 0) Ui.Add(card, Ui.Text(P.Pad + 40, ph + 30, w - 2 * P.Pad - 40, 20, string.Join(" · ", bits), 12, P.Secondary));
            return card;
        }

        // ---------- logbook ----------
        private static List<string> LogbookIds(Dictionary<string, object> cfg) =>
            (Json.A(cfg, "target") != null ? null : Json.A(cfg, "entities") ?? new List<object>()).Select(x => x is string xs ? xs : Json.S(x, "entity")).Where(x => x != null).ToList();

        private static Dictionary<string, object> LogbookCard(Ctx c, Dictionary<string, object> cfg, double w, double h)
        {
            var P = c.P; var card = Card(c, w, h);
            double y = P.Pad;
            if (Json.S(cfg, "title") is string t) { Ui.Add(card, Ui.Text(P.Pad + 4, y, w - 2 * P.Pad, 32, t, 22, P.Text)); y += 38; }
            Ui.Add(card, Ui.Text(P.Pad + 4, y, w - 2 * P.Pad, 22, "Today · " + c.S.NowLocal.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture), 13, P.Text, "medium"));
            y += 30;
            var rows = c.S.Logbook(LogbookIds(cfg), Json.N(cfg, "hours_to_show", 24));
            if (rows == null || rows.Count == 0) { Ui.Add(card, Ui.Text(P.Pad + 4, y, w - 2 * P.Pad, 40, rows == null ? "Loading…" : "No activity found", 14, P.Secondary)); return card; }
            var shown = rows.Take((int)Json.N(cfg, "max_entries", 8)).ToList();
            double rh = 62, lx = P.Pad + 14;
            Ui.Add(card, Ui.Box(lx - 1, y + 14, 2, Math.Max(0, Math.Min((shown.Count - 1) * rh, h - y - 14)), P.Divider));
            foreach (var r in shown)
            {
                if (y + rh - 8 > h) break;
                var e = c.S.State(Json.S(r, "entity_id"));
                var then = e;
                if (Json.S(r, "state") is string st && e != null) then = new Ent { Id = e.Id, State = st, Attr = e.Attr };
                string name = Json.S(r, "name") ?? Format.Name(e, null);
                string what = Json.S(r, "message") ?? (then != null ? c.S.StateText(then) : "");
                string area = e != null && c.S.AreaOf(e.Id) is string aid && c.S.Areas.TryGetValue(aid, out var ar) ? Json.S(ar, "name") : null;
                string dot = then != null && Format.Active(then) ? Theme.Named["red"] : Theme.Named["green"];
                if (then != null && Format.StateColor(then) is string sc) dot = sc;
                Ui.Add(card, Ui.Box(lx - 5, y + 9, 10, 10, dot, 5));
                Ui.Add(card, Ui.Text(lx + 22, y, w - lx - 22 - P.Pad - 150, 26, name, 14, P.Text, "medium"));
                if (area != null) Ui.Add(card, Ui.Text(lx + 22, y + 24, w - lx - 22 - P.Pad - 150, 22, area, 12, P.Secondary));
                Ui.Add(card, Ui.Text(w - P.Pad - 150, y, 146, 26, what, 14, P.Text, null, "right"));
                var when = c.S.ToLocal(Json.N(r, "when", 0));
                Ui.Add(card, Ui.Text(w - P.Pad - 150, y + 24, 146, 22, when.ToString("h:mm:ss tt", CultureInfo.InvariantCulture), 12, P.Secondary, null, "right"));
                if (r != shown[shown.Count - 1]) Ui.Add(card, Ui.Box(lx + 22, y + rh - 8, w - lx - 22 - P.Pad, 1, P.Divider));
                y += rh;
            }
            return card;
        }

        // ---------- statistic ----------
        private static Dictionary<string, object> Statistic(Ctx c, Dictionary<string, object> cfg, double w, double h)
        {
            var P = c.P; var e = c.E(cfg); var card = Card(c, w, h);
            string stat = Json.S(cfg, "stat_type") ?? "change";
            string period = Json.S(Json.O(Json.O(cfg, "period"), "calendar"), "period") ?? "day";
            var v = c.S.StatisticValue(Json.S(cfg, "entity"), stat, period);
            string unit = Json.S(cfg, "unit") ?? e?.A("unit_of_measurement") ?? "";
            Ui.Add(card, Ui.Text(P.Pad + 4, P.Pad, w - 2 * P.Pad - 40, 22, Format.Name(e, Json.S(cfg, "name")), 15, P.Secondary));
            Ui.Add(card, Ui.Icon(w - P.Pad - 26, P.Pad - 1, 24, Format.Icon(e, Json.S(cfg, "icon"), c.S.Icons), P.Accent));
            string val = v == null ? "—" : Format.Number(v.Value, c.S.Registry.TryGetValue(e?.Id ?? "", out var rg) && Json.Get(rg, "dp") is double dp ? (int)dp : (int?)null);
            Ui.Add(card, Ui.Text(P.Pad + 4, P.Pad + 36, Ui.Width(val, 30) + 8, 40, val, 30, P.Text));
            if (unit != "") Ui.Add(card, Ui.Text(P.Pad + 8 + Ui.Width(val, 30), P.Pad + 48, w, 24, unit, 16, P.Secondary));
            return card;
        }

        // ---------- statistics-graph (long-term statistics; line or bars) ----------
        private static Dictionary<string, object> StatisticsGraph(Ctx c, Dictionary<string, object> cfg, double w, double h)
        {
            var P = c.P; var card = Card(c, w, h);
            double y = P.Pad;
            if (Json.S(cfg, "title") is string t) { Ui.Add(card, Ui.Text(P.Pad + 4, y, w - 2 * P.Pad, 32, t, 22, P.Text)); y += 40; }
            string period = Json.S(cfg, "period") ?? "hour";
            double days = Json.N(cfg, "days_to_show", 30);
            var types = (Json.A(cfg, "stat_types") ?? new List<object> { "mean" }).OfType<string>().ToList();
            string stat = types.Contains("change") ? "change" : types.Contains("mean") ? "mean" : types.FirstOrDefault() ?? "mean";
            bool bars = Json.S(cfg, "chart_type") == "bar" || stat == "change";
            var ents = (Json.A(cfg, "entities") ?? new List<object>()).Select(x => x is string xs ? xs : Json.S(x, "entity")).Where(x => x != null).ToList();
            var series = ents.Select(id => c.S.StatisticSeries(id, period, stat, days)).ToList();
            var all = series.SelectMany(s => s).ToList();
            double ax = P.Pad + 34, gw = w - ax - P.Pad - 10, gy = y + 14, gh = h - gy - P.Pad - 52;
            if (all.Count > 0 && gh > 20)
            {
                double lo = Math.Floor(all.Min(p => p.v)), hi = Math.Ceiling(all.Max(p => p.v));
                if (hi - lo < 1) hi = lo + 1;
                double step = Math.Max(1, Math.Ceiling((hi - lo) / 6));
                lo = Math.Floor(lo / step) * step; hi = Math.Ceiling(hi / step) * step;
                for (double v = lo; v <= hi + 1e-9; v += step)
                {
                    double ly = gy + (1 - (v - lo) / (hi - lo)) * gh;
                    Ui.Add(card, Ui.Box(ax, ly, gw, 1, P.Divider));
                    Ui.Add(card, Ui.Text(P.Pad - 4, ly - 9, 34, 18, Format.Number(v, 0), 11, P.Secondary, null, "right"));
                }
                if (ents.Count > 0 && c.S.State(ents[0])?.A("unit_of_measurement") is string unit) Ui.Add(card, Ui.Text(ax - 4, gy - 22, 40, 16, unit, 10, P.Secondary));
                double t0 = all.Min(p => p.t), t1 = all.Max(p => p.t);
                int ticks = Math.Max(2, Math.Min(8, (int)(gw / 90)));
                for (int i = 0; i < ticks; i++)
                {
                    double tt = t0 + (t1 - t0) * i / (ticks - 1), lx = ax + gw * i / (ticks - 1);
                    Ui.Add(card, Ui.Box(lx, gy, 1, gh, P.Divider));
                    Ui.Add(card, Ui.Text(lx - 40, gy + gh + 4, 80, 18, c.S.ToLocal(tt).ToString(period == "hour" ? "MMM d h tt" : "MMM d", CultureInfo.InvariantCulture), 11, P.Secondary, i == 0 ? "medium" : null, "center"));
                }
                for (int i = 0; i < series.Count; i++)
                {
                    if (series[i].Count < 1) continue;
                    string col = Lines[i % Lines.Length];
                    string svg = bars ? Svg.Bars(series[i].Select(p => p.v - lo).ToList(), (int)gw, (int)gh, col)
                                      : Svg.Graph(series[i].Select(p => p.t).ToList(), series[i].Select(p => p.v).ToList(), (int)gw, (int)gh, col, 2.5, 0, lo, hi, t0, t1, true);
                    Ui.Add(card, Ui.SvgImg(ax, gy, gw, gh, svg));
                }
            }
            // legend, centred
            var names = ents.Select(id => Format.Name(c.S.State(id), null) + " (" + stat + ")").ToList();
            double total = names.Sum(n => 26 + Ui.Width(n, 12)), lx2 = (w - total) / 2;
            for (int i = 0; i < names.Count; i++)
            {
                Ui.Add(card, Ui.Box(lx2, h - P.Pad - 18, 12, 12, Lines[i % Lines.Length], 6));
                Ui.Add(card, Ui.Text(lx2 + 16, h - P.Pad - 24, Ui.Width(names[i], 12) + 8, 22, names[i], 12, P.Text));
                lx2 += 26 + Ui.Width(names[i], 12);
            }
            return card;
        }

        // ---------- custom:mini-graph-card ----------
        // mini-graph-card's own default series colours (the first is HA's default accent colour)
        private static readonly string[] MiniColors = { "#ff9800", "#3498db", "#e74c3c", "#9b59b6", "#f1c40f", "#2ecc71", "#1abc9c", "#34495e", "#e67e22", "#7f8c8d" };

        // A series with one point (the state never changed in the window) is a flat line from that point to now.
        private static (List<double> t, List<double> v) Extend((List<double> t, List<double> v) s, Ent e, double now)
        {
            if (s.t.Count == 1) return (new List<double> { s.t[0], now }, new List<double> { s.v[0], s.v[0] });
            if (s.t.Count == 0 && e?.Num != null) return (new List<double> { now - 1, now }, new List<double> { e.Num.Value, e.Num.Value });
            return s;
        }

        private static List<Dictionary<string, object>> MiniEntities(Dictionary<string, object> cfg)
        {
            var l = (Json.A(cfg, "entities") ?? new List<object>()).Select(x => x is string xs ? new Dictionary<string, object> { ["entity"] = xs } : x as Dictionary<string, object>).Where(x => x != null && Json.S(x, "entity") != null).ToList();
            if (l.Count == 0 && Json.S(cfg, "entity") != null) l.Add(new Dictionary<string, object> { ["entity"] = Json.S(cfg, "entity") });
            return l;
        }

        private static Dictionary<string, object> MiniGraph(Ctx c, Dictionary<string, object> cfg, double w, double h)
        {
            var P = c.P; var card = Card(c, w, h);
            var ents = MiniEntities(cfg);
            if (ents.Count == 0) return card;
            var show = Json.O(cfg, "show") ?? new Dictionary<string, object>();
            var main = c.S.State(Json.S(ents[0], "entity"));
            var colors = (Json.A(cfg, "line_color") ?? new List<object>()).OfType<string>().ToList();
            if (Json.S(cfg, "line_color") is string lc1) colors = new List<string> { lc1 };
            string Col(int i) => Theme.Color(Json.S(ents[i], "color")) ?? (i < colors.Count ? Theme.Color(colors[i]) : null) ?? MiniColors[i % MiniColors.Length];
            double y = P.Pad;
            if (Json.B(show, "name", true)) Ui.Add(card, Ui.Text(P.Pad + 4, y, w - 2 * P.Pad - 40, 22, Json.S(cfg, "name") ?? Format.Name(main, Json.S(ents[0], "name")), 15, P.Secondary));
            if (Json.B(show, "icon", true)) Ui.Add(card, Ui.Icon(w - P.Pad - 26, y - 1, 24, Json.S(cfg, "icon") ?? Format.Icon(main, null, c.S.Icons), Col(0)));
            y += 24;
            if (Json.B(show, "state", true) && main != null)
            {
                string val = main.Num != null ? Format.Number(main.Num.Value, Json.Get(cfg, "decimals") is double dd ? (int)dd : (int?)null) : c.S.StateText(main);
                string unit = Json.S(cfg, "unit") ?? (main.Num != null ? main.A("unit_of_measurement") : null);
                Ui.Add(card, Ui.Text(P.Pad + 4, y, Ui.Width(val, 30) + 8, 42, val, 30, P.Text));
                if (unit != null) Ui.Add(card, Ui.Text(P.Pad + 8 + Ui.Width(val, 30), y + 12, 80, 24, unit, 15, P.Secondary));
                y += 46;
            }
            double hours = Json.N(cfg, "hours_to_show", 24), now = (c.S.NowUtc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            var series = ents.Select(x => Extend(c.S.History(Json.S(x, "entity"), hours), c.S.State(Json.S(x, "entity")), now)).ToList();
            if (Json.B(show, "extrema", false) && series[0].t.Count > 1)
            {
                var (t0, v0) = series[0];
                int iMin = v0.IndexOf(v0.Min()), iMax = v0.IndexOf(v0.Max());
                string u = main?.A("unit_of_measurement") ?? "";
                void Ext(double x, string label, int idx, string align)
                {
                    Ui.Add(card, Ui.Text(x, y, 160, 20, label, 13, P.Text, "medium", align));
                    Ui.Add(card, Ui.Text(x, y + 20, 160, 20, Format.Number(v0[idx], null) + " " + u, 13, P.Secondary, null, align));
                    Ui.Add(card, Ui.Text(x, y + 40, 160, 20, c.S.ToLocal(t0[idx]).ToString("h:mm tt", CultureInfo.InvariantCulture), 13, P.Secondary, null, align));
                }
                Ext(P.Pad + 4, "Min", iMin, "left");
                Ext(w - P.Pad - 164, "Max", iMax, "right");
                y += 66;
            }
            bool legend = Json.B(show, "legend", true) && ents.Count > 1;
            if (legend)
            {
                var names = ents.Select(x => Format.Name(c.S.State(Json.S(x, "entity")), Json.S(x, "name"))).ToList();
                double total = names.Sum(n => 26 + Ui.Width(n, 13)), lx = (w - total) / 2;
                for (int i = 0; i < names.Count; i++)
                {
                    Ui.Add(card, Ui.Box(lx, y + 6, 10, 10, Col(i), 5));
                    Ui.Add(card, Ui.Text(lx + 14, y, Ui.Width(names[i], 13) + 8, 22, names[i], 13, P.Secondary));
                    lx += 26 + Ui.Width(names[i], 13);
                }
                y += 26;
            }
            double gh = h - y;
            bool fill = Json.Get(show, "fill") as bool? ?? true;
            int buckets = Math.Max(4, (int)Math.Round(hours * Json.N(cfg, "points_per_hour", 0.5)));
            var ok = series.Where(s => s.t.Count >= 2).ToList();
            if (ok.Count > 0 && gh > 10)
            {
                double lo = Json.Get(cfg, "lower_bound") is double lb ? lb : ok.Min(s => s.v.Min()), hi = Json.Get(cfg, "upper_bound") is double ub ? ub : ok.Max(s => s.v.Max());
                for (int i = 0; i < series.Count; i++)
                {
                    if (series[i].t.Count < 2) continue;
                    var (gt, gv) = Smooth(series[i].t, series[i].v, now - hours * 3600, now, buckets, 2);
                    Ui.Add(card, Ui.SvgImg(0, y, w, gh, Svg.Graph(gt, gv, (int)w, (int)gh, Col(i), Json.N(cfg, "line_width", 5) * 0.8, fill && i == 0 ? 0.25 : 0, lo, hi, now - hours * 3600, now, Json.Get(cfg, "smoothing") as bool? ?? true)));
                }
            }
            return card;
        }

        // ---------- custom:clock-weather-card ----------
        private static Dictionary<string, object> ClockWeather(Ctx c, Dictionary<string, object> cfg, double w, double h)
        {
            var P = c.P; var e = c.E(cfg); var card = Card(c, w, h);
            double y = P.Pad;
            var now = c.S.NowLocal;
            string unit = e?.A("temperature_unit") ?? "°";
            if (!Json.B(cfg, "hide_today_section", false))
            {
                if (e != null) Ui.Add(card, Ui.Icon(P.Pad, y + 4, 96, Format.WeatherIcon(e.State), "#ffd54f"));
                double tx = P.Pad + 110;
                string tf = Json.S(cfg, "time_format");
                string time = tf == "24" ? now.ToString("HH:mm", CultureInfo.InvariantCulture) : now.ToString("h:mm", CultureInfo.InvariantCulture);
                Ui.Add(card, Ui.Text(tx, y, w - tx - P.Pad, 56, time, 46, P.Text, "light", "right"));
                Ui.Add(card, Ui.Text(tx, y + 58, w - tx - P.Pad, 24, now.ToString("dddd, MMMM d", CultureInfo.InvariantCulture), 15, P.Secondary, null, "right"));
                if (e != null)
                {
                    string temp = Json.Get(e.Attr, "temperature") is double t ? Format.Number(t, 0) + unit : "";
                    Ui.Add(card, Ui.Text(tx, y + 84, w - tx - P.Pad, 26, Format.State(e, null, c.S.Tz, c.S.NowUtc).Replace("Partlycloudy", "Partly cloudy").Replace("Clear-night", "Clear") + (temp != "" ? " · " + temp : ""), 15, P.Text, null, "right"));
                }
                y += 120;
            }
            if (!Json.B(cfg, "hide_forecast_section", false) && e != null)
            {
                var fc = c.S.Forecasts(e.Id, "daily") ?? new List<Forecast>();
                int rows = Math.Min(fc.Count, (int)Json.N(cfg, "forecast_rows", 5));
                var shown = fc.Take(rows).ToList();
                if (shown.Count > 0)
                {
                    double lo = shown.Where(f => f.Lo != null).Select(f => f.Lo.Value).DefaultIfEmpty(shown.Where(f => f.Hi != null).Select(f => f.Hi.Value).DefaultIfEmpty(0).Min()).Min();
                    double hi = shown.Where(f => f.Hi != null).Select(f => f.Hi.Value).DefaultIfEmpty(lo + 1).Max();
                    double barX = P.Pad + 150, barW = w - barX - P.Pad - 60;
                    foreach (var f in shown)
                    {
                        string dayName = f.Time.Date == now.Date ? "Today" : f.Time.ToString("ddd", CultureInfo.InvariantCulture);
                        Ui.Add(card, Ui.Text(P.Pad + 4, y, 64, 34, dayName, 14, P.Text));
                        Ui.Add(card, Ui.Icon(P.Pad + 70, y + 5, 24, Format.WeatherIcon(f.Condition), "#ffd54f"));
                        double a = f.Lo ?? f.Hi ?? lo, b = f.Hi ?? a;
                        Ui.Add(card, Ui.Text(barX - 50, y, 44, 34, Format.Number(a, 0) + "°", 13, P.Secondary, null, "right"));
                        Ui.Add(card, Ui.Box(barX, y + 15, barW, 5, "#ffffff1f", 3));
                        double x0 = barX + (a - lo) / Math.Max(1e-9, hi - lo) * barW, x1 = barX + (b - lo) / Math.Max(1e-9, hi - lo) * barW;
                        Ui.Add(card, Ui.Box(x0, y + 15, Math.Max(5, x1 - x0), 5, Theme.Named["orange"], 3));
                        Ui.Add(card, Ui.Text(barX + barW + 6, y, 50, 34, Format.Number(b, 0) + "°", 13, P.Text));
                        y += 34;
                    }
                }
            }
            return card;
        }
    }
}
