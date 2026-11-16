// The video relay (relay/ in this repo): turns any video source into the synced MJPEG + PCM stream the mjpeg node plays
// without the TV's hardware decoder. Configured at build time (RELAY_URL, RELAY_TOKEN_FILE). When it isn't configured or
// doesn't answer, video falls back to snapshots where a card or node has one, and is left out otherwise.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;

namespace TizenNotifier
{
    internal static class Relay
    {
        private static readonly HttpClient Client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        private static readonly object L = new object();
        private static bool up;
        private static DateTime checkedAt = DateTime.MinValue;

        public static bool Configured => !string.IsNullOrEmpty(Cfg.RelayUrl);

        // Health, cached for 15 s (a failed check is retried after 5 s).
        public static bool Available()
        {
            if (!Configured) return false;
            lock (L)
            {
                if ((DateTime.UtcNow - checkedAt).TotalSeconds < (up ? 15 : 5)) return up;
                checkedAt = DateTime.UtcNow;
            }
            bool ok;
            try { var r = Client.GetAsync(Cfg.RelayUrl + "/health").Result; ok = r.IsSuccessStatusCode; }
            catch (Exception) { ok = false; }
            lock (L)
            {
                if (ok != up) Log.W("relay " + (ok ? "available" : "not available"));
                up = ok;
            }
            return ok;
        }

        // The relay URL for a source at a width (TV pixels); null when the relay can't be used now.
        public static string Url(string src, int width, double start = 0, bool audio = true, int quality = 0)
        {
            if (string.IsNullOrEmpty(src) || !Available()) return null;
            var u = Cfg.RelayUrl + "/av?src=" + Uri.EscapeDataString(src) + "&w=" + Math.Max(160, Math.Min(1920, width));
            if (start > 0) u += "&start=" + start.ToString("0.###", CultureInfo.InvariantCulture);
            if (!audio) u += "&audio=0";
            if (quality > 0) u += "&q=" + quality;
            return u;
        }

        // POST /play {src, id?, w?, h?, x?, y?, anchor?, margin?, opacity?, volume?, start?, quality?, bg?, duration?, level?, callback?, data?}
        // One message with the video through the relay: full screen by default, or a window (w/h, x/y or anchor).
        // The video box is named "vid" (change its opacity with /update); the message closes when the source ends.
        public static (int, object) Play(object req)
        {
            if (!(req is Dictionary<string, object> r) || string.IsNullOrEmpty(Json.S(r, "src"))) return (400, Http.Err("src missing"));
            if (!Configured) return (503, Http.Err("no video relay configured (build with RELAY_URL)"));
            bool hasW = Json.Has(r, "w"), hasPos = Json.Has(r, "x") || Json.Has(r, "y") || Json.Has(r, "anchor");
            int w = (int)Json.N(r, "w", 1920), h = (int)Json.N(r, "h", hasW ? Math.Round(w * 9 / 16.0) : 1080);
            if (!Available()) return (503, Http.Err("video relay not available"));
            var root = new Dictionary<string, object> { ["type"] = "box", ["name"] = "vid", ["opacity"] = Json.N(r, "opacity", 1.0) };
            if (Json.S(r, "bg") != null) root["bg"] = Json.S(r, "bg");
            root["children"] = new List<object> { new Dictionary<string, object>
            {
                ["type"] = "video", ["src"] = Json.S(r, "src"), ["start"] = Json.N(r, "start", 0), ["quality"] = Json.N(r, "quality", 0),
                ["x"] = 0.0, ["y"] = 0.0, ["w"] = (double)w, ["h"] = (double)h, ["fit"] = "contain",
                ["volume"] = Json.N(r, "volume", 1.0), ["close_on_end"] = true, ["stats"] = Json.B(r, "stats", false),
            } };
            var msg = new Dictionary<string, object>
            {
                ["id"] = Json.S(r, "id") ?? "play", ["duration"] = Json.N(r, "duration", 0), ["w"] = (double)w, ["h"] = (double)h, ["stack"] = false,
                ["enter"] = new Dictionary<string, object> { ["type"] = "fade", ["ms"] = 250.0 }, ["root"] = root,
            };
            if (hasPos)
            {
                foreach (var k in new[] { "x", "y", "anchor", "margin" }) if (Json.Has(r, k)) msg[k] = r[k];
            }
            else { msg["x"] = Math.Round((1920 - w) / 2.0); msg["y"] = Math.Round((1080 - h) / 2.0); }
            foreach (var k in new[] { "level", "callback", "data" }) if (Json.Has(r, k)) msg[k] = r[k];
            return Notes.Notify(msg);
        }

        // ---- limits: how many relay streams play at once, and how many pixels they add up to ----
        private static readonly List<(string note, double area)> Active = new List<(string, double)>();

        private sealed class Reg : IDisposable
        {
            private readonly (string, double) e; private bool done;
            public Reg((string, double) e) { this.e = e; }
            public void Dispose() { if (done) return; done = true; lock (Active) Active.Remove(e); }
        }

        // An mjpeg node playing a relay stream registers itself; disposing (when its message closes) releases it.
        public static IDisposable Register(string note, double area)
        {
            var e = (note, area);
            lock (Active) Active.Add(e);
            return new Reg(e);
        }

        public static Dictionary<string, object> Usage()
        {
            lock (Active)
                return new Dictionary<string, object>
                {
                    ["streams"] = (double)Active.Count, ["mpx"] = Math.Round(Active.Sum(a => a.area) / 1e6, 3),
                    ["max_streams"] = (double)Cfg.VideoMaxStreams, ["budget_mpx"] = Cfg.VideoBudgetMpx,
                };
        }

        // Before a message is built: decide which of its video nodes may play through the relay, and at what width.
        // Streams of other messages count; the message's own earlier streams don't (it is being replaced), unless countOwn:
        // a partial update (/update replace/add) keeps the rest of the message playing, so all registered streams count.
        // Over the count limit -> those nodes fall back. Several streams over the pixel budget -> the new ones are requested
        // smaller and scaled up into their boxes. One stream alone is never limited.
        public static void Plan(Dictionary<string, object> root, string note, bool takeover, List<object> warn, bool countOwn = false)
        {
            if (takeover || root == null) return;
            var vids = new List<Dictionary<string, object>>();
            void Walk(Dictionary<string, object> n)
            {
                if (n == null) return;
                if ((Json.S(n, "type") ?? "").ToLowerInvariant() == "video") vids.Add(n);
                foreach (var c in Json.A(n, "children") ?? new List<object>()) Walk(c as Dictionary<string, object>);
            }
            Walk(root);
            if (vids.Count == 0) return;
            int others; double otherArea;
            lock (Active) { others = Active.Count(a => countOwn || a.note != note); otherArea = Active.Where(a => countOwn || a.note != note).Sum(a => a.area); }
            int slots = Math.Max(0, Cfg.VideoMaxStreams - others);
            for (int i = slots; i < vids.Count; i++) vids[i]["__relay_deny"] = true;
            if (vids.Count > slots) warn.Add("video: " + (vids.Count - slots) + " stream(s) over this TV's limit of " + Cfg.VideoMaxStreams + "; using fallbacks");
            var kept = vids.Take(slots).ToList();
            if (kept.Count == 0) return;
            double area = kept.Sum(v => Json.N(v, "w", 640) * Json.N(v, "h", 360)), budget = Cfg.VideoBudgetMpx * 1e6;
            if (others + kept.Count > 1 && otherArea + area > budget)
            {
                double s = Math.Max(0.25, Math.Min(1, Math.Sqrt(Math.Max(budget * 0.05, budget - otherArea) / area)));
                foreach (var v in kept) v["__relay_w"] = Math.Round(Json.N(v, "w", 640) * s);
                warn.Add("video: " + kept.Count + " stream(s) requested at " + Math.Round(s * 100) + "% size to fit this TV's budget");
            }
        }

        public static bool IsRelay(string url) => Configured && url != null && url.StartsWith(Cfg.RelayUrl + "/", StringComparison.OrdinalIgnoreCase);

        // video node (not takeover) -> mjpeg node through the relay, or its fallback snapshot, or nothing.
        public static void VideoNode(Dictionary<string, object> n, List<object> warn)
        {
            string src = Json.S(n, "src");
            int w = (int)Json.N(n, "__relay_w", Json.N(n, "w", 640));
            bool denied = Json.B(n, "__relay_deny", false);
            string url = denied ? null : Url(src, w, Json.N(n, "start", 0), Json.B(n, "audio", true), (int)Json.N(n, "quality", 0));
            n.Remove("__relay_w"); n.Remove("__relay_deny");
            string why = denied ? "over this TV's stream limit" : "relay " + (Configured ? "not available" : "not configured");
            var fb = Json.Get(n, "fallback");
            foreach (var k in new[] { "src", "start", "audio", "quality", "fallback", "loop", "underlay" }) n.Remove(k);
            if (url != null)
            {
                n["type"] = "mjpeg"; n["src"] = url;
                if (Json.B(n, "mute", false)) n["volume"] = 0.0;
                n.Remove("mute");
                return;
            }
            foreach (var k in new[] { "mute", "volume", "stats", "close_on_end", "sync_trim_ms" }) n.Remove(k);
            string snap = fb as string ?? Json.S(fb, "src");
            if (snap != null)
            {
                n["type"] = "image"; n["src"] = snap; n["fit"] = Json.S(n, "fit") ?? "contain";
                n["refresh_ms"] = Json.N(fb, "refresh_ms", 1000);
                warn.Add("video: " + why + "; showing snapshots");
                return;
            }
            n.Remove("fit");
            n["type"] = "box"; n["visible"] = false;
            if (!denied) warn.Add("video: " + why + " and no fallback; left out");   // a denied one is already in Plan's warning
        }
    }
}
