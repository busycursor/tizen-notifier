using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Xunit;
using static TizenNotifier.Fixtures;

namespace TizenNotifier
{
    public class VisibilityTests
    {
        private static readonly Dictionary<string, object> Ambient = P("scale", 1, "theme", P("name", "ambient"));

        private static int Draws(TestHost h) => h.ShowCalls + h.UpdateCalls + h.PatchCalls;

        [Fact]
        public void HiddenPublishesNothing()
        {
            var (s, host) = Session("fixture-conditional", Ambient);
            s.PublishForTest(Render(s));
            s.SetVisible(false);
            Assert.Equal(1, host.HideCalls);
            int draws = Draws(host);
            SetState(s, "sensor.power", "999");
            s.PublishForTest(Render(s));
            SetState(s, "input_boolean.guest", "on");
            s.PublishForTest(Render(s));
            Assert.Equal(draws, Draws(host));
            s.SetVisible(false);   // already hidden: nothing more
            Assert.Equal(1, host.HideCalls);
        }

        [Fact]
        public void ShowAgainDrawsLatest()
        {
            var (s, host) = Session("fixture-conditional", Ambient);
            s.PublishForTest(Render(s));
            s.SetVisible(false);
            SetState(s, "sensor.power", "111");
            s.PublishForTest(Render(s));
            SetState(s, "sensor.power", "222");
            s.PublishForTest(Render(s));
            int shows = host.ShowCalls, others = host.UpdateCalls + host.PatchCalls;
            s.SetVisible(true);
            Assert.Equal(shows + 1, host.ShowCalls);
            Assert.Equal(others, host.UpdateCalls + host.PatchCalls);
            Assert.Contains(Nodes(host.LastShow), n => (Json.S(n, "text") ?? "").Contains("222"));
        }

        [Fact]
        public void PendingFadeWhileHiddenDrawsNothing()
        {
            var (s, host) = Session("fixture-conditional", Ambient);
            s.PublishForTest(Render(s));
            SetState(s, "input_boolean.show_cam", "off");   // reserve: fade out, then the slot after 400 ms
            s.PublishForTest(Render(s));
            int shows = host.ShowCalls, patches = host.PatchCalls;
            s.SetVisible(false);
            Thread.Sleep(700);
            Assert.Equal(shows, host.ShowCalls);
            Assert.Equal(patches, host.PatchCalls);
        }

        [Fact]
        public void StatusReportsHidden()
        {
            var (s, _) = Session("fixture-conditional", Ambient);
            s.PublishForTest(Render(s));
            Assert.False(Json.B(s.StatusInfo(), "hidden", true));
            s.SetVisible(false);
            Assert.True(Json.B(s.StatusInfo(), "hidden", false));
        }

        [Fact]
        public void ArtModeOptionFromRequest()
        {
            Assert.True(DashOptions.From(P("dashboard", "x", "art_mode", true)).ArtMode);
            Assert.False(DashOptions.From(P("dashboard", "x", "art_mode", false)).ArtMode);
            Assert.Null(DashOptions.From(P("dashboard", "x")).ArtMode);
        }

        [Fact]
        public void EmptyConfigRendersNothing()
        {
            var (s, _) = Session("fixture-conditional", Ambient, P("views", new List<object>()));
            var tree = Render(s);
            Assert.Null(Json.A(Json.O(tree, "root"), "children"));
            var (s2, _) = Session("fixture-conditional", Ambient, P("tv_overlay", P("theme", "ambient"), "views", new List<object> { P("title", "F", "type", "sections", "sections", new List<object>()) }));
            Assert.Null(Json.A(Json.O(Render(s2), "root"), "children"));
        }

        [Fact]
        public void ConfigArrivesLater()
        {
            var (s, _) = Session("fixture-conditional", Ambient, P("views", new List<object>()));
            Render(s);
            s.Config = P("views", new List<object> { P("title", "F", "cards", new List<object> { P("type", "tile", "entity", "sensor.power") }) });
            Assert.Contains(Nodes(Render(s)), n => Json.S(n, "name") == "d.c0");
        }

        [Fact]
        public void MissingConfigIsEmpty()
        {
            Assert.Equal(0, Json.A(DashSession.EmptyIfMissing(new System.InvalidOperationException("config_not_found: No config found.")), "views").Count);
            Assert.Throws<System.InvalidOperationException>(() => DashSession.EmptyIfMissing(new System.InvalidOperationException("unauthorized: no")));
        }
    }
}
