// Dashboards on the TV: HTTP endpoints, persistence across app restarts, and the bridge from DashSession to Notes.
//   POST /dashboard        {dashboard, view?, id?, x?, y?, w?, h?, scale?, theme?, as_user?, persist?}
//   GET  /dashboard        sessions and their status
//   GET  /dashboard/tree   ?id=main: the node tree last shown (debugging, previews)
//   POST /dashboard/stop   {id} | {all: true}
// Each session is shown as the message "dash:<id>". /dismiss of that id stops the session too.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace TizenNotifier
{
    internal sealed class TvDashHost : IDashHost
    {
        public void Log(string msg) => TizenNotifier.Log.W(msg);

        public void Show(DashSession s, Dictionary<string, object> tree)
        {
            var (code, res) = Notes.Notify(tree);
            var warn = Json.A(res, "warnings");
            TizenNotifier.Log.W("dash " + s.Opt.Id + ": show (" + s.CardsDrawn + " cards)" + (code != 200 ? " failed: " + Json.S(res, "error") : "") + (warn != null && warn.Count > 0 ? " warnings: " + Json.Write(warn.Take(5).ToList()) : ""));
        }

        public string VideoUrl(string src, int widthPx) => Relay.Url(src, widthPx, 0, true);

        public void Hide(DashSession s) => Notes.CloseById("dash:" + s.Opt.Id);

        public void Patch(DashSession s, Dictionary<string, object> ops)
        {
            var req = new Dictionary<string, object>(ops) { ["id"] = "dash:" + s.Opt.Id };
            var (code, res) = Notes.Update(req);
            if (code == 404) s.ForceShow();
            else if (code != 200) TizenNotifier.Log.W("dash " + s.Opt.Id + ": patch failed: " + Json.S(res, "error"));
        }

        public void Update(DashSession s, Dictionary<string, Dictionary<string, object>> changes)
        {
            var nodes = new Dictionary<string, object>();
            foreach (var kv in changes) nodes[kv.Key] = kv.Value;
            var (code, res) = Notes.Update(new Dictionary<string, object> { ["id"] = "dash:" + s.Opt.Id, ["nodes"] = nodes });
            if (code == 404) s.ForceShow();   // the message is gone (dismissed elsewhere or the window was lost): draw it again
            else if (code != 200) TizenNotifier.Log.W("dash " + s.Opt.Id + ": update failed: " + Json.S(res, "error"));
        }
    }

    internal static class Dash
    {
        private static readonly object L = new object();
        private static readonly Dictionary<string, DashSession> Sessions = new Dictionary<string, DashSession>();
        private static HaClient ha;
        private static readonly TvDashHost Host = new TvDashHost();
        private static string StoreFile => Path.Combine(Cfg.DataDir, "dashboards.json");

        private static HaClient Client()
        {
            if (ha == null) { ha = new HaClient(Cfg.HaUrl, Cfg.HaToken, m => Log.W(m)); ha.Start(); }
            return ha;
        }

        public static (int, object) Start(object req)
        {
            if (string.IsNullOrEmpty(Cfg.HaUrl) || string.IsNullOrEmpty(Cfg.HaToken))
                return (400, Http.Err("dashboards are not configured: build with HA_URL and HA_TOKEN_FILE"));
            if (!(req is Dictionary<string, object> r) || (Json.S(r, "dashboard") == null && Json.O(r, "config") == null)) return (400, Http.Err("dashboard (its url_path) or config missing"));
            var opt = DashOptions.From(r);
            if (!System.Text.RegularExpressions.Regex.IsMatch(opt.Id, "^[A-Za-z0-9_-]{1,40}$")) return (400, Http.Err("id: letters, digits, _ and - only"));
            lock (L)
            {
                if (Sessions.TryGetValue(opt.Id, out var old)) { old.Dispose(); Sessions.Remove(opt.Id); }
                var s = new DashSession(opt, Client(), Host);
                Sessions[opt.Id] = s;
                if (WantsArt(s) && !ArtNow()) s.SetVisible(false);   // started while someone is watching: don't flash over it
                s.Start();
                Watch();
                if (Json.B(r, "persist", true)) Save();
            }
            Log.W("dash " + opt.Id + ": start " + opt.Dashboard + (opt.View != null ? " view " + opt.View : ""));
            return (200, new Dictionary<string, object> { ["ok"] = true, ["id"] = opt.Id, ["message_id"] = "dash:" + opt.Id });
        }

        public static (int, object) Stop(object req)
        {
            bool all = Json.B(req, "all", false);
            string id = Json.S(req, "id");
            int n = StopIds(all ? null : new[] { id ?? "main" }, true);
            return (200, new Dictionary<string, object> { ["ok"] = true, ["stopped"] = (double)n });
        }

        // ids == null: all. closeMessage: also remove the message (not needed when /dismiss already closes it).
        public static int StopIds(IEnumerable<string> ids, bool closeMessage)
        {
            List<DashSession> stop;
            lock (L)
            {
                stop = Sessions.Where(kv => ids == null || ids.Contains(kv.Key)).Select(kv => kv.Value).ToList();
                foreach (var s in stop) { Sessions.Remove(s.Opt.Id); s.Dispose(); }
                if (stop.Count > 0) Save();
            }
            if (closeMessage && stop.Count > 0) Notes.Dismiss(new Dictionary<string, object> { ["ids"] = stop.Select(s => (object)("dash:" + s.Opt.Id)).ToList() });
            foreach (var s in stop) Log.W("dash " + s.Opt.Id + ": stopped");
            return stop.Count;
        }

        // ---- Art Mode: a dashboard with art_mode (the default on Frames) shows only while the Art Mode app is in front ----
#if TIZEN5
        private const bool ArtDefault = false;   // no Art Mode on Tizen 5 TVs
#else
        private const bool ArtDefault = true;
#endif
        private const string ArtApp = "org.tizen.art-app";
        private static Thread watcher;
        private static bool? artSeen;
        private static bool artErrorLogged;

        private static bool WantsArt(DashSession s) => s.Opt.ArtMode ?? ArtDefault;

        // Is the Art Mode app in front? On an error: yes (never hide a dashboard because the question failed).
        private static bool ArtNow()
        {
            try
            {
                bool art = Tizen.Applications.ApplicationManager.GetRunningApplicationsAsync().Result
                    .Any(c => c.ApplicationId == ArtApp && c.State == Tizen.Applications.ApplicationRunningContext.AppState.Foreground);
                artErrorLogged = false;
                return art;
            }
            catch (Exception e)
            {
                if (!artErrorLogged) { artErrorLogged = true; Log.W("art mode check failed: " + (e.InnerException ?? e).Message); }
                return true;
            }
        }

        // One background loop for all sessions: about once a second while any of them follows Art Mode.
        private static void Watch()
        {
            lock (L)
            {
                if (watcher != null) return;
                watcher = new Thread(() =>
                {
                    while (true)
                    {
                        Thread.Sleep(1000);
                        List<DashSession> follow;
                        lock (L) follow = Sessions.Values.Where(WantsArt).ToList();
                        if (follow.Count == 0) continue;
                        bool art = ArtNow();
                        if (artSeen != art) Log.W("art mode " + (art ? "on" : "off"));
                        artSeen = art;
                        foreach (var s in follow) try { s.SetVisible(art); } catch (Exception e) { Log.W("dash " + s.Opt.Id + ": " + e.Message); }
                    }
                }) { IsBackground = true, Name = "art-watch" };
                watcher.Start();
            }
        }

        public static Dictionary<string, object> Status()
        {
            lock (L)
                return new Dictionary<string, object>
                {
                    ["ok"] = true, ["configured"] = !string.IsNullOrEmpty(Cfg.HaUrl), ["ha_connected"] = ha?.Connected ?? false,
                    ["art_mode"] = artSeen,
                    ["sessions"] = Sessions.Values.Select(s => (object)s.StatusInfo()).ToList(),
                };
        }

        public static (int, object) Tree(string query)
        {
            string id = "main";
            foreach (var p in (query ?? "").Split('&')) if (p.StartsWith("id=")) id = Uri.UnescapeDataString(p.Substring(3));
            lock (L)
            {
                if (!Sessions.TryGetValue(id, out var s)) return (404, Http.Err("no dashboard session " + id));
                return (200, (object)s.Tree ?? new Dictionary<string, object> { ["ok"] = false, ["error"] = "not rendered yet" });
            }
        }

        private static void Save()
        {
            try
            {
                var list = Sessions.Values.Where(s => Json.B(s.Opt.Raw, "persist", true)).Select(s => (object)s.Opt.Raw).ToList();
                File.WriteAllText(StoreFile, Json.Write(list));
            }
            catch (Exception e) { Log.W("dash save failed: " + e.Message); }
        }

        // On app start: bring back the dashboards that were showing (the TV's power-off kills the app; Art Mode doesn't).
        public static void Resume()
        {
            if (string.IsNullOrEmpty(Cfg.HaUrl) || !File.Exists(StoreFile)) return;
            new Thread(() =>
            {
                Thread.Sleep(1500);   // let the main loop settle
                try
                {
                    if (Json.Parse(File.ReadAllText(StoreFile)) is List<object> l)
                        foreach (var r in l) { var (code, res) = Start(r); if (code != 200) Log.W("dash resume failed: " + Json.S(res, "error")); }
                }
                catch (Exception e) { Log.W("dash resume failed: " + e.Message); }
            }) { IsBackground = true, Name = "dash-resume" }.Start();
        }
    }
}
