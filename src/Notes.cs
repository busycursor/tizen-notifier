// Messages ("notes"): one transparent Notification-type window each, built from the request's node tree.
//
// POST /notify
// { "id": "front-door",            optional; same id again = replace content in place (no re-animation unless "reanimate": true)
//   "duration": 10,                seconds; 0 = stays until /dismiss (or Back, if it has buttons)
//   "w": 900, "h": 200,            window size (screen is 1920x1080)
//   "anchor": "top-right",         top-left|top|top-right|left|center|right|bottom-left|bottom|bottom-right
//   "margin": 60 | [mx, my],       distance from the screen edges
//   "x": .., "y": ..,              absolute position instead of anchor
//   "stack": true, "gap": 20,      notes on the same anchor stack instead of overlapping
//   "level": "base",               window notification level: base|medium|high|top
//   "enter": {"type": "slide|fade|scale|none", "from": "top|bottom|left|right", "ms": 400},
//   "exit":  {"type": .., "from": .., "ms": 300},
//   "sound": {"src": "<url|data:> WAV (PCM 16-bit) or MP3", "volume": 0.8, "engine": "raw|player"},
//            raw (default) = PCM out through AudioPlayback, mixes with the playing app; player = Tizen media player
//            (took the media resource from the playing app and stopped its video on a Frame, 2026-11-14)
//   "callback": "http://ha/api/webhook/..", "data": {..},   button presses / Back / timeout are POSTed here
//   "timeout": 60,                 hard limit (s) for notes with buttons, max 600
//   "root": <node> }
//
// node: { "type": "box|text|image|gif|lottie|video|progress|nui:<ClassName>", "name": "for /update",
//   "x","y","w","h" (numbers; in a row/column layout also "fill"|"wrap"),
//   "bg", "gradient": {"colors": [..], "stops": [..], "angle": 0}, "radius", "opacity", "scale", "visible",
//   "shadow": {"blur", "color", "x", "y", "spread"}, "padding": n | [l, r, t, b],
//   "layout": "row|column", "gap",
//   text:  "text", "size", "pixel_size", "color", "bold", "weight", "font", "align", "valign", "multiline", "ellipsis",
//          "markup", "line_spacing", "text_shadow": {"color", "x", "y", "blur"}
//   image/gif/lottie/video: "src" (allow-listed URL or data: URI), "fit": "fill|contain|cover|width|height",
//          image "refresh_ms": re-download src (http) on that interval, flicker-free (camera view, ~2-5 fps; min 200)
//          video needs top-level "takeover": true: VideoView blanks the playing app and it does not recover
//          "loop" (lottie count, -1 = forever; video bool), "mute"
//   progress: "color", "bg", "ms" (default = duration), "reverse"
//   "animate": [{"prop": "opacity|x|y|scale|w|h|<NUI property>", "from", "to", "ms", "delay", "loop", "ease"}],
//   "action": "<id>" makes it a button; "focus": {"bg", "color", "scale", "opacity"} is its selected look,
//   "props": {"<NUI property>": value}, "call": [{"method": "..", "args": [..]}],
//   "children": [..] }
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using Timer = Tizen.NUI.Timer;

namespace TizenNotifier
{
    internal sealed class Note
    {
        public string Id, Anchor, Callback;
        public object Data, Req;
        public Surface Win;
        public View Root;
        public int X, Y, W, H, Mx, My, Gap;
        public bool Absolute, Stack, Closing;
        public readonly Dictionary<string, View> Named = new Dictionary<string, View>();
        public readonly List<object> Keep = new List<object>();     // animations, timers, players: rooted until closed
        public readonly Dictionary<object, View> KeepOwner = new Dictionary<object, View>();   // the view each one belongs to
        public void AddKeep(object k, View owner) { Keep.Add(k); if (owner != null) KeepOwner[k] = owner; }
        public readonly List<string> Files = new List<string>();
        public readonly List<(View v, string action, Dictionary<string, object> focus, Dictionary<string, object> saved)> Buttons = new List<(View, string, Dictionary<string, object>, Dictionary<string, object>)>();
        public int Sel;
        public Timer Life, Move;
        public long Seq;
        public bool Placed;
        public int CurX, CurY;
    }

    internal static class Notes
    {
        private static readonly Dictionary<string, Note> All = new Dictionary<string, Note>();
        private static readonly HttpClient Client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        private static long seq;
        private const int ScreenW = 1920, ScreenH = 1080;

        // ---------------- endpoints (HTTP thread) ----------------

        public static (int, object) Notify(object req)
        {
            if (!(req is Dictionary<string, object>)) return (400, Http.Err("body must be a JSON object"));
            if (Json.O(req, "root") == null) return (400, Http.Err("root node missing"));
            string id = Json.S(req, "id") ?? ("n" + DateTime.Now.Ticks);
            var warn = new List<object>();
            var files = new List<string>();
            Relay.Plan(Json.O(req, "root"), id, Json.B(req, "takeover", false), warn);
            try { Media.Prepare(Json.O(req, "root"), files, warn, Json.B(req, "takeover", false)); }
            catch (Exception e) { Media.Delete(files); return (400, Http.Err(e.Message)); }
            string sound = null;
            var snd = Json.O(req, "sound");
            if (snd != null)
            {
                // raw engine (default) decodes the file itself, so it needs the bytes; the player engine may stream
                try { sound = Media.Resolve(Json.S(snd, "src"), files, (Json.S(snd, "engine") ?? "raw") == "player"); }
                catch (Exception e) { warn.Add("sound: " + e.Message); }
            }
            string cb = Json.S(req, "callback");
            if (cb != null && !Cfg.Allowed(cb)) { warn.Add("callback host not in allow list; ignored"); cb = null; }

            App.OnMain(() => { Show(id, (Dictionary<string, object>)req, files, cb, warn); return true; });
            if (sound != null) Media.PlaySound(sound, snd, warn);
            return (200, new Dictionary<string, object> { ["ok"] = true, ["id"] = id, ["warnings"] = warn });
        }

        // nodes:   {"<name>": {props}}                       change properties in place
        // replace: {"<name>": <node>}                        swap a named subtree for a new one (same place, same parent)
        // add:     [{"parent": "<name>", "index": n, "node": <node>}]
        // remove:  ["<name>"]
        // Only the replaced or removed subtrees' players, streams, timers and animations stop; the rest keeps running.
        public static (int, object) Update(object req)
        {
            string id = Json.S(req, "id");
            if (id == null) return (400, Http.Err("id missing"));
            var nodes = Json.O(req, "nodes");
            var replace = Json.O(req, "replace");
            var add = (Json.A(req, "add") ?? new List<object>()).OfType<Dictionary<string, object>>().ToList();
            var remove = (Json.A(req, "remove") ?? new List<object>()).OfType<string>().ToList();
            var warn = new List<object>();
            var files = new List<string>();
            if (nodes != null) foreach (var kv in nodes)
                if (kv.Value is Dictionary<string, object> sp && (Json.S(sp, "type") == "icon" || Json.S(sp, "type") == "graph" || Json.S(sp, "type") == "svg")) Media.SvgNode(sp, warn);
            if (nodes != null) foreach (var kv in nodes) if (kv.Value is Dictionary<string, object> p && p.ContainsKey("src")) {
                try { p["src"] = Media.Resolve(Json.S(p, "src"), files, Json.S(p, "type") == "video"); } catch (Exception e) { warn.Add(kv.Key + ": " + e.Message); p.Remove("src"); } }

            bool structural = (replace != null && replace.Count > 0) || add.Count > 0 || remove.Count > 0;
            bool takeover = false;
            if (structural)
            {
                // 1. stop what the outgoing subtrees run, so their streams no longer count against the limits (the streams
                //    that stay do count: Plan with countOwn)
                var outgoing = (replace?.Keys ?? Enumerable.Empty<string>()).Concat(remove).ToList();
                var t = App.OnMain(() =>
                {
                    if (!All.TryGetValue(id, out var n) || n.Closing) return (bool?)null;
                    foreach (var name in outgoing) if (n.Named.TryGetValue(name, out var ov)) StopSubtree(n, ov);
                    return Json.B(n.Req, "takeover", false);
                });
                if (t == null) { Media.Delete(files); return (404, Http.Err("no active note " + id)); }
                takeover = t.Value;
                // 2. the incoming subtrees: stream limits and media, as for a new message
                var incoming = (replace?.Values.OfType<Dictionary<string, object>>() ?? Enumerable.Empty<Dictionary<string, object>>())
                    .Concat(add.Select(a => Json.O(a, "node")).Where(x => x != null)).ToList();
                // all incoming subtrees share what is left of the limits, next to the streams that keep playing
                Relay.Plan(new Dictionary<string, object> { ["type"] = "box", ["children"] = incoming.Cast<object>().ToList() }, id, takeover, warn, countOwn: true);
                foreach (var sub in incoming)
                    try { Media.Prepare(sub, files, warn, takeover); } catch (Exception e) { warn.Add(e.Message); }
            }

            bool found = App.OnMain(() =>
            {
                if (!All.TryGetValue(id, out var n) || n.Closing) return false;
                n.Files.AddRange(files);
                if (replace != null)
                    foreach (var kv in replace)
                    {
                        if (!n.Named.TryGetValue(kv.Key, out var old)) { warn.Add("no node named " + kv.Key); continue; }
                        if (!(old.GetParent() is View parent) || !(kv.Value is Dictionary<string, object> spec)) continue;
                        int order = old.SiblingOrder;
                        var nv = Build.Node(n, spec, (int)parent.SizeWidth, (int)parent.SizeHeight, false, warn);
                        parent.Add(nv);
                        try { nv.SiblingOrder = order; } catch (Exception e) { warn.Add("order: " + e.Message); }   // where the old one was
                        RemoveSubtree(n, old);
                    }
                foreach (var a in add)
                {
                    string pn = Json.S(a, "parent");
                    if (pn == null || !n.Named.TryGetValue(pn, out var parent)) { warn.Add("no parent named " + pn); continue; }
                    var spec = Json.O(a, "node");
                    if (spec == null) continue;
                    int index = (int)Json.N(a, "index", -1);
                    View before = index >= 0 && index < (int)parent.ChildCount ? parent.GetChildAt((uint)index) : null;
                    var nv = Build.Node(n, spec, (int)parent.SizeWidth, (int)parent.SizeHeight, false, warn);
                    parent.Add(nv);
                    if (before != null) try { nv.SiblingOrder = before.SiblingOrder; } catch (Exception e) { warn.Add("order: " + e.Message); }
                }
                foreach (var name in remove)
                    if (n.Named.TryGetValue(name, out var old)) { StopSubtree(n, old); RemoveSubtree(n, old); }
                if (nodes != null)
                    foreach (var kv in nodes)
                    {
                        if (!n.Named.TryGetValue(kv.Key, out var v)) { warn.Add("no node named " + kv.Key); continue; }
                        if (kv.Value is Dictionary<string, object> p) Build.Apply(n, v, p, warn, false);
                    }
                if (Json.Has(req, "duration")) StartLife(n, Json.N(req, "duration", 0));
                return true;
            });
            if (!found) { Media.Delete(files); return (404, Http.Err("no active note " + id)); }
            return (200, new Dictionary<string, object> { ["ok"] = true, ["id"] = id, ["warnings"] = warn });
        }

        private static bool Within(View v, View root)
        {
            for (var p = v; p != null; p = p.GetParent() as View) if (p == root) return true;
            return false;
        }

        // Stop the players, streams, timers and animations that belong to `root` or anything inside it.
        private static void StopSubtree(Note n, View root)
        {
            foreach (var k in n.Keep.ToList())
                if (n.KeepOwner.TryGetValue(k, out var owner) && Within(owner, root)) { StopKeep(k); n.Keep.Remove(k); n.KeepOwner.Remove(k); }
        }

        // Take a subtree out: its names (unless a new node took them over) and its views.
        private static void RemoveSubtree(Note n, View root)
        {
            foreach (var kv in n.Named.ToList()) if (Within(kv.Value, root)) n.Named.Remove(kv.Key);
            n.Buttons.RemoveAll(b => Within(b.v, root));
            if (n.Sel >= n.Buttons.Count) n.Sel = 0;
            try { root.GetParent()?.Remove(root); } catch (Exception) { }
            root.Dispose();
        }

        public static (int, object) Dismiss(object req)
        {
            bool all = Json.B(req, "all", false);
            string id = Json.S(req, "id");
            var want = (Json.A(req, "ids") ?? new List<object>()).OfType<string>().ToList();
            if (id != null) want.Add(id);
            int count = App.OnMain(() =>
            {
                var ids = all ? All.Keys.ToList() : want.Where(All.ContainsKey).ToList();
                foreach (var i in ids) Close(All[i], "dismissed", true);
                return ids.Count;
            });
            // a dismissed dashboard message ends its session (otherwise it would be drawn again on the next change)
            if (all) Dash.StopIds(null, false);
            else { var d = want.Where(i => i.StartsWith("dash:")).Select(i => i.Substring(5)).ToList(); if (d.Count > 0) Dash.StopIds(d, false); }
            return (200, new Dictionary<string, object> { ["ok"] = true, ["closed"] = count });
        }

        public static Dictionary<string, object> Info()
        {
            var ids = App.OnMain(() => All.Keys.Cast<object>().ToList());
            Size2D scr = null;
            try { scr = App.OnMain(() => Surface.Screen()); } catch (Exception) { }
            return new Dictionary<string, object>
            {
                ["ok"] = true, ["version"] = Cfg.Version, ["runtime"] = Environment.Version.ToString(),
                ["uptime_s"] = (int)(DateTime.Now - Cfg.Started).TotalSeconds,
                ["cpu_s"] = Math.Round(System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime.TotalSeconds, 2),   // this app's CPU time since start
                ["screen"] = scr == null ? null : new List<object> { scr.Width, scr.Height },
                ["active"] = ids, ["allow"] = Cfg.Allow, ["port"] = Cfg.Port,
                ["capabilities"] = SelfTest.Last,
            };
        }

        // ---------------- main loop ----------------

        private static void Show(string id, Dictionary<string, object> req, List<string> files, string cb, List<object> warn)
        {
            All.TryGetValue(id, out var old);
            if (old != null && old.Closing) { DisposeNote(old); old = null; }
            int w = Clamp((int)Json.N(req, "w", 900), 10, ScreenW), h = Clamp((int)Json.N(req, "h", 200), 10, ScreenH);
            Note n = old ?? new Note { Id = id, Seq = ++seq };
            n.Req = req; n.W = w; n.H = h; n.Callback = cb; n.Data = Json.Get(req, "data");
            n.Anchor = (Json.S(req, "anchor") ?? "top-right").ToLowerInvariant();
            n.Absolute = Json.Has(req, "x") || Json.Has(req, "y");
            n.X = (int)Json.N(req, "x", 0); n.Y = (int)Json.N(req, "y", 0);
            var m = Json.Get(req, "margin");
            if (m is List<object> ml && ml.Count >= 2) { n.Mx = (int)(double)ml[0]; n.My = (int)(double)ml[1]; }
            else n.Mx = n.My = (int)Json.N(req, "margin", 60);
            n.Stack = Json.B(req, "stack", true); n.Gap = (int)Json.N(req, "gap", 20);

            var enter = Json.O(req, "enter");
            bool fresh = old == null;
            if (fresh)
            {
                int x = 0, y = 0;
                if (MotionDuration(enter, true) == 0)
                    (_, x, y) = Positions(All.Values.Append(n)).Last();
                n.Win = new Surface("tiznotify-" + id, x, y, w, h, e => OnKey(n, e), warn);
                All[id] = n;
            }
            else
            {
                // replace content in place
                foreach (var k in n.Keep) StopKeep(k);
                n.Keep.Clear(); n.KeepOwner.Clear(); n.Named.Clear(); n.Buttons.Clear(); n.Sel = 0;
                if (n.Root != null) { n.Win.Remove(n.Root); n.Root.Dispose(); n.Root = null; }
                Media.Delete(n.Files); n.Files.Clear();
                try { n.Win.Resize(w, h); } catch (Exception e) { warn.Add("resize: " + e.Message); }
            }
            n.Files.AddRange(files);
            var lvl = (Json.S(req, "level") ?? "base").ToLowerInvariant();
            try
            {
                var nl = lvl == "top" ? NotificationLevel.Top : lvl == "high" ? NotificationLevel.High : lvl == "medium" ? NotificationLevel.Medium : NotificationLevel.Base;
                if (!n.Win.SetLevel(nl)) warn.Add("notification level refused");
            }
            catch (Exception e) { warn.Add("level: " + e.Message); }

            // outer holder: carries enter/exit motion so the user's root keeps its own position/opacity/scale
            var holder = new View { Size = Build.Sz(w, h), BackgroundColor = Color.Transparent, PivotPoint = PivotPoint.Center, PositionUsesPivotPoint = false };
            var root = Build.Node(n, Json.O(req, "root"), w, h, false, warn);
            holder.Add(root);
            n.Root = holder;
            n.Win.Add(holder);
            Restack();
            n.Win.Show();
            if (fresh || Json.B(req, "reanimate", false)) Motion(n, enter, true, null);

            if (n.Buttons.Count > 0)
            {
                try { n.Win.Focus(true); } catch (Exception e) { warn.Add("take focus: " + e.Message); }
                Select(n, 0);
            }
            double dur = Json.N(req, "duration", 10);
            if (n.Buttons.Count > 0)
            {
                double cap = Math.Min(600, Math.Max(5, Json.N(req, "timeout", 60)));
                dur = dur <= 0 ? cap : Math.Min(dur, cap);
            }
            StartLife(n, dur);
            Log.W((fresh ? "show " : "replace ") + id + " " + w + "x" + h + " " + n.Anchor + (n.Buttons.Count > 0 ? " buttons=" + n.Buttons.Count : "") + (warn.Count > 0 ? " warnings=" + warn.Count : ""));
        }

        private static void StartLife(Note n, double seconds)
        {
            if (n.Life != null) { n.Life.Stop(); n.Life.Dispose(); n.Life = null; }
            if (seconds <= 0) return;
            n.Life = new Timer((uint)Math.Min(seconds * 1000, int.MaxValue));
            n.Life.Tick += (s, e) => { Close(n, "timeout", true); return false; };
            n.Life.Start();
        }

        private static void OnKey(Note n, Window.KeyEventArgs e)
        {
            if (n.Closing || e.Key.State != Key.StateType.Down || n.Buttons.Count == 0) return;
            string k = e.Key.KeyPressedName;
            switch (k)
            {
                case "Left": case "Up": Select(n, n.Sel - 1); break;
                case "Right": case "Down": Select(n, n.Sel + 1); break;
                case "Return": case "KP_Enter": case "Select":
                    var b = n.Buttons[n.Sel];
                    Log.W("button " + n.Id + "/" + b.action);
                    Callback(n, b.action);
                    Close(n, null, true);
                    break;
                case "XF86Back": case "Escape": case "BackSpace":
                    Close(n, "back", true);
                    break;
            }
        }

        private static void Select(Note n, int i)
        {
            if (n.Buttons.Count == 0) return;
            i = (i % n.Buttons.Count + n.Buttons.Count) % n.Buttons.Count;
            var warn = new List<object>();
            var prev = n.Buttons[n.Sel];
            if (prev.saved != null && prev.saved.Count > 0) Build.Apply(n, prev.v, prev.saved, warn, true);
            n.Sel = i;
            var cur = n.Buttons[i];
            if (cur.focus != null) Build.Apply(n, cur.v, cur.focus, warn, true);
        }

        // reason != null -> report it to the callback
        // Take a message off screen at once (a dashboard leaving with Art Mode); nothing else is stopped.
        public static void CloseById(string id)
        {
            App.OnMain(() => { if (All.TryGetValue(id, out var n)) Close(n, null, false); return true; });
        }

        public static void Close(Note n, string reason, bool animate)
        {
            if (n.Closing) return;
            n.Closing = true;
            if (reason != null) Callback(n, reason);
            if (n.Life != null) { n.Life.Stop(); }
            if (n.Buttons.Count > 0) { try { n.Win.Focus(false); } catch (Exception) { } }
            var exit = Json.O(n.Req, "exit") ?? Json.O(n.Req, "enter");
            if (animate) Motion(n, exit, false, () => App.Post(() => { DisposeNote(n); Restack(); }));   // not inside the animation's own Finished event
            else { DisposeNote(n); Restack(); }
        }

        private static void DisposeNote(Note n)
        {
            Log.W("closing " + n.Id);
            if (All.TryGetValue(n.Id, out var cur) && cur == n) All.Remove(n.Id);
            foreach (var k in n.Keep) StopKeep(k);
            n.Keep.Clear();
            try { n.Life?.Dispose(); } catch (Exception) { }
            try { n.Move?.Stop(); n.Move?.Dispose(); n.Move = null; } catch (Exception) { }
            try { n.Root?.Dispose(); } catch (Exception) { }
            try { n.Win.Dispose(); } catch (Exception) { }
            Media.Delete(n.Files);
            Log.W("closed " + n.Id);
        }

        private static void StopKeep(object k)
        {
            try
            {
                if (Build.StopMedia(k)) return;
                if (k is Animation a) { a.Stop(); a.Dispose(); }
                else if (k is Timer t) { t.Stop(); t.Dispose(); }
                else if (k is IDisposable d) d.Dispose();
            }
            catch (Exception) { }
        }

        private static int MotionDuration(Dictionary<string, object> spec, bool enter) =>
            string.Equals(Json.S(spec, "type"), "none", StringComparison.OrdinalIgnoreCase)
                ? 0 : Clamp((int)Json.N(spec, "ms", enter ? 400 : 300), 0, 5000);

        private static void Motion(Note n, Dictionary<string, object> spec, bool enter, Action done)
        {
            string type = (Json.S(spec, "type") ?? "slide").ToLowerInvariant();
            int ms = MotionDuration(spec, enter);
            var v = n.Root;
            if (ms == 0) { done?.Invoke(); return; }
            string from = (Json.S(spec, "from") ?? DefaultFrom(n.Anchor)).ToLowerInvariant();
            float dx = 0, dy = 0;
            if (type == "slide")
            {
                if (from == "top") dy = -n.H; else if (from == "bottom") dy = n.H; else if (from == "left") dx = -n.W; else dx = n.W;
            }
            var anim = new Animation(ms);
            var ease = new AlphaFunction(enter ? AlphaFunction.BuiltinFunctions.EaseOut : AlphaFunction.BuiltinFunctions.EaseIn);
            if (enter)
            {
                v.Opacity = 0f;
                if (type == "slide") { v.PositionX = dx; v.PositionY = dy; }
                if (type == "scale") v.Scale = new Vector3(0.8f, 0.8f, 1f);
                anim.AnimateTo(v, "Opacity", 1f, ease);
                if (type == "slide") { anim.AnimateTo(v, "PositionX", 0f, ease); anim.AnimateTo(v, "PositionY", 0f, ease); }
                if (type == "scale") anim.AnimateTo(v, "Scale", new Vector3(1f, 1f, 1f), ease);
            }
            else
            {
                anim.AnimateTo(v, "Opacity", 0f, ease);
                if (type == "slide") { anim.AnimateTo(v, "PositionX", dx, ease); anim.AnimateTo(v, "PositionY", dy, ease); }
                if (type == "scale") anim.AnimateTo(v, "Scale", new Vector3(0.8f, 0.8f, 1f), ease);
            }
            n.AddKeep(anim, v);
            if (done != null) anim.Finished += (s, e) => done();
            anim.Play();
        }

        private static string DefaultFrom(string anchor) =>
            anchor.EndsWith("right") ? "right" : anchor.EndsWith("left") ? "left" : anchor.StartsWith("bottom") ? "bottom" : "top";

        // Position every window: absolute ones as given, anchored ones stacked in arrival order.
        private static void Restack()
        {
            foreach (var (note, x, y) in Positions(All.Values)) MoveTo(note, x, y);
        }

        private static IEnumerable<(Note note, int x, int y)> Positions(IEnumerable<Note> notes)
        {
            var offs = new Dictionary<string, int>();
            foreach (var n in notes.Where(x => !x.Closing).OrderBy(x => x.Seq))
            {
                int x, y;
                if (n.Absolute) { x = n.X; y = n.Y; }
                else
                {
                    string a = n.Anchor;
                    x = a.EndsWith("left") ? n.Mx : a.EndsWith("right") ? ScreenW - n.W - n.Mx : (ScreenW - n.W) / 2;
                    bool bottom = a.StartsWith("bottom"), top = a.StartsWith("top");
                    y = top ? n.My : bottom ? ScreenH - n.H - n.My : (ScreenH - n.H) / 2;
                    if (n.Stack && (top || bottom))
                    {
                        offs.TryGetValue(a, out int o);
                        y += bottom ? -o : o;
                        offs[a] = o + n.H + n.Gap;
                    }
                }
                yield return (n, x, y);
            }
        }

        // Windows are not Views, so NUI animations cannot move them: step the position on a timer (~250 ms glide).
        private static void MoveTo(Note n, int x, int y)
        {
            try
            {
                if (n.Move != null) { n.Move.Stop(); n.Move.Dispose(); n.Move = null; }
                if (!n.Placed || (n.CurX == x && n.CurY == y)) { n.Win.Move(x, y); n.CurX = x; n.CurY = y; n.Placed = true; return; }
                int sx = n.CurX, sy = n.CurY, step = 0, steps = 15;
                var t = new Timer(16);
                t.Tick += (s, e) =>
                {
                    if (n.Closing && n.Win == null) return false;
                    step++;
                    double f = step / (double)steps; f = 1 - (1 - f) * (1 - f);   // ease out
                    n.CurX = (int)Math.Round(sx + (x - sx) * f); n.CurY = (int)Math.Round(sy + (y - sy) * f);
                    try { n.Win.Move(n.CurX, n.CurY); } catch (Exception) { return false; }
                    return step < steps;
                };
                n.Move = t;
                t.Start();
            }
            catch (Exception e) { Log.W("move " + n.Id + ": " + e.Message); }
        }

        private static void Callback(Note n, string action)
        {
            if (n.Callback == null) return;
            var body = Json.Write(new Dictionary<string, object> { ["id"] = n.Id, ["action"] = action, ["data"] = n.Data });
            string url = n.Callback;
            System.Threading.Tasks.Task.Run(async () =>
            {
                try { var r = await Client.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json")); Log.W("callback " + action + " -> " + (int)r.StatusCode); }
                catch (Exception e) { Log.W("callback failed: " + e.Message); }
            });
        }

        public static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
    }
}
