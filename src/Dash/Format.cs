// How entities look: display state, name, icon and color, approximating the HA frontend. Pure C#.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace TizenNotifier
{
    internal sealed class Ent
    {
        public string Id, State;
        public Dictionary<string, object> Attr = new Dictionary<string, object>();
        public double LastChanged, LastUpdated;   // unix seconds
        public string Domain => Id.Substring(0, Id.IndexOf('.'));
        public string A(string k) => Json.S(Attr, k);
        public double? Num => double.TryParse(State, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : (double?)null;
    }

    internal static class Theme
    {
        // HA's named colors (frontend ha-style.ts).
        public static readonly Dictionary<string, string> Named = new Dictionary<string, string>
        {
            ["primary"] = "#03a9f4", ["accent"] = "#ff9800", ["red"] = "#f44336", ["pink"] = "#e91e63", ["purple"] = "#926bc7",
            ["deep-purple"] = "#6e41ab", ["indigo"] = "#3f51b5", ["blue"] = "#2196f3", ["light-blue"] = "#03a9f4", ["cyan"] = "#00bcd4",
            ["teal"] = "#009688", ["green"] = "#4caf50", ["light-green"] = "#8bc34a", ["lime"] = "#cddc39", ["yellow"] = "#ffeb3b",
            ["amber"] = "#ffc107", ["orange"] = "#ff9800", ["deep-orange"] = "#ff6f22", ["brown"] = "#795548", ["light-grey"] = "#bdbdbd",
            ["grey"] = "#9e9e9e", ["dark-grey"] = "#606060", ["blue-grey"] = "#607d8b", ["black"] = "#000000", ["white"] = "#ffffff",
            ["disabled"] = "#bdbdbd", ["state"] = "", ["none"] = "",
        };

        // A card/badge/template color value -> #hex, or null for "use the default".
        public static string Color(string c)
        {
            if (string.IsNullOrWhiteSpace(c)) return null;
            c = c.Trim().ToLowerInvariant();
            if (Named.TryGetValue(c, out var h)) return h.Length == 0 ? null : h;
            if (c.StartsWith("#")) return c;
            if (c.StartsWith("rgb"))
            {
                var p = c.Substring(c.IndexOf('(') + 1).TrimEnd(')').Split(',').Select(x => x.Trim()).ToArray();
                if (p.Length >= 3 && int.TryParse(p[0], out var r) && int.TryParse(p[1], out var g) && int.TryParse(p[2], out var b))
                    return "#" + r.ToString("x2") + g.ToString("x2") + b.ToString("x2");
            }
            return null;
        }

        public static string WithAlpha(string hex, double a)
        {
            var (h, _) = Svg.Hex(hex);
            return h + ((int)Math.Round(Math.Max(0, Math.Min(1, a)) * 255)).ToString("x2");
        }
    }

    internal static class Format
    {
        public static string TempUnit = "°";   // HA's unit system (set from get_config)
        private static readonly HashSet<string> OnStates = new HashSet<string> { "on", "open", "opening", "closing", "unlocked", "unlocking", "locking", "jammed", "home", "playing", "cleaning", "heat", "cool", "heat_cool", "auto", "dry", "fan_only", "active", "problem", "detected", "above_horizon" };

        public static bool Unavailable(Ent e) => e == null || e.State == "unavailable" || e.State == "unknown";

        public static bool Active(Ent e)
        {
            if (Unavailable(e)) return false;
            switch (e.Domain)
            {
                case "sensor": case "input_number": case "number": case "counter": case "weather": case "sun": case "calendar": case "todo": case "input_text": case "input_select": case "select": case "input_datetime": return true;
                case "lock": return e.State != "locked";
                case "person": case "device_tracker": return e.State != "not_home";
                case "cover": return e.State != "closed";
                case "climate": return e.State != "off";
                case "media_player": return e.State != "off" && e.State != "standby" && e.State != "idle";
                case "alarm_control_panel": return e.State != "disarmed";
                case "vacuum": return e.State == "cleaning" || e.State == "returning";
            }
            return OnStates.Contains(e.State);
        }

        public static string Name(Ent e, string over) =>
            !string.IsNullOrEmpty(over) ? over : e == null ? "" : e.A("friendly_name") ?? e.Id;

        // Icon: card override, then the entity's own, then HA's defaults (frontend/get_icons), then a domain fallback.
        public static string Icon(Ent e, string over, Dictionary<string, object> icons)
        {
            if (!string.IsNullOrEmpty(over)) return over;
            if (e == null) return "mdi:help-circle-outline";
            var own = e.A("icon");
            if (!string.IsNullOrEmpty(own)) return own;
            // groups (light groups, Hue rooms/zones) carry their members in entity_id: the frontend shows a group icon
            if (e.Domain == "light" && Json.Get(e.Attr, "entity_id") is List<object> members && members.Count > 0) return "mdi:lightbulb-group";
            var dom = Json.O(icons, e.Domain);
            string dc = e.A("device_class");
            foreach (var key in new[] { dc, "_" })
            {
                if (key == null) continue;
                var r = Json.O(dom, key);
                if (r == null) continue;
                var st = Json.S(Json.O(r, "state"), e.State);
                if (st != null) return st;
                var def = Json.S(r, "default");
                if (def != null) return def;
            }
            switch (e.Domain)
            {
                case "light": return "mdi:lightbulb";
                case "switch": return "mdi:toggle-switch-variant";
                case "lock": return e.State == "locked" ? "mdi:lock" : "mdi:lock-open-variant";
                case "person": return "mdi:account";
                case "sensor": return "mdi:eye";
                case "binary_sensor": return e.State == "on" ? "mdi:checkbox-marked-circle" : "mdi:radiobox-blank";
                case "weather": return WeatherIcon(e.State);
                case "sun": return e.State == "below_horizon" ? "mdi:weather-night" : "mdi:white-balance-sunny";
                case "calendar": return "mdi:calendar";
                case "todo": return "mdi:clipboard-list";
                case "input_datetime": return "mdi:calendar-clock";
                case "climate": return "mdi:thermostat";
                case "cover": return e.State == "closed" ? "mdi:window-shutter" : "mdi:window-shutter-open";
                case "media_player": return "mdi:cast";
                case "fan": return "mdi:fan";
                case "input_boolean": return e.State == "on" ? "mdi:check-circle-outline" : "mdi:close-circle-outline";
                case "zone": return "mdi:map-marker-radius";
            }
            return "mdi:bookmark";
        }

        public static string WeatherIcon(string cond)
        {
            switch (cond)
            {
                case "clear-night": return "mdi:weather-night";
                case "cloudy": return "mdi:weather-cloudy";
                case "exceptional": return "mdi:alert-circle-outline";
                case "fog": return "mdi:weather-fog";
                case "hail": return "mdi:weather-hail";
                case "lightning": return "mdi:weather-lightning";
                case "lightning-rainy": return "mdi:weather-lightning-rainy";
                case "partlycloudy": return "mdi:weather-partly-cloudy";
                case "pouring": return "mdi:weather-pouring";
                case "rainy": return "mdi:weather-rainy";
                case "snowy": return "mdi:weather-snowy";
                case "snowy-rainy": return "mdi:weather-snowy-rainy";
                case "sunny": return "mdi:weather-sunny";
                case "windy": return "mdi:weather-windy";
                case "windy-variant": return "mdi:weather-windy-variant";
            }
            return "mdi:weather-cloudy";
        }

        // Active color by domain/state (frontend state colors), or null when inactive.
        public static string StateColor(Ent e)
        {
            if (Unavailable(e)) return null;
            switch (e.Domain)
            {
                case "lock": return e.State == "locked" ? Theme.Named["green"] : e.State == "jammed" || e.State == "unlocked" ? Theme.Named["red"] : Theme.Named["orange"];
                case "person": case "device_tracker": return e.State == "home" ? Theme.Named["green"] : Theme.Named["blue-grey"];
                case "cover": return Active(e) ? Theme.Named["purple"] : null;
                case "climate":
                    return e.State == "heat" ? Theme.Named["deep-orange"] : e.State == "cool" ? Theme.Named["blue"] : e.State == "off" ? null : Theme.Named["amber"];
                case "sensor": case "input_number": case "number": case "counter": case "weather": case "calendar": case "todo": return Theme.Named["primary"];
                case "binary_sensor": return e.State == "on" ? Theme.Named["amber"] : null;
                case "alarm_control_panel": return e.State == "disarmed" ? Theme.Named["green"] : Theme.Named["red"];
            }
            return Active(e) ? Theme.Named["amber"] : null;
        }

        private static string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1).Replace('_', ' ');

        // The state as the frontend would print it.
        public static string State(Ent e, Dictionary<string, Dictionary<string, object>> reg, TimeZoneInfo tz, DateTime nowUtc)
        {
            if (e == null) return "Not found";
            if (e.State == "unavailable") return "Unavailable";
            if (e.State == "unknown") return "Unknown";
            string dc = e.A("device_class");
            if (e.Domain == "sensor" && dc == "timestamp" && DateTime.TryParse(e.State, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var ts))
                return Relative(ts, nowUtc);
            var num = e.Num;
            if (num != null && (e.Domain == "sensor" || e.Domain == "number" || e.Domain == "input_number" || e.Domain == "counter"))
            {
                int? dp = null;
                if (reg != null && reg.TryGetValue(e.Id, out var r) && Json.Get(r, "dp") is double d) dp = (int)d;
                string unit = e.A("unit_of_measurement");
                return Number(num.Value, dp) + (string.IsNullOrEmpty(unit) ? "" : unit == "%" ? "%" : " " + unit);
            }
            switch (e.Domain)
            {
                case "binary_sensor": return Binary(dc, e.State == "on");
                case "person": case "device_tracker": return e.State == "home" ? "Home" : e.State == "not_home" ? "Away" : e.State;
                case "climate":
                    var cur = e.A("current_temperature");
                    var unit = e.A("temperature_unit") ?? TempUnit;
                    return Cap(e.State) + (cur != null ? " · " + Number(Json.N(e.Attr, "current_temperature", 0), null) + " " + unit : "");
                case "light":
                    if (e.State == "on" && Json.Get(e.Attr, "brightness") is double br) return "On · " + Math.Round(br / 255 * 100) + "%";
                    break;
                case "cover":
                    if (Json.Get(e.Attr, "current_position") is double pos && e.State == "open" && pos < 100) return "Open · " + pos + "%";
                    break;
                case "input_datetime":
                    return e.State;
            }
            return Cap(e.State);
        }

        public static string Number(double v, int? dp)
        {
            if (dp != null) return v.ToString("N" + dp.Value, CultureInfo.InvariantCulture);
            if (Math.Abs(v) >= 100 || v == Math.Floor(v)) return Math.Round(v).ToString("N0", CultureInfo.InvariantCulture);
            return v.ToString("0.##", CultureInfo.InvariantCulture);
        }

        public static string Binary(string dc, bool on)
        {
            switch (dc)
            {
                case "battery": return on ? "Low" : "Normal";
                case "battery_charging": return on ? "Charging" : "Not charging";
                case "cold": return on ? "Cold" : "Normal";
                case "connectivity": return on ? "Connected" : "Disconnected";
                case "door": case "garage_door": case "opening": case "window": return on ? "Open" : "Closed";
                case "gas": case "co": case "smoke": case "safety": case "problem": case "tamper": return on ? (dc == "safety" ? "Unsafe" : dc == "problem" ? "Problem" : dc == "tamper" ? "Tampering detected" : "Detected") : (dc == "safety" ? "Safe" : dc == "problem" ? "OK" : "Clear");
                case "heat": return on ? "Hot" : "Normal";
                case "lock": return on ? "Unlocked" : "Locked";
                case "moisture": return on ? "Wet" : "Dry";
                case "motion": case "occupancy": case "presence": case "sound": case "vibration": return on ? "Detected" : "Clear";
                case "moving": return on ? "Moving" : "Not moving";
                case "plug": return on ? "Plugged in" : "Unplugged";
                case "power": return on ? "Power" : "No power";
                case "running": return on ? "Running" : "Not running";
                case "update": return on ? "Update available" : "Up-to-date";
            }
            return on ? "On" : "Off";
        }

        public static string Relative(DateTime utc, DateTime nowUtc)
        {
            var d = utc - nowUtc;
            bool future = d.TotalSeconds > 0;
            double s = Math.Abs(d.TotalSeconds);
            string v;
            if (s < 45) return "now";
            if (s < 90) v = "1 minute";
            else if (s < 45 * 60) v = Math.Round(s / 60) + " minutes";
            else if (s < 90 * 60) v = "1 hour";
            else if (s < 22 * 3600) v = Math.Round(s / 3600) + " hours";
            else if (s < 36 * 3600) v = "1 day";
            else v = Math.Round(s / 86400) + " days";
            return future ? "in " + v : v + " ago";
        }
    }
}
