// Offline dashboard fixtures: {"config": <lovelace config>, "states": [{entity_id, state, attributes, last_changed}]}.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TizenNotifier
{
    internal sealed class TestHost : IDashHost
    {
        public readonly List<string> Logs = new List<string>();
        public int VideoUrlCalls, ShowCalls, UpdateCalls, PatchCalls;
        public List<Dictionary<string, object>> PatchesSent = new List<Dictionary<string, object>>();
        public bool ThrowOnPatch;
        public int HideCalls;
        public void Hide(DashSession s) { HideCalls++; }
        public void Patch(DashSession s, Dictionary<string, object> ops) { if (ThrowOnPatch) throw new System.TimeoutException("main loop busy"); PatchCalls++; PatchesSent.Add(ops); }
        public Dictionary<string, object> LastShow;
        public List<Dictionary<string, Dictionary<string, object>>> UpdatesSent = new List<Dictionary<string, Dictionary<string, object>>>();
        public void Log(string msg) { lock (Logs) Logs.Add(msg); }
        public void Show(DashSession s, Dictionary<string, object> tree) { ShowCalls++; LastShow = tree; }
        public void Update(DashSession s, Dictionary<string, Dictionary<string, object>> changes) { UpdateCalls++; UpdatesSent.Add(changes); }
        public string VideoUrl(string src, int widthPx) { VideoUrlCalls++; return null; }
    }

    internal static class Fixtures
    {
        public static string Dir(string sub) => Path.Combine(AppContext.BaseDirectory, "..", "..", "..", sub);

        static Fixtures()
        {
            Mdi.File = Path.Combine(AppContext.BaseDirectory, "mdi.tsv.gz");
            System.Threading.Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        }

        public static Dictionary<string, object> Raw(string name) => (Dictionary<string, object>)Json.Parse(File.ReadAllText(Path.Combine(Dir("fixtures"), name + ".json")));

        public static Ent ToEnt(object o)
        {
            var e = new Ent { Id = Json.S(o, "entity_id"), State = Json.S(o, "state") };
            e.Attr = Json.O(o, "attributes") ?? new Dictionary<string, object>();
            e.LastChanged = e.LastUpdated = Json.N(o, "last_changed", 1700000000);
            return e;
        }

        // A session over the fixture; `request` adds /dashboard request keys (theme, scale, ...).
        public static (DashSession s, TestHost host) Session(string name, Dictionary<string, object> request = null, Dictionary<string, object> config = null)
        {
            var raw = Raw(name);
            var req = new Dictionary<string, object>(request ?? new Dictionary<string, object>()) { ["config"] = config ?? Json.O(raw, "config") };
            var host = new TestHost();
            var s = DashSession.Offline(DashOptions.From(req), host, (Json.A(raw, "states") ?? new List<object>()).Select(ToEnt));
            return (s, host);
        }

        public static Dictionary<string, object> Render(string name, Dictionary<string, object> request = null) => Render(Session(name, request).s);

        public static Dictionary<string, object> Render(DashSession s)
        {
            s.Unsupported.Clear(); s.Offscreen.Clear(); s.Warnings.Clear();
            return Layout.Render(s, s.CurrentView());
        }

        public static void SetState(DashSession s, string id, string state) => s.SetStateForTest(id, state);

        public static IEnumerable<Dictionary<string, object>> Nodes(Dictionary<string, object> tree)
        {
            var root = Json.O(tree, "root") ?? tree;
            var stack = new Stack<Dictionary<string, object>>(); stack.Push(root);
            while (stack.Count > 0)
            {
                var n = stack.Pop(); yield return n;
                var kids = Json.A(n, "children");
                if (kids != null) for (int i = kids.Count - 1; i >= 0; i--) if (kids[i] is Dictionary<string, object> k) stack.Push(k);
            }
        }

        public static Dictionary<string, object> P(params object[] kv)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1] is int n ? (double)n : kv[i + 1];
            return d;
        }
    }
}
