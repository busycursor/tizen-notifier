using System.Collections.Generic;
using System.Linq;
using Xunit;
using static TizenNotifier.Fixtures;

namespace TizenNotifier
{
    public class ThemeTests
    {
        private static Dictionary<string, object> Lovelace(object extra = null) =>
            P("theme", extra == null ? (object)P("name", "lovelace") : extra);

        [Fact]
        public void LovelaceUsesRoboto()
        {
            var texts = Nodes(Render("fixture-sections", Lovelace())).Where(n => Json.S(n, "type") == "text").ToList();
            Assert.NotEmpty(texts);
            Assert.All(texts, t => Assert.Equal("Roboto", Json.S(t, "font")));
        }

        [Fact]
        public void DefaultHasNoFont()
        {
            Assert.DoesNotContain(Nodes(Render("fixture-sections")), n => Json.Has(n, "font"));
        }

        [Fact]
        public void LovelaceCardsOpaque()
        {
            var nodes = Nodes(Render("fixture-sections", Lovelace())).ToList();
            Assert.Contains(nodes, n => Json.S(n, "bg") == "#1c1c1c");
            Assert.DoesNotContain(nodes, n => Json.S(n, "bg") == new Palette().CardBg);
        }

        [Fact]
        public void LovelaceBackgroundParam()
        {
            var on = Json.O(Render("fixture-sections", Lovelace(P("name", "lovelace", "background", true))), "root");
            var first = (Dictionary<string, object>)Json.A(on, "children")[0];
            Assert.Equal("#111111", Json.S(first, "bg"));
            Assert.Equal(Json.N(on, "w", 0), Json.N(first, "w", -1));
            var off = Json.O(Render("fixture-sections", Lovelace()), "root");
            Assert.DoesNotContain(Json.A(off, "children").Cast<Dictionary<string, object>>(), n => Json.S(n, "bg") == "#111111");
        }

        [Fact]
        public void LovelaceSameGeometry()
        {
            string Geo(Dictionary<string, object> t) => string.Join(";", Nodes(t).Select(n => string.Join(",", new[] { "x", "y", "w", "h" }.Select(k => Json.N(n, k, -1)))));
            Assert.Equal(Geo(Render("fixture-sections")), Geo(Render("fixture-sections", Lovelace())));
        }

        [Fact]
        public void PaletteOverrideStillApplies()
        {
            var nodes = Nodes(Render("fixture-sections", Lovelace(P("name", "lovelace", "card_bg", "#202020")))).ToList();
            Assert.Contains(nodes, n => Json.S(n, "bg") == "#202020");
        }

        private static Dictionary<string, object> Full(params object[] kv)
        {
            var t = P("name", "fullscreen");
            for (int k = 0; k + 1 < kv.Length; k += 2) t[(string)kv[k]] = kv[k + 1] is int n ? (double)n : kv[k + 1];
            return P("theme", t);
        }

        [Fact]
        public void FullscreenCoversTheScreen()
        {
            var root = Json.O(Render("fixture-sections", Full()), "root");
            var first = (Dictionary<string, object>)Json.A(root, "children")[0];
            Assert.Equal("#111111", Json.S(first, "bg"));
            Assert.Equal(0.0, Json.N(first, "x", -1)); Assert.Equal(0.0, Json.N(first, "y", -1));
            Assert.Equal(Json.N(root, "w", 0), Json.N(first, "w", -1)); Assert.Equal(Json.N(root, "h", 0), Json.N(first, "h", -1));
            var custom = Json.O(Render("fixture-sections", Full("background", "#203040")), "root");
            Assert.Equal("#203040", Json.S((Dictionary<string, object>)Json.A(custom, "children")[0], "bg"));
        }

        [Fact]
        public void FullscreenLooksLikeDefault()
        {
            var full = Nodes(Render("fixture-sections", Full())).ToList();
            Assert.Contains(full, n => Json.S(n, "bg") == new Palette().CardBg);   // default's cards
            Assert.DoesNotContain(full, n => Json.Has(n, "font"));                 // default's font
        }

        [Fact]
        public void FullscreenHasSmallerMargins()
        {
            // masonry: the first card starts at the margin from the left edge, and the top
            var def = Json.A(Json.O(Render("fixture-masonry"), "root"), "children").Cast<Dictionary<string, object>>().First(n => Json.S(n, "name") == "d.c0");
            var full = Json.A(Json.O(Render("fixture-masonry", Full()), "root"), "children").Cast<Dictionary<string, object>>().First(n => Json.S(n, "name") == "d.c0");
            Assert.Equal(12.0, Json.N(full, "y", -1));
            Assert.True(Json.N(full, "y", -1) < Json.N(def, "y", -1));
            // columns are capped and centered (as in HA), so a wide screen gains nothing sideways
            var custom = Json.A(Json.O(Render("fixture-masonry", Full("margin", 40)), "root"), "children").Cast<Dictionary<string, object>>().First(n => Json.S(n, "name") == "d.c0");
            Assert.Equal(40.0, Json.N(custom, "y", -1));
        }

        [Fact]
        public void FullscreenIsAKnownTheme()
        {
            var warn = new List<string>();
            var cfg = P("tv_overlay", P("theme", "fullscreen"), "views", new List<object> { P("title", "V", "cards", new List<object>()) });
            var e = Settings.Resolve(DashOptions.From(P("config", cfg)), cfg, (Dictionary<string, object>)Json.A(cfg, "views")[0], warn);
            Assert.Equal("fullscreen", e.ThemeName);
            Assert.Empty(warn);
            Assert.Equal("instant", e.Conditionals);
            Assert.Equal(1.0, e.Scale);
        }
    }
}
