using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Xunit;
using static TizenNotifier.Fixtures;

namespace TizenNotifier
{
    public class ConditionalTests
    {
        private static readonly Dictionary<string, object> Ambient = P("scale", 1, "theme", P("name", "ambient"));
        private static readonly Dictionary<string, object> Reserve = P("scale", 1, "theme", P("name", "ambient"), "conditionals", "reserve");

        private static Dictionary<string, Dictionary<string, object>> Cards(Dictionary<string, object> tree) =>
            (Json.A(Json.O(tree, "root"), "children") ?? new List<object>()).Cast<Dictionary<string, object>>().Where(n => Json.S(n, "name") != null).ToDictionary(n => Json.S(n, "name"));

        // a render the way the session publishes it (so the next render knows what was shown)
        private static Dictionary<string, object> Shown(DashSession s)
        {
            var t = Render(s);
            s.MarkShown(t);
            return t;
        }

        [Fact]
        public void ReserveKeepsPositions()
        {
            var (s, _) = Session("fixture-conditional", Reserve);
            var on = Cards(Shown(s));
            SetState(s, "input_boolean.show_cam", "off");
            var off = Cards(Shown(s));
            Assert.Equal(on.Keys.OrderBy(k => k), off.Keys.OrderBy(k => k));
            foreach (var k in on.Keys)
            {
                Assert.Equal(Json.N(on[k], "x", -1), Json.N(off[k], "x", -2));
                Assert.Equal(Json.N(on[k], "y", -1), Json.N(off[k], "y", -2));
            }
            var slot = off["d.s0.c1"];
            Assert.False(Json.Has(slot, "children"));
            Assert.False(Json.Has(slot, "bg"));
            Assert.Equal(Json.N(on["d.s0.c1"], "h", -1), Json.N(slot, "h", -2));
        }

        [Fact]
        public void ReserveContactsNothing()
        {
            var (s, host) = Session("fixture-conditional", Reserve);
            SetState(s, "input_boolean.show_cam", "off");
            Shown(s);
            Assert.Equal(0, host.VideoUrlCalls);
            SetState(s, "input_boolean.show_cam", "on");
            Shown(s);
            Assert.True(host.VideoUrlCalls > 0);   // the test can see a camera asking for video
        }

        [Fact]
        public void ReserveTrailingGivesBack()
        {
            var cfg = (Dictionary<string, object>)Json.Parse(Json.Write(Json.O(Raw("fixture-conditional"), "config")));
            var cards = Json.A((Dictionary<string, object>)Json.A((Dictionary<string, object>)Json.A(cfg, "views")[0], "sections")[0], "cards");
            cards.Add(P("type", "conditional", "conditions", new List<object> { P("condition", "state", "entity", "input_boolean.guest", "state", "on") },
                "card", P("type", "tile", "entity", "switch.fan")));
            var (s, _) = Session("fixture-conditional", Reserve, cfg);
            var c = Cards(Shown(s));
            Assert.True(c.ContainsKey("d.s0.c2"));    // hidden in the middle: reserved
            Assert.False(c.ContainsKey("d.s0.c6"));   // hidden at the end: nothing
        }

        [Fact]
        public void ReserveAllHidden()
        {
            var hide = new List<object> { P("condition", "state", "entity", "input_boolean.guest", "state", "on") };
            var cfg = P("views", new List<object> { P("title", "H", "type", "sections", "sections", new List<object> { P("type", "grid", "cards", new List<object> {
                P("type", "tile", "entity", "light.hall", "visibility", hide), P("type", "tile", "entity", "switch.fan", "visibility", hide) }) }) });
            var (s, _) = Session("fixture-conditional", Reserve, cfg);
            var tree = Shown(s);
            Assert.Empty(Cards(tree));
        }

        [Fact]
        public void InstantCollapses()
        {
            var (s, _) = Session("fixture-conditional", P("theme", P("name", "default")));
            SetState(s, "input_boolean.show_cam", "off");
            var names = Nodes(Shown(s)).Select(n => Json.S(n, "name")).ToList();
            Assert.DoesNotContain("d.s0.c1", names);
            Assert.DoesNotContain("d.s0.c2", names);
            Assert.Contains("d.s0.c3", names);
        }

        [Fact]
        public void AmbientSlidesByDefault()
        {
            // conditional cards last: a hidden one ahead of a shown one leaves no gap, the fixed card stays put
            var cfg = P("views", new List<object> { P("title", "S", "type", "sections", "sections", new List<object> { P("type", "grid", "cards", new List<object> {
                P("type", "tile", "entity", "light.hall"),
                P("type", "tile", "entity", "switch.fan", "visibility", new List<object> { P("condition", "state", "entity", "input_boolean.guest", "state", "on") }),
                P("type", "tile", "entity", "switch.fan", "visibility", new List<object> { P("condition", "state", "entity", "input_boolean.guest", "state", "off") }) }) }) });
            var (s, _) = Session("fixture-conditional", Ambient, cfg);
            SetState(s, "input_boolean.guest", "off");
            var c = Cards(Shown(s));
            Assert.False(c.ContainsKey("d.s0.c1"));
            double y0 = Json.N(c["d.s0.c0"], "y", -1), h2 = Json.N(c["d.s0.c2"], "h", -1), y2 = Json.N(c["d.s0.c2"], "y", -1);
            Assert.Equal(y0 - 8 - h2, y2);   // directly above the fixed card (bottom-left origin, gap 8)
            var fixedY = y0;
            SetState(s, "input_boolean.guest", "on");
            c = Cards(Shown(s));
            Assert.True(c.ContainsKey("d.s0.c1"));
            Assert.False(c.ContainsKey("d.s0.c2"));
            Assert.Equal(fixedY, Json.N(c["d.s0.c0"], "y", -2));
        }

        [Fact]
        public void CardOverride()
        {
            var cfg = (Dictionary<string, object>)Json.Parse(Json.Write(Json.O(Raw("fixture-conditional"), "config")));
            var cam = (Dictionary<string, object>)Json.A((Dictionary<string, object>)Json.A((Dictionary<string, object>)Json.A(cfg, "views")[0], "sections")[0], "cards")[1];
            cam["tv_overlay"] = P("conditional", "instant");
            var (s, _) = Session("fixture-conditional", Reserve, cfg);
            SetState(s, "input_boolean.show_cam", "off");
            var c = Cards(Shown(s));
            Assert.False(c.ContainsKey("d.s0.c1"));   // instant: collapses
            Assert.True(c.ContainsKey("d.s0.c2"));    // the guest card still reserves
        }

        [Fact]
        public void FadeInOnAppear()
        {
            var (s, _) = Session("fixture-conditional", Ambient);
            SetState(s, "input_boolean.show_cam", "off");
            Shown(s);
            SetState(s, "input_boolean.show_cam", "on");
            var c = Cards(Shown(s));
            var anim = Json.A(c["d.s0.c1"], "animate");
            Assert.NotNull(anim);
            var a = (Dictionary<string, object>)anim[0];
            Assert.Equal("opacity", Json.S(a, "prop"));
            Assert.Equal(0.0, Json.N(a, "from", -1));
            Assert.Equal(1.0, Json.N(a, "to", -1));
            Assert.Equal(400.0, Json.N(a, "ms", -1));
            Assert.False(Json.Has(c["d.s0.c0"], "animate"));
            Assert.False(Json.Has(Cards(Shown(s))["d.s0.c1"], "animate"));   // only on the render where it appears
        }

        [Fact]
        public void FadeOutBeforeHide()
        {
            var (s, host) = Session("fixture-conditional", Reserve);
            s.PublishForTest(Render(s));
            int shows = host.ShowCalls;
            SetState(s, "input_boolean.show_cam", "off");
            s.PublishForTest(Render(s));
            var fade = host.UpdatesSent.Last();
            var a = (Dictionary<string, object>)Json.A(fade["d.s0.c1"], "animate")[0];
            Assert.Equal(0.0, Json.N(a, "to", -1));
            Assert.Equal(shows, host.ShowCalls);   // the hide waits for the fade
            Assert.Equal(0, host.PatchCalls);
            Thread.Sleep(700);
            Assert.Equal(shows, host.ShowCalls);   // then only that card is swapped for its empty slot
            Assert.Equal(new[] { "d.s0.c1" }, Json.O(host.PatchesSent.Single(), "replace").Keys.ToArray());
        }

        [Fact]
        public void CrossfadeSequence()
        {
            var (s, host) = Session("fixture-conditional", P("scale", 1, "theme", P("name", "ambient"), "conditionals", "crossfade"));
            s.PublishForTest(Render(s));
            int shows = host.ShowCalls;
            SetState(s, "input_boolean.show_cam", "off");
            s.PublishForTest(Render(s));
            var out0 = (Dictionary<string, object>)Json.A(host.UpdatesSent.Last()["d"], "animate")[0];
            Assert.Equal(0.0, Json.N(out0, "to", -1));
            Assert.Equal(300.0, Json.N(out0, "ms", -1));
            Assert.Equal(shows, host.ShowCalls);
            Thread.Sleep(600);
            Assert.Equal(shows, host.ShowCalls);   // no full redraw: one patch, with the overlay fading back in
            var ops = host.PatchesSent.Single();
            var in0 = (Dictionary<string, object>)Json.A(Json.O(Json.O(ops, "nodes"), "d"), "animate")[0];
            Assert.Equal(0.0, Json.N(in0, "from", -1));
            Assert.Equal(1.0, Json.N(in0, "to", -1));
            Assert.Equal(300.0, Json.N(in0, "ms", -1));
            Assert.Contains("d.s0.c1", Json.A(ops, "remove").Cast<string>());   // crossfade collapses
        }

        private static readonly Dictionary<string, object> Slide = P("scale", 1, "theme", P("name", "ambient"), "conditionals", "slide");

        [Fact]
        public void SlideMovesWithAnimate()
        {
            var (s, host) = Session("fixture-conditional", Slide);
            var first = Render(s); s.PublishForTest(first);
            var before = Cards(first);
            int shows = host.ShowCalls;
            SetState(s, "input_boolean.show_cam", "off");
            var next = Render(s); var after = Cards(next);
            s.PublishForTest(next);
            Assert.Equal(0.0, Json.N((Dictionary<string, object>)Json.A(host.UpdatesSent.Last()["d.s0.c1"], "animate")[0], "to", -1));   // fades first
            Assert.Equal(0, host.PatchCalls);
            Thread.Sleep(700);
            Assert.Equal(shows, host.ShowCalls);
            var ops = host.PatchesSent.Single();
            Assert.Equal(new object[] { "d.s0.c1" }, Json.A(ops, "remove").ToArray());
            var nodes = Json.O(ops, "nodes");
            foreach (var k in new[] { "d.s0.c3", "d.s0.c4", "d.s0.c5" })
            {
                if (Json.N(before[k], "y", 0) == Json.N(after[k], "y", 0) && Json.N(before[k], "x", 0) == Json.N(after[k], "x", 0)) continue;
                var props = (Dictionary<string, object>)nodes[k];
                Assert.False(props.ContainsKey("x") || props.ContainsKey("y"));   // no jump: they glide
                var anims = Json.A(props, "animate").Cast<Dictionary<string, object>>().ToList();
                Assert.All(anims, a => Assert.Equal(500.0, Json.N(a, "ms", -1)));
                Assert.Contains(anims, a => Json.S(a, "prop") == "y" && Json.N(a, "to", -1) == Json.N(after[k], "y", -2) || Json.S(a, "prop") == "x" && Json.N(a, "to", -1) == Json.N(after[k], "x", -2));
            }
            Assert.Contains(nodes.Keys, k => k.StartsWith("d.s0.c"));
        }

        [Fact]
        public void SlideAppear()
        {
            var (s, host) = Session("fixture-conditional", Slide);
            SetState(s, "input_boolean.show_cam", "off");
            s.PublishForTest(Render(s));
            int shows = host.ShowCalls;
            SetState(s, "input_boolean.show_cam", "on");
            s.PublishForTest(Render(s));
            Assert.Equal(shows, host.ShowCalls);
            Assert.Equal(1, host.PatchCalls);   // one patch: the others glide, the card is added invisible and fades in after
            var add = (Dictionary<string, object>)Json.A(host.PatchesSent[0], "add").Single();
            Assert.Equal("d", Json.S(add, "parent"));
            var node = Json.O(add, "node");
            Assert.Equal("d.s0.c1", Json.S(node, "name"));
            Assert.Equal(0.0, Json.N(node, "opacity", -1));
            var a = (Dictionary<string, object>)Json.A(node, "animate")[0];
            Assert.Equal(0.0, Json.N(a, "from", -1));
            Assert.Equal(1.0, Json.N(a, "to", -1));
            Assert.Equal(500.0, Json.N(a, "delay", -1));
            Thread.Sleep(700);
            Assert.Equal(1, host.PatchCalls);   // nothing left on a timer
        }

        [Fact]
        public void SteadyRenderAfterAppearSendsNothingStructural()
        {
            var (s, host) = Session("fixture-conditional", Ambient);
            SetState(s, "input_boolean.show_cam", "off");
            s.PublishForTest(Render(s));
            SetState(s, "input_boolean.show_cam", "on");
            s.PublishForTest(Render(s));   // the card fades in (animate on its root)
            int shows = host.ShowCalls, patches = host.PatchCalls;
            s.PublishForTest(Render(s));   // nothing changed
            Assert.Equal(shows, host.ShowCalls);
            Assert.Equal(patches, host.PatchCalls);
        }

        [Fact]
        public void SteadyRenderAfterCrossfadeSendsNothingStructural()
        {
            var (s, host) = Session("fixture-conditional", P("scale", 1, "theme", P("name", "ambient"), "conditionals", "crossfade"));
            s.PublishForTest(Render(s));
            SetState(s, "input_boolean.show_cam", "off");
            s.PublishForTest(Render(s));
            Thread.Sleep(600);
            int shows = host.ShowCalls, patches = host.PatchCalls;
            s.PublishForTest(Render(s));
            Assert.Equal(shows, host.ShowCalls);
            Assert.Equal(patches, host.PatchCalls);
        }

        [Fact]
        public void TrailingReserveFlipPatches()
        {
            var cfg = (Dictionary<string, object>)Json.Parse(Json.Write(Json.O(Raw("fixture-conditional"), "config")));
            var cards = Json.A((Dictionary<string, object>)Json.A((Dictionary<string, object>)Json.A(cfg, "views")[0], "sections")[0], "cards");
            cards.Add(P("type", "conditional", "conditions", new List<object> { P("condition", "state", "entity", "input_boolean.guest", "state", "on") },
                "card", P("type", "tile", "entity", "switch.fan")));
            var (s, host) = Session("fixture-conditional", Reserve, cfg);
            s.PublishForTest(Render(s));
            int shows = host.ShowCalls;
            SetState(s, "input_boolean.guest", "on");   // the trailing card (and the reserved c2) come in
            s.PublishForTest(Render(s));
            Assert.Equal(shows, host.ShowCalls);
            var ops = host.PatchesSent.Last();
            Assert.Contains(Json.A(ops, "add").Cast<Dictionary<string, object>>(), a => Json.S(Json.O(a, "node"), "name") == "d.s0.c6");
        }

        [Fact]
        public void InstantFlipPatches()
        {
            var (s, host) = Session("fixture-conditional", P("theme", P("name", "default")));
            s.PublishForTest(Render(s));
            int shows = host.ShowCalls;
            SetState(s, "input_boolean.show_cam", "off");
            s.PublishForTest(Render(s));
            Assert.Equal(shows, host.ShowCalls);
            Assert.Contains("d.s0.c1", Json.A(host.PatchesSent.Last(), "remove").Cast<string>());
        }

        [Fact]
        public void HostFailureDoesNotThrowAndRedraws()
        {
            var (s, host) = Session("fixture-conditional", Ambient);
            s.PublishForTest(Render(s));
            int shows = host.ShowCalls;
            host.ThrowOnPatch = true;
            SetState(s, "input_boolean.guest", "on");
            s.PublishForTest(Render(s));   // must not throw
            host.ThrowOnPatch = false;
            s.PublishForTest(Render(s));
            Assert.Equal(shows + 1, host.ShowCalls);   // what is shown is unknown now: a full show
        }

        [Fact]
        public void HiddenTemplateCardKeepsItsSize()
        {
            var cfg = P("views", new List<object> { P("title", "T", "type", "sections", "sections", new List<object> { P("type", "grid", "cards", new List<object> {
                P("type", "tile", "entity", "light.hall"),
                P("type", "markdown", "content", "{{ states('sensor.power') }}", "visibility", new List<object> { P("condition", "state", "entity", "input_boolean.guest", "state", "off") }),
                P("type", "tile", "entity", "switch.fan") }) }) });
            var (s, _) = Session("fixture-conditional", Reserve, cfg);
            Render(s);
            s.SetTemplateValueForTest("{{ states('sensor.power') }}", "one\n\ntwo\n\nthree\n\nfour");
            Render(s); s.DropUnusedTemplates();
            SetState(s, "input_boolean.guest", "on");   // hide it: reserve keeps its slot
            double h1 = Json.N(Nodes(Render(s)).First(n => Json.S(n, "name") == "d.s0.c1"), "h", -1);
            s.DropUnusedTemplates();
            double h2 = Json.N(Nodes(Render(s)).First(n => Json.S(n, "name") == "d.s0.c1"), "h", -2);
            Assert.Equal(h1, h2);
        }
    }
}
