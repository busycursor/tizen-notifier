using System.Collections.Generic;
using System.Linq;
using Xunit;
using static TizenNotifier.Fixtures;

namespace TizenNotifier
{
    public class SettingsTests
    {
        private static Dictionary<string, object> Cfg(Dictionary<string, object> dash, Dictionary<string, object> view = null)
        {
            var v = new Dictionary<string, object> { ["title"] = "V", ["cards"] = new List<object>() };
            if (view != null) v["tv_overlay"] = view;
            var c = new Dictionary<string, object> { ["views"] = new List<object> { v } };
            if (dash != null) c["tv_overlay"] = dash;
            return c;
        }

        private static EffectiveOptions Eff(Dictionary<string, object> request, Dictionary<string, object> dash, Dictionary<string, object> view = null, List<string> warn = null)
        {
            var cfg = Cfg(dash, view);
            var req = new Dictionary<string, object>(request ?? new Dictionary<string, object>()) { ["config"] = cfg };
            return Settings.Resolve(DashOptions.From(req), cfg, (Dictionary<string, object>)Json.A(cfg, "views")[0], warn ?? new List<string>());
        }

        [Fact]
        public void Merge_NestedKeyByKey()
        {
            var m = Settings.Merge(P("a", P("x", 1, "y", 2)), P("a", P("y", 3)));
            Assert.Equal("{\"a\":{\"x\":1,\"y\":3}}", Json.Write(m));
        }

        [Fact]
        public void ViewOverridesDashboard()
        {
            var e = Eff(null, P("theme", "ambient", "ambient", P("origin", "bottom-left")), P("ambient", P("origin", "top-right")));
            Assert.Equal("ambient", e.ThemeName);
            Assert.Equal("top-right", Json.S(e.ThemeParams, "origin"));
        }

        [Fact]
        public void RequestOnlyOverridesPresentKeys()
        {
            Assert.Equal(1.5, Eff(P("dashboard", "x"), P("scale", 1.5)).Scale);
            Assert.Equal(2.0, Eff(P("dashboard", "x", "scale", 2), P("scale", 1.5)).Scale);
        }

        [Fact]
        public void ResumeDoesNotShadowEdits()
        {
            var o = DashOptions.From(P("dashboard", "x", "theme", P("name", "lovelace")));
            var back = DashOptions.From((Dictionary<string, object>)Json.Parse(Json.Write(o.Raw)));
            Assert.Equal(o.Present.OrderBy(x => x), back.Present.OrderBy(x => x));
            Assert.DoesNotContain("scale", back.Present);
        }

        [Fact]
        public void BadValuesWarn()
        {
            var warn = new List<string>();
            var e = Eff(null, P("theme", "neon", "scale", "big", "ambient", P("origin", "middle")), null, warn);
            Assert.Equal("default", e.ThemeName);
            Assert.Equal(1.0, e.Scale);
            Assert.Equal(3, warn.Count);
            Assert.Contains(warn, w => w.Contains("theme"));
            Assert.Contains(warn, w => w.Contains("scale"));
            Assert.Contains(warn, w => w.Contains("origin"));
            var amb = Eff(null, P("theme", "ambient", "ambient", P("origin", "middle")));
            Assert.Equal("bottom-left", Json.S(amb.ThemeParams, "origin"));
        }

        [Fact]
        public void AmbientDefaultsScale()
        {
            Assert.Equal(2.33, Eff(null, P("theme", "ambient")).Scale);
            Assert.Equal("reserve", Eff(null, P("theme", "ambient")).Conditionals);
            Assert.Equal("instant", Eff(null, null).Conditionals);
        }

        [Fact]
        public void AmbientDefaultsAreTheTvBaseline()
        {
            // settled on a Frame over Art Mode, 2026-11-15
            var p = Eff(null, P("theme", "ambient")).ThemeParams;
            Assert.Equal("#ffffff", Json.S(p, "tile_color"));
            Assert.Equal(0.18, Json.N(p, "tile_opacity", 0));
            Assert.Equal(340.0, Json.N(p, "column_width", 0));
            Assert.Equal(8.0, Json.N(p, "gap", 0));
            Assert.Equal(24.0, Json.N(p, "margin", 0));
            Assert.Equal(14.0, Json.N(p, "radius", 0));
            Assert.Equal(0.7, Json.N(p, "min_value", 0));
            Assert.Equal(1.0, Json.N(p, "outline", 0));
            Assert.Equal("#00000099", Json.S(p, "outline_color"));
            Assert.Equal(4.0, Json.N(p, "shadow", 0));
            Assert.Equal("#000000b3", Json.S(p, "shadow_color"));
            Assert.Equal("medium", Json.S(p, "min_weight"));
            Assert.Equal("#000000", Json.S(p, "knockout_color"));
        }

        [Fact]
        public void RequestThemeObjectStillWorks()
        {
            var e = Eff(P("theme", P("name", "lovelace", "card_opacity", 0.5)), P("theme", "ambient"));
            Assert.Equal("lovelace", e.ThemeName);
            Assert.Equal(0.5, Json.N(e.ThemeParams, "card_opacity", 0));
        }

        [Fact]
        public void OpacityApplied()
        {
            var (s, _) = Session("fixture-conditional", null, null);
            Json.O(s.Config, "views");
            s.Config["tv_overlay"] = P("opacity", 0.6);
            var tree = Render(s);
            Assert.Equal(0.6, Json.N(Json.O(tree, "root"), "opacity", 1));
        }
    }
}
