// Tizen Notifier: a resident overlay renderer for Samsung Tizen TVs, driven by Home Assistant over HTTP.
// Every look lives in the request (a tree of nodes); this app only renders. No code is ever loaded at runtime.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Tizen.NUI;
using Tizen.Applications;
using Timer = Tizen.NUI.Timer;

namespace TizenNotifier
{
    internal static class Log
    {
        private static readonly object L = new object();
        private static readonly LinkedList<string> Ring = new LinkedList<string>();
        private static string file;

        public static void Init(string dataDir) { file = System.IO.Path.Combine(dataDir, "notifier.log"); }

        public static void W(string msg)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + msg;
            lock (L)
            {
                Ring.AddLast(line);
                while (Ring.Count > 300) Ring.RemoveFirst();
                try
                {
                    if (file != null)
                    {
                        var fi = new FileInfo(file);
                        if (fi.Exists && fi.Length > 512 * 1024) { File.Delete(file + ".1"); File.Move(file, file + ".1"); }
                        File.AppendAllText(file, line + "\n");
                    }
                }
                catch (Exception) { }
            }
            try { Tizen.Log.Info("TIZNOTIFY", msg); } catch (Exception) { }
        }

        // Reads the log FILE (current + previous rotation), so lines from before a crash or restart are included.
        public static List<object> Tail(int n)
        {
            lock (L)
            {
                var lines = new List<string>();
                try
                {
                    if (file != null && File.Exists(file + ".1")) lines.AddRange(File.ReadAllLines(file + ".1"));
                    if (file != null && File.Exists(file)) lines.AddRange(File.ReadAllLines(file));
                }
                catch (Exception) { lines = new List<string>(Ring); }
                if (lines.Count == 0) lines = new List<string>(Ring);
                var l = new List<object>(lines);
                return l.GetRange(Math.Max(0, l.Count - n), Math.Min(n, l.Count));
            }
        }
    }

    internal static class Cfg
    {
        public const string Version = "1.1.0";
        public static string Token = "";
        public static int Port = 9123;
        public static List<string> Allow = new List<string>();
        public static string HaUrl, HaToken;
        public static string RelayUrl, RelayToken;   // video relay (relay/): base URL and optional bearer token
        // Relay video limits, measured 2026-11-15: Tizen 6 Frames ran 6 x 640-wide streams at 24 fps; the Tizen 5 TV ran 4
        // cleanly and stuttered at 6 (with another app playing underneath). One stream alone may be full screen on both.
#if TIZEN5
        public static int VideoMaxStreams = 4; public static double VideoBudgetMpx = 0.75;
#else
        public static int VideoMaxStreams = 6; public static double VideoBudgetMpx = 1.25;
#endif   // dashboards: base URL and a non-admin user's token (baked in; never logged)
        public static string DataDir, ResDir;
        public static readonly DateTime Started = DateTime.Now;

        public static void Load()
        {
            // res/config.json is baked in at build time (per-TV token). data/config.json holds runtime overrides (allow list).
            var baked = Json.Parse(File.ReadAllText(System.IO.Path.Combine(ResDir, "config.json")));
            Token = Json.S(baked, "token", "");
            Port = (int)Json.N(baked, "port", 9123);
            Allow = StrList(Json.A(baked, "allow"));
            HaUrl = Json.S(Json.O(baked, "ha"), "url")?.TrimEnd('/');
            HaToken = Json.S(Json.O(baked, "ha"), "token");
            RelayUrl = Json.S(Json.O(baked, "relay"), "url")?.TrimEnd('/');
            RelayToken = Json.S(Json.O(baked, "relay"), "token");
            string ov = System.IO.Path.Combine(DataDir, "config.json");
            if (File.Exists(ov))
            {
                try
                {
                    var o = Json.Parse(File.ReadAllText(ov));
                    if (Json.Has(o, "allow")) Allow = StrList(Json.A(o, "allow"));
                    SetVideo(Json.O(o, "video"));
                }
                catch (Exception e) { Log.W("override config unreadable: " + e.Message); }
            }
        }

        public static void SetVideo(Dictionary<string, object> v)
        {
            if (v == null) return;
            if (Json.Get(v, "max_streams") is double m) VideoMaxStreams = Math.Max(0, (int)m);
            if (Json.Get(v, "budget_mpx") is double b) VideoBudgetMpx = Math.Max(0.05, b);
        }

        // runtime overrides (data/config.json): allow list and video limits
        public static void Save()
        {
            File.WriteAllText(System.IO.Path.Combine(DataDir, "config.json"), Json.Write(new Dictionary<string, object>
            {
                ["allow"] = Allow, ["video"] = new Dictionary<string, object> { ["max_streams"] = (double)VideoMaxStreams, ["budget_mpx"] = VideoBudgetMpx },
            }));
        }

        public static void SaveAllow(List<string> allow) { Allow = allow; Save(); }

        private static List<string> StrList(List<object> l)
        {
            var r = new List<string>();
            if (l != null) foreach (var x in l) if (x is string s) r.Add(s);
            return r;
        }

        // URLs under the configured Home Assistant base get the HA token (camera snapshots, entity pictures).
        public static bool IsHa(string url) => !string.IsNullOrEmpty(HaUrl) && !string.IsNullOrEmpty(HaToken) && url != null &&
            (url == HaUrl || url.StartsWith(HaUrl + "/", StringComparison.OrdinalIgnoreCase));

        public static bool Allowed(string url)
        {
            try
            {
                var u = new Uri(url);
                if (u.Scheme != "http" && u.Scheme != "https" && u.Scheme != "rtsp") return false;
                if (IsHa(url) || Relay.IsRelay(url)) return true;
                foreach (var a in Allow) if (string.Equals(a, u.Host, StringComparison.OrdinalIgnoreCase) || string.Equals(a, u.Host + ":" + u.Port, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch (Exception) { }
            return false;
        }
    }

    internal sealed class App : NUIApplication
    {
        private static readonly ConcurrentQueue<Action> Q = new ConcurrentQueue<Action>();
        private Timer pump;
        public static Window MainWin;

        // Run on the NUI main loop and wait for the result (NUI is single-threaded).
        public static T OnMain<T>(Func<T> f, int timeoutMs = 15000)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Q.Enqueue(() => { try { tcs.SetResult(f()); } catch (Exception e) { tcs.SetException(e); } });
            if (!tcs.Task.Wait(timeoutMs)) throw new TimeoutException("main loop busy");
            return tcs.Task.Result;
        }

        public static void Post(Action a) => Q.Enqueue(a);

        protected override void OnCreate()
        {
            base.OnCreate();
            Cfg.DataDir = Tizen.Applications.Application.Current.DirectoryInfo.Data;
            Cfg.ResDir = Tizen.Applications.Application.Current.DirectoryInfo.Resource;
            Log.Init(Cfg.DataDir);
            Log.W("start v" + Cfg.Version + " runtime " + Environment.Version);
            try { Cfg.Load(); } catch (Exception e) { Log.W("config load failed: " + e.Message); }
            Mdi.File = System.IO.Path.Combine(Cfg.ResDir, "mdi.tsv.gz");
            // bundled fonts (res/fonts): the TV itself ships only Samsung's own family
            try
            {
                var fd = System.IO.Path.Combine(Cfg.ResDir, "fonts");
                if (Directory.Exists(fd)) Log.W("fonts " + fd + ": " + Tizen.NUI.FontClient.Instance.AddCustomFontDirectory(fd));
            }
            catch (Exception e) { Log.W("fonts: " + e.Message); }
            try { if (Directory.Exists(System.IO.Path.Combine(Cfg.DataDir, "svg"))) Directory.Delete(System.IO.Path.Combine(Cfg.DataDir, "svg"), true); } catch (Exception) { }
            foreach (var f in Directory.GetFiles(Cfg.DataDir, "media-*")) { try { File.Delete(f); } catch (Exception) { } }

#if TIZEN5
            MainWin = Window.Instance;
#else
            MainWin = GetDefaultWindow();
#endif
            HideMain();
            pump = new Timer(16);
            pump.Tick += (s, e) =>
            {
                int n = 0;
                while (n++ < 50 && Q.TryDequeue(out var a)) { try { a(); } catch (Exception ex) { Log.W("main-loop task failed: " + ex); } }
                return true;
            };
            pump.Start();
            new Thread(() => Http.Serve(Cfg.Port)) { IsBackground = true, Name = "http" }.Start();
            Dash.Resume();
        }

        // The app has no screen of its own: the main window stays (hiding it gets the app closed) but is transparent,
        // non-focusable and sent behind everything.
        private static void HideMain()
        {
#if TIZEN5
            Surface.SetupHost();   // Tizen 5: the app's own window IS the overlay host
            return;
#endif
            try { MainWin.BackgroundColor = Color.Transparent; } catch (Exception e) { Log.W("main bg: " + e.Message); }
            try { MainWin.SetTransparency(true); } catch (Exception e) { Log.W("main transparency: " + e.Message); }
            try { MainWin.SetOpaqueState(false); } catch (Exception e) { Log.W("main opaque: " + e.Message); }
            try { MainWin.SetAcceptFocus(false); } catch (Exception e) { Log.W("main focus: " + e.Message); }
            try { MainWin.Lower(); } catch (Exception e) { Log.W("main lower: " + e.Message); }
        }

        protected override void OnAppControlReceived(AppControlReceivedEventArgs e)
        {
            base.OnAppControlReceived(e);
            Log.W("launched again (app control)");
            HideMain();
        }

        protected override void OnResume() { base.OnResume(); HideMain(); }
        protected override void OnPause() { base.OnPause(); Log.W("paused"); }
        protected override void OnTerminate() { Log.W("terminate"); base.OnTerminate(); }

        private static void Main(string[] args) => new App().Run(args);
    }
}
