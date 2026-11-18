// Dashboard settings: the `tv_overlay` key in the Lovelace config (dashboard top level, overridable per view) merged
// with the /dashboard request. Lowest first: theme defaults, dashboard key, view key, request keys actually present.
//   tv_overlay: { theme: default|lovelace|ambient, x, y, w, h, scale, opacity, as_user, conditionals,
//                 <theme name>: { theme parameters } }
// The request's `theme` object keeps working: theme.name picks the theme, its other keys are theme parameters.
using System;
using System.Collections.Generic;
using System.Linq;

namespace TizenNotifier
{
    internal sealed class EffectiveOptions
    {
        public string ThemeName = "default", AsUser, Conditionals = "instant";
        public double X, Y, W = 1920, H = 1080, Scale = 1.0, Opacity = 1.0;
        public Dictionary<string, object> ThemeParams = new Dictionary<string, object>();
    }

    internal static class Settings
    {
        public const string Key = "tv_overlay";
        public static readonly string[] Themes = { "default", "lovelace", "ambient", "fullscreen" };
        public static readonly string[] CondModes = { "instant", "reserve", "crossfade", "slide" };
        private static readonly string[] Numbers = { "x", "y", "w", "h", "scale", "opacity" };
        private static readonly HashSet<string> Known = new HashSet<string>(new[] { "theme", "as_user", "conditionals" }.Concat(Numbers).Concat(Themes));

        // Per-theme parameter defaults. A parameter's type (number or string) is taken from its default; strings with a
        // fixed set of values list them in Choices.
        public static readonly Dictionary<string, Dictionary<string, object>> Defaults = new Dictionary<string, Dictionary<string, object>>
        {
            ["default"] = new Dictionary<string, object>(),
            ["lovelace"] = new Dictionary<string, object> { ["background"] = false },
            // fullscreen: default's look on a solid background over the whole screen, instead of the art
            ["fullscreen"] = new Dictionary<string, object> { ["background"] = "#111111", ["margin"] = 12.0 },
            // ambient: settled on a Frame over Art Mode in the evening (2026-11-15): smallest text 28 px on the 1080 layer
            // (scale 2.33), a light frosted tile, white text with a thin outline and a soft shadow
            ["ambient"] = new Dictionary<string, object>
            {
                ["origin"] = "bottom-left", ["color"] = "#ffffff", ["tile_color"] = "#ffffff", ["tile_opacity"] = 0.18,
                ["column_width"] = 340.0, ["gap"] = 8.0, ["margin"] = 24.0, ["radius"] = 14.0, ["font"] = "Inter",
                ["min_value"] = 0.7, ["recolor"] = "alpha", ["text_opacity"] = 1.0, ["outline"] = 1.0, ["outline_color"] = "#00000099",
                ["shadow"] = 4.0, ["shadow_color"] = "#000000b3", ["shadow_x"] = 2.0, ["shadow_y"] = 2.0, ["min_weight"] = "medium",
                ["knockout_color"] = "#000000", ["fill_opacity"] = 0.45,
            },
        };
        private static readonly Dictionary<string, double> ThemeScale = new Dictionary<string, double> { ["ambient"] = 2.33 };
        private static readonly Dictionary<string, string> ThemeConditionals = new Dictionary<string, string> { ["ambient"] = "slide" };
        private static readonly Dictionary<string, string[]> Choices = new Dictionary<string, string[]>
        {
            ["origin"] = new[] { "top-left", "top-right", "bottom-left", "bottom-right" },
            ["recolor"] = new[] { "alpha", "ramp" },
            ["min_weight"] = new[] { "light", "regular", "medium", "bold" },
        };

        // Deep merge: later layers win, nested objects merge key by key. Inputs are not modified.
        public static Dictionary<string, object> Merge(params Dictionary<string, object>[] layers)
        {
            var r = new Dictionary<string, object>();
            foreach (var l in layers)
            {
                if (l == null) continue;
                foreach (var kv in l)
                    r[kv.Key] = kv.Value is Dictionary<string, object> d && r.TryGetValue(kv.Key, out var cur) && cur is Dictionary<string, object> cd
                        ? Merge(cd, d) : kv.Value is Dictionary<string, object> d2 ? Merge(d2) : kv.Value;
            }
            return r;
        }

        public static EffectiveOptions Resolve(DashOptions req, Dictionary<string, object> dashCfg, Dictionary<string, object> view, List<string> warn)
        {
            // the request as a tv_overlay layer: only keys it actually contains
            var rl = new Dictionary<string, object>();
            Dictionary<string, object> reqThemeParams = null;
            foreach (var k in req.Present)
            {
                if (k == "theme")
                {
                    var t = Json.Get(req.Raw, "theme");
                    if (t is string ts) rl["theme"] = ts;
                    else if (t is Dictionary<string, object> td)
                    {
                        if (Json.S(td, "name") is string tn) rl["theme"] = tn;
                        reqThemeParams = td.Where(kv => kv.Key != "name").ToDictionary(kv => kv.Key, kv => kv.Value);
                    }
                }
                else if (Known.Contains(k)) rl[k] = req.Raw[k];
            }
            var dl = Json.O(dashCfg, Key);
            var vl = Json.O(view, Key);
            foreach (var (layer, where) in new[] { (dl, "dashboard"), (vl, "view") })
                if (layer != null) foreach (var k in layer.Keys) if (!Known.Contains(k)) warn.Add(Key + "." + k + " (" + where + "): unknown key");
            var m = Merge(dl, vl, rl);

            var e = new EffectiveOptions();
            var theme = Json.S(m, "theme") ?? "default";
            if (!Themes.Contains(theme)) { warn.Add(Key + ".theme: unknown theme '" + theme + "', using default"); theme = "default"; }
            e.ThemeName = theme;

            double Num(string k, double def)
            {
                if (!m.TryGetValue(k, out var v) || v == null) return def;
                if (v is double d) return d;
                warn.Add(Key + "." + k + ": not a number (" + Json.Write(v) + ")");
                return def;
            }
            e.X = Num("x", 0); e.Y = Num("y", 0);
            e.W = Num("w", 1920 - e.X); e.H = Num("h", 1080 - e.Y);
            e.Scale = Math.Max(0.25, Math.Min(4, Num("scale", ThemeScale.TryGetValue(theme, out var ts2) ? ts2 : 1.0)));
            e.Opacity = Math.Max(0, Math.Min(1, Num("opacity", 1.0)));
            e.AsUser = Json.S(m, "as_user");
            e.Conditionals = Json.S(m, "conditionals") ?? (ThemeConditionals.TryGetValue(theme, out var tc) ? tc : "instant");
            if (!CondModes.Contains(e.Conditionals))
            {
                warn.Add(Key + ".conditionals: unknown mode '" + e.Conditionals + "'");
                e.Conditionals = ThemeConditionals.TryGetValue(theme, out var tc2) ? tc2 : "instant";
            }

            // theme parameters: defaults, then the merged key's section for this theme, then the request's theme object.
            // Every theme's section is checked, so a mistake shows up before that theme is chosen.
            var sections = Themes.ToDictionary(t => t, t => Clean(t, Json.O(m, t), warn));
            var p = Merge(Defaults[theme], sections[theme], Clean(theme, reqThemeParams, warn));
            e.ThemeParams = p;
            return e;
        }

        // A theme section with bad values dropped (each one warned about).
        private static Dictionary<string, object> Clean(string theme, Dictionary<string, object> sec, List<string> warn)
        {
            if (sec == null) return null;
            var defs = Defaults[theme];
            var r = new Dictionary<string, object>();
            foreach (var kv in sec)
            {
                if (defs.TryGetValue(kv.Key, out var def))
                {
                    var v = kv.Value;
                    bool ok = def is double ? v is double : def is bool ? v is bool : v is string;
                    if (ok && def is string && Choices.TryGetValue(kv.Key, out var ch) && !ch.Contains((string)v)) ok = false;
                    if (!ok) { warn.Add(Key + "." + theme + "." + kv.Key + ": bad value " + Json.Write(v) + ", using " + Json.Write(def)); continue; }
                }
                r[kv.Key] = kv.Value;   // known and valid, or a palette override passed through
            }
            return r;
        }
    }
}
