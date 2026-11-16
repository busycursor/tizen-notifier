using System.Collections.Generic;
using System.Linq;
using Xunit;
using static TizenNotifier.Fixtures;

namespace TizenNotifier
{
    public class PublishTests
    {
        [Fact]
        public void NamesStableAcrossInsert()
        {
            var (s, _) = Session("fixture-conditional", P("theme", P("name", "default")));
            var before = Nodes(Render(s)).Select(n => Json.S(n, "name")).Where(n => n.StartsWith("d.s0.c3")).ToList();
            SetState(s, "input_boolean.show_cam", "off");   // the card before c3 goes away
            var after = Nodes(Render(s)).Select(n => Json.S(n, "name")).Where(n => n.StartsWith("d.s0.c3")).ToList();
            Assert.NotEmpty(before);
            Assert.Equal(before, after);
        }

        [Fact]
        public void PatchWhenOneCardChanges()
        {
            // ambient, reserve: the guest card's slot fills in; nothing else moves
            var (s, host) = Session("fixture-conditional", P("scale", 1, "theme", P("name", "ambient")));
            s.PublishForTest(Render(s));
            int shows = host.ShowCalls;
            SetState(s, "input_boolean.guest", "on");
            s.PublishForTest(Render(s));
            Assert.Equal(shows, host.ShowCalls);
            Assert.Equal(1, host.PatchCalls);
            var replace = Json.O(host.PatchesSent[0], "replace");
            Assert.Equal(new[] { "d.s0.c2" }, replace.Keys.ToArray());
            Assert.NotNull(Json.A((Dictionary<string, object>)replace["d.s0.c2"], "children"));
        }

        [Fact]
        public void ShowWhenLayoutChanges()
        {
            var (s, host) = Session("fixture-conditional", P("theme", P("name", "ambient")));
            s.PublishForTest(Render(s));
            int shows = host.ShowCalls;
            s.Config["tv_overlay"] = P("x", 100);
            s.PublishForTest(Render(s));
            Assert.Equal(shows + 1, host.ShowCalls);
            Assert.Equal(0, host.PatchCalls);
        }

        [Fact]
        public void SameShapeStillUpdatesInPlace()
        {
            var (s, host) = Session("fixture-conditional", P("theme", P("name", "default")));
            s.PublishForTest(Render(s));
            SetState(s, "sensor.power", "999");
            s.PublishForTest(Render(s));
            Assert.Equal(1, host.UpdateCalls);
            Assert.Equal(0, host.PatchCalls);
        }
    }
}
