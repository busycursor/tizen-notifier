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
    }
}
