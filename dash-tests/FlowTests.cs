using System.Collections.Generic;
using System.Linq;
using Xunit;
using static TizenNotifier.Fixtures;

namespace TizenNotifier
{
    public class FlowTests
    {
        // CSS px at scale 1, so the tree's numbers are the layout's numbers. Column 360, gap 16, margin 48.
        private const double W = 1920, H = 1080, M = 48, G = 16, C = 360, Avail = H - 2 * M;
        private static double Rows(int r) => r * 56 + (r - 1) * 8;

        private static Dictionary<string, object> Tile(int rows, string entity = "sensor.power") =>
            P("type", "tile", "entity", entity, "grid_options", P("rows", rows));

        private static Dictionary<string, object> View(IEnumerable<Dictionary<string, object>> cards, object badges = null)
        {
            var v = P("title", "F", "type", "sections", "sections", new List<object> { P("type", "grid", "cards", cards.Cast<object>().ToList()) });
            if (badges != null) v["badges"] = badges;
            return P("views", new List<object> { v });
        }

        private static (Dictionary<string, object> tree, DashSession s) Flow(Dictionary<string, object> config, string origin = "bottom-left", string conditionals = null)
        {
            var req = P("scale", 1, "theme", P("name", "ambient", "origin", origin, "column_width", 360, "gap", 16, "margin", 48));
            if (conditionals != null) req["conditionals"] = conditionals;
            var (s, _) = Session("fixture-sections", req, config);
            return (Render(s), s);
        }

        // the placed cards (root children), by name
        private static Dictionary<string, Dictionary<string, object>> Cards(Dictionary<string, object> tree) =>
            Json.A(Json.O(tree, "root"), "children").Cast<Dictionary<string, object>>().ToDictionary(n => Json.S(n, "name"));

        private static double N(Dictionary<string, object> n, string k) => Json.N(n, k, double.NaN);

        [Fact]
        public void OrderIsConfigOrder()
        {
            var cfg = P("views", new List<object> { P("title", "F", "type", "sections",
                "badges", new List<object> { P("type", "entity", "entity", "sensor.power") },
                "sections", new List<object> {
                    P("type", "grid", "cards", new List<object> { Tile(1), Tile(1) }),
                    P("type", "grid", "cards", new List<object> { Tile(1) }) }) });
            var (tree, _) = Flow(cfg);
            var names = Json.A(Json.O(tree, "root"), "children").Cast<Dictionary<string, object>>().Select(n => Json.S(n, "name")).ToList();
            Assert.Equal(new[] { "d.badges", "d.s0.c0", "d.s0.c1", "d.s1.c0" }, names);
        }

        [Fact]
        public void BottomLeftFillsUpThenRight()
        {
            var (tree, _) = Flow(View(new[] { Tile(4), Tile(5), Tile(5), Tile(3) }));
            var c = Cards(tree);
            Assert.Equal(M, N(c["d.s0.c0"], "x"));
            Assert.Equal(H - M - Rows(4), N(c["d.s0.c0"], "y"));
            Assert.Equal(C, N(c["d.s0.c0"], "w"));
            Assert.Equal(H - M - Rows(4) - G - Rows(5), N(c["d.s0.c1"], "y"));
            Assert.Equal(M, N(c["d.s0.c2"], "x"));
            Assert.Equal(M + C + G, N(c["d.s0.c3"], "x"));
            Assert.Equal(H - M - Rows(3), N(c["d.s0.c3"], "y"));
        }

        [Fact]
        public void TopRightFillsDownThenLeft()
        {
            var (tree, _) = Flow(View(new[] { Tile(4), Tile(5), Tile(5), Tile(3) }), "top-right");
            var c = Cards(tree);
            Assert.Equal(W - M - C, N(c["d.s0.c0"], "x"));
            Assert.Equal(M, N(c["d.s0.c0"], "y"));
            Assert.Equal(M + Rows(4) + G, N(c["d.s0.c1"], "y"));
            Assert.Equal(W - M - C - G - C, N(c["d.s0.c3"], "x"));
            Assert.Equal(M, N(c["d.s0.c3"], "y"));
        }

        [Fact]
        public void NoBackfill()
        {
            var (tree, _) = Flow(View(new[] { Tile(6), Tile(6), Tile(5), Tile(2) }));
            var c = Cards(tree);
            Assert.Equal(M + C + G, N(c["d.s0.c2"], "x"));
            Assert.Equal(M + C + G, N(c["d.s0.c3"], "x"));   // would fit in column 1, but the flow never goes back
            Assert.Equal(H - M - Rows(5) - G - Rows(2), N(c["d.s0.c3"], "y"));
        }

        [Fact]
        public void TallCardOwnColumnClipped()
        {
            var (tree, _) = Flow(View(new[] { Tile(2), Tile(17), Tile(2) }));
            var c = Cards(tree);
            Assert.Equal(M + C + G, N(c["d.s0.c1"], "x"));
            Assert.Equal(Avail, N(c["d.s0.c1"], "h"));
            Assert.Equal(M, N(c["d.s0.c1"], "y"));
            Assert.Equal(M + 2 * (C + G), N(c["d.s0.c2"], "x"));
        }

        [Fact]
        public void OverflowReportedOffscreen()
        {
            var (tree, s) = Flow(View(Enumerable.Range(0, 10).Select(_ => Tile(7))));
            var c = Cards(tree);
            Assert.Equal(8, c.Count);
            Assert.Equal(2, s.Offscreen.Count);
            Assert.All(c.Values, n => Assert.True(N(n, "x") + N(n, "w") <= W - M));
        }

        [Fact]
        public void VisibilityStillApplies()
        {
            var hidden = Tile(2); hidden["visibility"] = new List<object> { P("condition", "state", "entity", "input_boolean.guest", "state", "on") };
            var (tree, _) = Flow(View(new[] { Tile(2), hidden, Tile(2) }), conditionals: "instant");
            var c = Cards(tree);
            Assert.False(c.ContainsKey("d.s0.c1"));
            Assert.Equal(H - M - Rows(2) - G - Rows(2), N(c["d.s0.c2"], "y"));
        }

        [Fact]
        public void MasonryViewCardsFlowToo()
        {
            var (tree, _) = Flow(P("views", new List<object> { P("title", "M", "cards", new List<object> { Tile(2), Tile(2) }) }));
            var c = Cards(tree);
            Assert.Equal(new[] { "d.c0", "d.c1" }, c.Keys.ToArray());
        }

        private static Dictionary<string, object> Heading(string text) => P("type", "heading", "heading", text);

        [Fact]
        public void HeadingStaysAboveItsSectionFromBottom()
        {
            var (tree, _) = Flow(View(new[] { Heading("Clock"), Tile(2), Tile(2) }));
            var c = Cards(tree);
            double h = N(c["d.s0.c0"], "y"), top = new[] { N(c["d.s0.c1"], "y"), N(c["d.s0.c2"], "y") }.Min();
            Assert.True(h < top);                                                   // above the section's cards
            Assert.Equal(top - G - N(c["d.s0.c0"], "h"), h);                        // right on top of them
            Assert.Equal(H - M - Rows(2), N(c["d.s0.c1"], "y"));                    // the first card still starts the corner
        }

        [Fact]
        public void HeadingFirstFromTop()
        {
            var (tree, _) = Flow(View(new[] { Heading("Clock"), Tile(2) }), "top-left");
            var c = Cards(tree);
            Assert.Equal(M, N(c["d.s0.c0"], "y"));
            Assert.True(N(c["d.s0.c1"], "y") > N(c["d.s0.c0"], "y"));
        }

        [Fact]
        public void EmptyHeadingTakesNoSpace()
        {
            var (tree, _) = Flow(View(new[] { Heading(""), Tile(2) }));
            var c = Cards(tree);
            Assert.False(c.ContainsKey("d.s0.c0"));
            Assert.Equal(H - M - Rows(2), N(c["d.s0.c1"], "y"));
        }
    }
}
