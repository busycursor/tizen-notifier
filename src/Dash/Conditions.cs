// Lovelace visibility / conditional-card conditions. Pure C#.
//   state (state / state_not, string or list), numeric_state (above / below), user, screen (media_query), and, or.
//   Legacy conditional-card form: {entity, state | state_not}.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace TizenNotifier
{
    internal static class Conditions
    {
        // All conditions must hold (an empty or missing list is true).
        public static bool All(DashSession s, object conds, double viewWidthCss)
        {
            if (!(conds is List<object> l)) return true;
            foreach (var c in l) if (c is Dictionary<string, object> d && !One(s, d, viewWidthCss)) return false;
            return true;
        }

        private static List<string> Strings(object v)
        {
            if (v == null) return null;
            if (v is List<object> l) return l.Select(x => x is string xs ? xs : Json.Write(x).Trim('"')).ToList();
            return new List<string> { v is string s ? s : Json.Write(v).Trim('"') };
        }

        // A condition value may name another entity ("state: input_select.mode").
        private static List<string> Resolve(DashSession s, List<string> vals) =>
            vals?.Select(v => v != null && v.Contains(".") && s.State(v) != null ? s.State(v).State : v).ToList();

        public static bool One(DashSession s, Dictionary<string, object> c, double viewWidthCss)
        {
            string type = Json.S(c, "condition");
            if (type == null && Json.Has(c, "entity")) type = Json.Has(c, "above") || Json.Has(c, "below") ? "numeric_state" : "state";   // legacy
            switch (type)
            {
                case "state":
                {
                    var e = s.State(Json.S(c, "entity"));
                    string st = e?.State ?? "unavailable";
                    if (Json.Has(c, "state")) return Resolve(s, Strings(Json.Get(c, "state"))).Contains(st);
                    if (Json.Has(c, "state_not")) return !Resolve(s, Strings(Json.Get(c, "state_not"))).Contains(st);
                    return true;
                }
                case "numeric_state":
                {
                    var e = s.State(Json.S(c, "entity"));
                    var n = e?.Num;
                    if (n == null) return false;
                    double? Lim(string k)
                    {
                        var v = Json.Get(c, k);
                        if (v is double d) return d;
                        if (v is string vs)
                        {
                            if (double.TryParse(vs, NumberStyles.Float, CultureInfo.InvariantCulture, out var p)) return p;
                            return s.State(vs)?.Num;
                        }
                        return null;
                    }
                    var above = Lim("above"); var below = Lim("below");
                    if (above != null && !(n > above)) return false;
                    if (below != null && !(n < below)) return false;
                    return true;
                }
                case "user":
                {
                    var users = Strings(Json.Get(c, "users")) ?? new List<string>();
                    return users.Contains(s.EffectiveUser);
                }
                case "screen":
                    return Media(Json.S(c, "media_query"), viewWidthCss);
                case "and":
                    return All(s, Json.Get(c, "conditions"), viewWidthCss);
                case "or":
                {
                    if (!(Json.Get(c, "conditions") is List<object> l) || l.Count == 0) return true;
                    foreach (var x in l) if (x is Dictionary<string, object> d && One(s, d, viewWidthCss)) return true;
                    return false;
                }
                case "location":
                {
                    // a person's zone; the TV has no location of its own
                    return true;
                }
            }
            return true;   // unknown condition types never hide anything
        }

        private static readonly Regex MinW = new Regex(@"min-width:\s*(\d+)"), MaxW = new Regex(@"max-width:\s*(\d+)");

        // Enough of CSS media queries for HA's screen condition presets (min-width / max-width; comma = or).
        public static bool Media(string q, double width)
        {
            if (string.IsNullOrWhiteSpace(q)) return true;
            foreach (var part in q.Split(','))
            {
                bool ok = true;
                var a = MinW.Match(part); if (a.Success && width < double.Parse(a.Groups[1].Value, CultureInfo.InvariantCulture)) ok = false;
                var b = MaxW.Match(part); if (b.Success && width > double.Parse(b.Groups[1].Value, CultureInfo.InvariantCulture)) ok = false;
                if (part.Contains("not all") && !a.Success && !b.Success) ok = false;
                if (ok) return true;
            }
            return false;
        }
    }
}
