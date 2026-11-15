// One live dashboard: loads its Lovelace config, subscribes to what it shows, renders a node tree and hands it to the
// host. Pure C# (the TV app and the desktop host both implement IDashHost).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TizenNotifier
{
    internal interface IDashHost
    {
        void Log(string msg);
        void Show(DashSession s, Dictionary<string, object> tree);                    // first render, or structure changed
        void Update(DashSession s, Dictionary<string, Dictionary<string, object>> changes);   // same structure: changed props by node name
        string VideoUrl(string src, int widthPx);   // live video through the relay, or null (then cards use snapshots)
    }

    internal sealed class DashOptions
    {
        public string Id = "main", Dashboard, View;   // View: index ("0") or path; null = first
        public double X, Y, W = 1920, H = 1080, Scale = 1.0;
        public string AsUser;                         // evaluate `user` conditions as this user id instead of the token's user
        public Dictionary<string, object> Theme = new Dictionary<string, object>();
        public Dictionary<string, object> Raw;        // the request, for persistence
        public Dictionary<string, object> Config;     // inline Lovelace config instead of reading `dashboard` from HA

        public static DashOptions From(Dictionary<string, object> r)
        {
            var o = new DashOptions { Raw = r };
            o.Id = Json.S(r, "id") ?? "main";
            o.Dashboard = Json.S(r, "dashboard");
            o.View = Json.S(r, "view");
            o.X = Json.N(r, "x", 0); o.Y = Json.N(r, "y", 0);
            o.W = Json.N(r, "w", 1920 - o.X); o.H = Json.N(r, "h", 1080 - o.Y);
            o.Scale = Math.Max(0.25, Math.Min(4, Json.N(r, "scale", 1.0)));
            o.AsUser = Json.S(r, "as_user");
            o.Theme = Json.O(r, "theme") ?? new Dictionary<string, object>();
            o.Config = Json.O(r, "config");
            if (o.Dashboard == null && o.Config != null) o.Dashboard = "inline";
            return o;
        }
    }

    internal sealed class Forecast { public DateTime Time; public string Condition; public double? Hi, Lo, Precip; }
    internal sealed class CalEvent { public DateTime Start, End; public bool AllDay; public string Summary, Calendar, Color, CalendarId; }

    internal sealed partial class DashSession : IDisposable
    {
        public readonly DashOptions Opt;
        public readonly IDashHost Host;
        public readonly HaClient Ha;
        private readonly object L = new object();
        private volatile bool disposed;

        // loaded once per connection
        public Dictionary<string, object> Config;
        public Dictionary<string, object> Icons = new Dictionary<string, object>();
        public Dictionary<string, Dictionary<string, object>> Registry = new Dictionary<string, Dictionary<string, object>>();
        public Dictionary<string, Dictionary<string, object>> Areas = new Dictionary<string, Dictionary<string, object>>();
        public Dictionary<string, string> DeviceArea = new Dictionary<string, string>();
        public string UserId, UserName;
        public TimeZoneInfo Tz = TimeZoneInfo.Local;
        public string ViewTitle;

        // live data
        private readonly Dictionary<string, Ent> states = new Dictionary<string, Ent>();
        private int entSub = -1;
        private HashSet<string> entSet = new HashSet<string>();
        private readonly Dictionary<string, (int sub, string value, bool used)> templates = new Dictionary<string, (int, string, bool)>();
        private readonly Dictionary<string, (List<double> t, List<double> v, DateTime fetched, bool used, bool busy)> history = new Dictionary<string, (List<double>, List<double>, DateTime, bool, bool)>();
        private readonly Dictionary<string, (List<Dictionary<string, object>> items, string state, bool busy, bool used)> todos = new Dictionary<string, (List<Dictionary<string, object>>, string, bool, bool)>();
        private readonly Dictionary<string, (List<CalEvent> ev, DateTime fetched, string state, bool busy, bool used)> calendars = new Dictionary<string, (List<CalEvent>, DateTime, string, bool, bool)>();
        private readonly Dictionary<string, (int sub, List<Forecast> f, bool used)> forecasts = new Dictionary<string, (int, List<Forecast>, bool)>();

        // render state
        private Dictionary<string, object> shownTree;
        private Dictionary<string, Dictionary<string, object>> shownFlat;
        private string shownShape;
        private Timer debounce, minute;
        public List<string> Unsupported = new List<string>(), Offscreen = new List<string>();
        public int CardsDrawn, Shows, Updates;
        public string Status = "starting", LastError;
        public DateTime LastRender;
        private DateTime loadedAt = DateTime.UtcNow;

        public DashSession(DashOptions opt, HaClient ha, IDashHost host)
        {
            Opt = opt; Ha = ha; Host = host;
        }

        public void Start()
        {
            Ha.OnConnected += OnConnected;
            Ha.OnDisconnected += OnDisconnected;
            if (Ha.Connected) Task.Run(() => OnConnected());
            else Ha.Start();
            minute = new Timer(_ => Dirty(), null, MsToNextMinute(), 60000);
        }

        private static int MsToNextMinute() { var n = DateTime.Now; return 60000 - (n.Second * 1000 + n.Millisecond) + 50; }

        private void OnDisconnected()
        {
            lock (L)
            {
                Status = "disconnected, retrying";
                entSub = -1;
                templates.Clear(); forecasts.Clear();
            }
        }

        private void OnConnected()
        {
            if (disposed) return;
            try
            {
                lock (L) Status = "loading";
                var cfg = Ha.Call(new Dictionary<string, object> { ["type"] = "get_config" });
                Format.TempUnit = Json.S(Json.O(cfg, "unit_system"), "temperature") ?? "°";
                try { var tz = Json.S(cfg, "time_zone"); if (tz != null) Tz = TimeZoneInfo.FindSystemTimeZoneById(tz); } catch (Exception) { Tz = TimeZoneInfo.Local; }
                var me = Ha.Call(new Dictionary<string, object> { ["type"] = "auth/current_user" });
                UserId = Json.S(me, "id"); UserName = Json.S(me, "name");
                Icons = Json.O(Ha.Call(new Dictionary<string, object> { ["type"] = "frontend/get_icons", ["category"] = "entity_component" }), "resources") ?? new Dictionary<string, object>();
                var reg = new Dictionary<string, Dictionary<string, object>>();
                var rl = Json.A(Ha.Call(new Dictionary<string, object> { ["type"] = "config/entity_registry/list_for_display" }), "entities");
                if (rl != null) foreach (var x in rl) if (x is Dictionary<string, object> d && Json.S(d, "ei") != null) reg[Json.S(d, "ei")] = d;
                Registry = reg;
                try
                {
                    var areas = new Dictionary<string, Dictionary<string, object>>();
                    foreach (var a in (Ha.Call(new Dictionary<string, object> { ["type"] = "config/area_registry/list" }) as List<object>) ?? new List<object>())
                        if (Json.S(a, "area_id") is string aid) areas[aid] = a as Dictionary<string, object>;
                    var dev = new Dictionary<string, string>();
                    foreach (var d in (Ha.Call(new Dictionary<string, object> { ["type"] = "config/device_registry/list" }) as List<object>) ?? new List<object>())
                        if (Json.S(d, "id") is string did && Json.S(d, "area_id") is string da) dev[did] = da;
                    Areas = areas; DeviceArea = dev;
                }
                catch (Exception e) { Host.Log("dash areas: " + e.Message); }
                LoadConfig();
                Ha.Subscribe(new Dictionary<string, object> { ["type"] = "subscribe_events", ["event_type"] = "lovelace_updated" }, ev =>
                {
                    var up = Json.S(Json.O(ev, "data"), "url_path");
                    if (up == Opt.Dashboard) Task.Run(() => { try { LoadConfig(); Dirty(); } catch (Exception e) { Fail(e); } });
                });
                lock (L) { Status = "live"; loadedAt = DateTime.UtcNow; }
                Dirty();
            }
            catch (Exception e) { Fail(e); }
        }

        private void Fail(Exception e)
        {
            lock (L) { LastError = (e.InnerException ?? e).Message; Status = "error"; }
            Host.Log("dash " + Opt.Id + ": " + LastError);
        }

        private void LoadConfig()
        {
            if (Opt.Config != null) { lock (L) Config = Opt.Config; ResubscribeEntities(); return; }
            var c = Ha.Call(new Dictionary<string, object> { ["type"] = "lovelace/config", ["url_path"] = Opt.Dashboard == "default" ? null : Opt.Dashboard });
            if (!(c is Dictionary<string, object> cd)) throw new InvalidOperationException("dashboard " + Opt.Dashboard + " has no config");
            if (Json.Has(cd, "strategy")) throw new InvalidOperationException("dashboard " + Opt.Dashboard + " is generated by a strategy (not supported)");
            lock (L) Config = cd;
            ResubscribeEntities();
        }

        // Entities referenced anywhere in the view's config (cards, conditions, camera selectors, ...).
        private static readonly Regex EntityId = new Regex(@"^[a-z_]+\.[a-z0-9_]+$");
        private void Collect(object o, HashSet<string> into)
        {
            if (o is string s) { if (s.Length < 120 && EntityId.IsMatch(s)) into.Add(s); }
            else if (o is Dictionary<string, object> d) { foreach (var v in d.Values) Collect(v, into); }
            else if (o is List<object> l) foreach (var v in l) Collect(v, into);
        }

        private void ResubscribeEntities()
        {
            var set = new HashSet<string>();
            Dictionary<string, object> view;
            lock (L) view = CurrentView();
            Collect(view, set);
            lock (L)
            {
                if (entSub >= 0 && set.SetEquals(entSet)) return;
                if (entSub >= 0) Ha.Unsubscribe(entSub);
                entSet = set;
                states.Clear();
            }
            if (set.Count == 0) return;
            // auto-entities filters over every entity, so such a view needs all of them
            bool all = Json.Write(view).Contains("custom:auto-entities");
            var msg = new Dictionary<string, object> { ["type"] = "subscribe_entities" };
            if (!all) msg["entity_ids"] = set.Cast<object>().ToList();
            else lock (L) entSet = new HashSet<string> { "*" };
            int id = Ha.Subscribe(msg, OnEntities);
            lock (L) entSub = id;
        }

        private void OnEntities(object ev)
        {
            bool changed = false;
            lock (L)
            {
                if (Json.O(ev, "a") is Dictionary<string, object> add)
                    foreach (var kv in add)
                    {
                        var e = new Ent { Id = kv.Key, State = Json.S(kv.Value, "s") };
                        e.Attr = Json.O(kv.Value, "a") ?? new Dictionary<string, object>();
                        e.LastChanged = Json.N(kv.Value, "lc", 0); e.LastUpdated = Json.N(kv.Value, "lu", e.LastChanged);
                        states[kv.Key] = e; changed = true;
                        OnState(e);
                    }
                if (Json.O(ev, "c") is Dictionary<string, object> chg)
                    foreach (var kv in chg)
                    {
                        if (!states.TryGetValue(kv.Key, out var e)) continue;
                        var plus = Json.O(kv.Value, "+");
                        if (plus != null)
                        {
                            if (Json.Has(plus, "s")) e.State = Json.S(plus, "s");
                            if (Json.O(plus, "a") is Dictionary<string, object> pa) foreach (var a in pa) e.Attr[a.Key] = a.Value;
                            if (Json.Get(plus, "lc") is double lc) { e.LastChanged = lc; e.LastUpdated = lc; }
                            if (Json.Get(plus, "lu") is double lu) e.LastUpdated = lu;
                        }
                        var minus = Json.O(kv.Value, "-");
                        if (Json.A(minus, "a") is List<object> rm) foreach (var a in rm) if (a is string k) e.Attr.Remove(k);
                        changed = true;
                        OnState(e);
                    }
                if (Json.A(ev, "r") is List<object> rem) foreach (var r in rem) if (r is string k) { states.Remove(k); changed = true; }
            }
            if (changed) Dirty();
        }

        // Called under L for every state change: keep graphs, to-dos and calendars current.
        private void OnState(Ent e)
        {
            if (e.Num != null)
                foreach (var kv in history)
                    if (kv.Key.StartsWith(e.Id + "|") && kv.Value.t.Count > 0 && e.LastUpdated > kv.Value.t[kv.Value.t.Count - 1])
                    { kv.Value.t.Add(e.LastUpdated); kv.Value.v.Add(e.Num.Value); }
        }

        public Dictionary<string, object> CurrentView()
        {
            var views = Json.A(Config, "views");
            if (views == null || views.Count == 0) return null;
            if (Opt.View != null)
            {
                if (int.TryParse(Opt.View, out var i) && i >= 0 && i < views.Count) return views[i] as Dictionary<string, object>;
                foreach (var v in views) if (Json.S(v, "path") == Opt.View) return v as Dictionary<string, object>;
            }
            return views[0] as Dictionary<string, object>;
        }

        // ---------------- data access for cards (called under L during a render) ----------------

        public Ent State(string id) => id != null && states.TryGetValue(id, out var e) ? e : null;
        public IEnumerable<string> AllEntityIds() => states.Keys.ToList();
        public string EffectiveUser => Opt.AsUser ?? UserId;
        public DateTime NowUtc => DateTime.UtcNow;
        public DateTime NowLocal => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Tz);
        public DateTime ToLocal(double unix) => TimeZoneInfo.ConvertTimeFromUtc(DateTimeOffset.FromUnixTimeMilliseconds((long)(unix * 1000)).UtcDateTime, Tz);
        public string StateText(Ent e) => Format.State(e, Registry, Tz, NowUtc);

        // A Jinja template rendered by HA (live). Returns the last result, or null until the first one arrives.
        public string Template(string tpl, Dictionary<string, object> vars = null)
        {
            if (string.IsNullOrEmpty(tpl)) return tpl;
            if (!tpl.Contains("{{") && !tpl.Contains("{%")) return tpl;
            string key = Json.Write(vars ?? new Dictionary<string, object>()) + "\u0001" + tpl;
            if (templates.TryGetValue(key, out var t)) { templates[key] = (t.sub, t.value, true); return t.value; }
            templates[key] = (-1, null, true);
            Task.Run(() =>
            {
                try
                {
                    var msg = new Dictionary<string, object> { ["type"] = "render_template", ["template"] = tpl, ["strict"] = false, ["report_errors"] = false };
                    if (vars != null) msg["variables"] = vars;
                    int id = Ha.Subscribe(msg, ev =>
                    {
                        string val = Json.Has(ev, "result") ? (Json.Get(ev, "result") is string s ? s : Json.Get(ev, "result") == null ? "" : Json.Write(Json.Get(ev, "result")).Trim('"')) : null;
                        if (val == null) return;
                        lock (L) { if (templates.TryGetValue(key, out var cur)) templates[key] = (cur.sub, val, cur.used); }
                        Dirty();
                    });
                    lock (L) { if (templates.TryGetValue(key, out var cur)) templates[key] = (id, cur.value, cur.used); else Ha.Unsubscribe(id); }
                }
                catch (Exception e) { Host.Log("dash template failed: " + e.Message); lock (L) { if (templates.TryGetValue(key, out var cur)) templates[key] = (-2, "", cur.used); } Dirty(); }
            });
            return null;
        }

        // Numeric history for the last `hours` (fetched, then kept live from state changes; refreshed every 5 minutes).
        public (List<double> t, List<double> v) History(string id, double hours)
        {
            string key = id + "|" + hours.ToString(CultureInfo.InvariantCulture);
            if (history.TryGetValue(key, out var h))
            {
                history[key] = (h.t, h.v, h.fetched, true, h.busy);
                if (!h.busy && (DateTime.UtcNow - h.fetched).TotalMinutes > 5) FetchHistory(key, id, hours);
                // keep one point from before the window so the line starts at the left edge
                double from = (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds - hours * 3600;
                int cut = 0;
                while (cut + 1 < h.t.Count && h.t[cut + 1] <= from) cut++;
                if (cut > 0) { h.t.RemoveRange(0, cut); h.v.RemoveRange(0, cut); }
                return (h.t, h.v);
            }
            history[key] = (new List<double>(), new List<double>(), DateTime.MinValue, true, false);
            FetchHistory(key, id, hours);
            return (new List<double>(), new List<double>());
        }

        private void FetchHistory(string key, string id, double hours)
        {
            if (history.TryGetValue(key, out var h0)) history[key] = (h0.t, h0.v, h0.fetched, h0.used, true);
            Task.Run(() =>
            {
                var t = new List<double>(); var v = new List<double>();
                try
                {
                    var start = DateTime.UtcNow.AddHours(-hours).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
                    var r = Ha.Call(new Dictionary<string, object>
                    {
                        ["type"] = "history/history_during_period", ["start_time"] = start, ["entity_ids"] = new List<object> { id },
                        ["minimal_response"] = true, ["no_attributes"] = true, ["significant_changes_only"] = false,
                    });
                    if (Json.A(r, id) is List<object> pts)
                        foreach (var p in pts)
                            if (double.TryParse(Json.S(p, "s"), NumberStyles.Float, CultureInfo.InvariantCulture, out var val))
                            { t.Add(Json.N(p, "lu", Json.N(p, "lc", 0))); v.Add(val); }
                }
                catch (Exception e) { Host.Log("dash history " + id + ": " + e.Message); }
                lock (L) history[key] = (t, v, DateTime.UtcNow, true, false);
                Dirty();
            });
        }

        public List<Dictionary<string, object>> Todo(string id)
        {
            var e = State(id);
            if (todos.TryGetValue(id, out var td))
            {
                todos[id] = (td.items, td.state, td.busy, true);
                if (!td.busy && (td.items == null || (e != null && td.state != e.State))) FetchTodo(id);   // state = open item count
                return td.items;
            }
            todos[id] = (null, null, false, true);
            FetchTodo(id);
            return null;
        }

        private void FetchTodo(string id)
        {
            var e = State(id);
            string st = e?.State;
            if (todos.TryGetValue(id, out var t0)) todos[id] = (t0.items, st, true, t0.used);
            Task.Run(() =>
            {
                List<Dictionary<string, object>> items = null;
                try
                {
                    var r = Ha.Call(new Dictionary<string, object> { ["type"] = "todo/item/list", ["entity_id"] = id });
                    items = (Json.A(r, "items") ?? new List<object>()).OfType<Dictionary<string, object>>().ToList();
                }
                catch (Exception ex) { Host.Log("dash todo " + id + ": " + ex.Message); items = new List<Dictionary<string, object>>(); }
                lock (L) { if (todos.TryGetValue(id, out var cur)) todos[id] = (items, cur.state, false, cur.used); }
                Dirty();
            });
        }

        // Events of these calendars from `from` (local date, default today) for `days` days. Refetched every 15 minutes or
        // when a calendar's state changes; each (calendar, range) is cached separately.
        public List<CalEvent> Events(IList<(string id, string color)> cals, int days, DateTime? from = null)
        {
            var start = (from ?? NowLocal).Date;
            var all = new List<CalEvent>();
            foreach (var (id, color) in cals)
            {
                string key = id + "|" + start.ToString("yyyy-MM-dd") + "|" + days;
                string st = State(id)?.State;
                if (calendars.TryGetValue(key, out var c))
                {
                    if (c.state != st) c.fetched = DateTime.MinValue;
                    calendars[key] = (c.ev, c.fetched, st, c.busy, true);
                    if (!c.busy && (DateTime.UtcNow - c.fetched).TotalMinutes > 15) FetchCalendar(key, id, start, days, color);
                    if (c.ev != null) all.AddRange(c.ev);
                }
                else { calendars[key] = (null, DateTime.MinValue, st, false, true); FetchCalendar(key, id, start, days, color); }
            }
            return all.OrderBy(x => x.Start).ToList();
        }

        private void FetchCalendar(string key, string id, DateTime startLocal, int days, string color)
        {
            if (calendars.TryGetValue(key, out var c0)) calendars[key] = (c0.ev, c0.fetched, c0.state, true, c0.used);
            Task.Run(() =>
            {
                var list = new List<CalEvent>();
                try
                {
                    var start = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(startLocal, DateTimeKind.Unspecified), Tz).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
                    var end = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(startLocal.AddDays(days), DateTimeKind.Unspecified), Tz).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
                    var r = Ha.Get("/api/calendars/" + id + "?start=" + Uri.EscapeDataString(start) + "&end=" + Uri.EscapeDataString(end));
                    string calName;
                    lock (L) calName = Format.Name(State(id), null);
                    if (r is List<object> evs)
                        foreach (var ev in evs)
                        {
                            var s = Json.O(ev, "start"); var en = Json.O(ev, "end");
                            var e = new CalEvent { Summary = Json.S(ev, "summary") ?? "", Calendar = calName, Color = color, CalendarId = id };
                            if (Json.S(s, "date") != null)
                            {
                                e.AllDay = true;
                                e.Start = DateTime.ParseExact(Json.S(s, "date"), "yyyy-MM-dd", CultureInfo.InvariantCulture);
                                e.End = Json.S(en, "date") != null ? DateTime.ParseExact(Json.S(en, "date"), "yyyy-MM-dd", CultureInfo.InvariantCulture) : e.Start.AddDays(1);
                            }
                            else
                            {
                                e.Start = TimeZoneInfo.ConvertTime(DateTimeOffset.Parse(Json.S(s, "dateTime"), CultureInfo.InvariantCulture), Tz).DateTime;
                                e.End = Json.S(en, "dateTime") != null ? TimeZoneInfo.ConvertTime(DateTimeOffset.Parse(Json.S(en, "dateTime"), CultureInfo.InvariantCulture), Tz).DateTime : e.Start;
                            }
                            list.Add(e);
                        }
                }
                catch (Exception ex) { Host.Log("dash calendar " + id + ": " + ex.Message); }
                lock (L) { if (calendars.TryGetValue(key, out var cur)) calendars[key] = (list, DateTime.UtcNow, cur.state, false, cur.used); }
                Dirty();
            });
        }

        // ---- area membership (entity area, else its device's area) ----
        public string AreaOf(string entityId)
        {
            if (!Registry.TryGetValue(entityId, out var r)) return null;
            if (Json.S(r, "ai") is string a) return a;
            return Json.S(r, "di") is string d && DeviceArea.TryGetValue(d, out var da) ? da : null;
        }

        public IEnumerable<string> EntitiesIn(string area) => Registry.Keys.Where(id => AreaOf(id) == area && Json.Get(Registry[id], "hb") == null && Json.Get(Registry[id], "ec") == null);

        // ---- logbook (refreshed every minute, or when one of its entities changes) ----
        private readonly Dictionary<string, (List<Dictionary<string, object>> rows, DateTime fetched, string sig, bool busy)> logbooks = new Dictionary<string, (List<Dictionary<string, object>>, DateTime, string, bool)>();

        public List<Dictionary<string, object>> Logbook(IList<string> ids, double hours)
        {
            string key = string.Join(",", ids) + "|" + hours.ToString(CultureInfo.InvariantCulture);
            string sig = string.Join(",", ids.Select(i => State(i)?.LastChanged.ToString(CultureInfo.InvariantCulture) ?? ""));
            logbooks.TryGetValue(key, out var lb);
            if (lb.rows == null || (!lb.busy && (lb.sig != sig || (DateTime.UtcNow - lb.fetched).TotalSeconds > 60)))
            {
                logbooks[key] = (lb.rows, lb.fetched, sig, true);
                Task.Run(() =>
                {
                    var rows = new List<Dictionary<string, object>>();
                    try
                    {
                        var r = Ha.Call(new Dictionary<string, object>
                        {
                            ["type"] = "logbook/get_events", ["entity_ids"] = ids.Cast<object>().ToList(),
                            ["start_time"] = DateTime.UtcNow.AddHours(-hours).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                        });
                        if (r is List<object> l) rows = l.OfType<Dictionary<string, object>>().Reverse().ToList();   // newest first
                    }
                    catch (Exception e) { Host.Log("dash logbook: " + e.Message); }
                    lock (L) logbooks[key] = (rows, DateTime.UtcNow, sig, false);
                    Dirty();
                });
            }
            return lb.rows;
        }

        // ---- a camera's live stream (HA HLS URL with its own token; HA returns the same URL while the stream is in use) ----
        private readonly Dictionary<string, (string url, DateTime fetched, bool busy)> streams = new Dictionary<string, (string, DateTime, bool)>();

        public string CameraStream(string entityId)
        {
            if (entityId == null || !entityId.StartsWith("camera.")) return null;
            streams.TryGetValue(entityId, out var st);
            if (!st.busy && (st.fetched == default(DateTime) || (DateTime.UtcNow - st.fetched).TotalSeconds > (st.url == null ? 120 : 60)))
            {
                streams[entityId] = (st.url, st.fetched, true);
                Task.Run(() =>
                {
                    string url = null;
                    try { url = Json.S(Ha.Call(new Dictionary<string, object> { ["type"] = "camera/stream", ["entity_id"] = entityId }), "url"); }
                    catch (Exception e) { Host.Log("dash camera stream " + entityId + ": " + e.Message); }
                    bool changed;
                    lock (L) { changed = st.url != url; streams[entityId] = (url, DateTime.UtcNow, false); }
                    if (changed) Dirty();
                });
            }
            return st.url == null ? null : (st.url.StartsWith("http") ? st.url : Ha.Url + st.url);
        }

        // ---- long-term statistics (refreshed every 5 minutes) ----
        private readonly Dictionary<string, (object value, DateTime fetched, bool busy)> stats = new Dictionary<string, (object, DateTime, bool)>();

        private object Stat(string key, Dictionary<string, object> msg)
        {
            stats.TryGetValue(key, out var st);
            if (!st.busy && (st.value == null || (DateTime.UtcNow - st.fetched).TotalMinutes > 5))
            {
                stats[key] = (st.value, st.fetched, true);
                Task.Run(() =>
                {
                    object v = null;
                    try { v = Ha.Call(msg); } catch (Exception e) { Host.Log("dash statistics: " + e.Message); }
                    lock (L) stats[key] = (v ?? new Dictionary<string, object>(), DateTime.UtcNow, false);
                    Dirty();
                });
            }
            return st.value;
        }

        // One value over a calendar period ("today", "this week" ...): stat = mean|min|max|change.
        public double? StatisticValue(string id, string stat, string period)
        {
            var msg = new Dictionary<string, object>
            {
                ["type"] = "recorder/statistic_during_period", ["statistic_id"] = id, ["types"] = new List<object> { stat },
                ["calendar"] = new Dictionary<string, object> { ["period"] = period ?? "day" },
            };
            var v = Stat("one|" + id + "|" + stat + "|" + period, msg);
            return Json.Get(v, stat) is double d ? d : (double?)null;
        }

        // Series of hourly / daily / ... values: each point (start unix s, value).
        public List<(double t, double v)> StatisticSeries(string id, string period, string stat, double days)
        {
            var msg = new Dictionary<string, object>
            {
                ["type"] = "recorder/statistics_during_period", ["statistic_ids"] = new List<object> { id }, ["period"] = period,
                ["types"] = new List<object> { stat }, ["start_time"] = DateTime.UtcNow.AddDays(-days).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            };
            var v = Stat("series|" + id + "|" + period + "|" + stat + "|" + days.ToString(CultureInfo.InvariantCulture), msg);
            var list = new List<(double, double)>();
            foreach (var p in Json.A(v, id) ?? new List<object>())
                if (Json.Get(p, stat) is double val) list.Add((Json.N(p, "start", 0) / 1000.0, val));
            return list;
        }

        public List<Forecast> Forecasts(string id, string type)
        {
            string key = id + "|" + type;
            if (forecasts.TryGetValue(key, out var f)) { forecasts[key] = (f.sub, f.f, true); return f.f; }
            forecasts[key] = (-1, null, true);
            Task.Run(() =>
            {
                try
                {
                    int sub = Ha.Subscribe(new Dictionary<string, object> { ["type"] = "weather/subscribe_forecast", ["entity_id"] = id, ["forecast_type"] = type }, ev =>
                    {
                        var list = new List<Forecast>();
                        foreach (var x in Json.A(ev, "forecast") ?? new List<object>())
                        {
                            var fc = new Forecast { Condition = Json.S(x, "condition") };
                            if (DateTimeOffset.TryParse(Json.S(x, "datetime"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto)) fc.Time = TimeZoneInfo.ConvertTime(dto, Tz).DateTime;
                            if (Json.Get(x, "temperature") is double hi) fc.Hi = hi;
                            if (Json.Get(x, "templow") is double lo) fc.Lo = lo;
                            if (Json.Get(x, "precipitation_probability") is double pp) fc.Precip = pp;
                            list.Add(fc);
                        }
                        lock (L) { if (forecasts.TryGetValue(key, out var cur)) forecasts[key] = (cur.sub, list, cur.used); }
                        Dirty();
                    });
                    lock (L) { if (forecasts.TryGetValue(key, out var cur)) forecasts[key] = (sub, cur.f, cur.used); }
                }
                catch (Exception e) { Host.Log("dash forecast " + id + ": " + e.Message); }
            });
            return null;
        }

        // ---------------- render loop ----------------

        public void Dirty()
        {
            if (disposed) return;
            lock (L)
            {
                if (disposed) return;
                if (debounce == null) debounce = new Timer(_ => Render(), null, 250, Timeout.Infinite);
                else try { debounce.Change(250, Timeout.Infinite); } catch (ObjectDisposedException) { }
            }
        }

        private readonly object renderLock = new object();
        private void Render()
        {
            if (disposed) return;
            lock (renderLock) RenderLocked();
        }

        private void RenderLocked()
        {
            Dictionary<string, object> tree;
            try
            {
                lock (L)
                {
                    if (Config == null) return;
                    foreach (var k in templates.Keys.ToList()) templates[k] = (templates[k].sub, templates[k].value, false);
                    var view = CurrentView();
                    ViewTitle = Json.S(view, "title");
                    Unsupported = new List<string>(); Offscreen = new List<string>();
                    CardsDrawn = 0;
                    tree = Layout.Render(this, view);
                    // first draw: give templates / history / lists that were just requested a moment to arrive, so the
                    // overlay appears once instead of drawing and immediately redrawing
                    bool waiting = templates.Values.Any(t => t.value == null && t.sub != -2) || history.Values.Any(h => h.busy) || todos.Values.Any(t => t.busy) || calendars.Values.Any(c => c.busy);
                    if (shownShape == null && waiting && (DateTime.UtcNow - loadedAt).TotalSeconds < 2.5) { if (!disposed) try { debounce.Change(300, Timeout.Infinite); } catch (ObjectDisposedException) { } return; }
                    // drop subscriptions for templates no render asked for any more
                    foreach (var k in templates.Where(t => !t.Value.used && t.Value.sub >= 0).Select(t => t.Key).ToList()) { Ha.Unsubscribe(templates[k].sub); templates.Remove(k); }
                    LastRender = DateTime.UtcNow;
                }
            }
            catch (Exception e) { Fail(e); Host.Log("dash render failed: " + e); return; }
            Publish(tree);
        }

        // Same structure as what is shown -> only changed properties; otherwise a full show.
        private void Publish(Dictionary<string, object> tree)
        {
            var flat = new Dictionary<string, Dictionary<string, object>>();
            var shape = new System.Text.StringBuilder();
            Flatten(Json.O(tree, "root"), flat, shape);
            string sh = shape.ToString() + "|" + Json.Write(new[] { Json.Get(tree, "w"), Json.Get(tree, "h"), Json.Get(tree, "x"), Json.Get(tree, "y") });
            if (shownShape != null && sh == shownShape)
            {
                Updates++;
                var changes = new Dictionary<string, Dictionary<string, object>>();
                foreach (var kv in flat)
                {
                    var old = shownFlat[kv.Key];
                    Dictionary<string, object> diff = null;
                    foreach (var p in kv.Value)
                        if (!old.TryGetValue(p.Key, out var ov) || Json.Write(ov) != Json.Write(p.Value)) (diff = diff ?? new Dictionary<string, object>())[p.Key] = p.Value;
                    if (diff != null)
                    {
                        if (diff.ContainsKey("src") || diff.ContainsKey("color")) diff["type"] = kv.Value.TryGetValue("type", out var t) ? t : "box";
                        changes[kv.Key] = diff;
                    }
                }
                shownTree = tree; shownFlat = flat;
                if (changes.Count > 0) Host.Update(this, changes);
                return;
            }
            shownTree = tree; shownFlat = flat; shownShape = sh;
            Shows++;
            Host.Show(this, tree);
        }

        public void ForceShow() { shownShape = null; Dirty(); }

        private static void Flatten(Dictionary<string, object> n, Dictionary<string, Dictionary<string, object>> flat, System.Text.StringBuilder shape)
        {
            if (n == null) return;
            var name = Json.S(n, "name");
            var props = new Dictionary<string, object>();
            foreach (var kv in n) if (kv.Key != "children" && kv.Key != "name") props[kv.Key] = kv.Value;
            flat[name] = props;
            // type and anything that can't be changed in place are part of the shape. Geometry of boxes, text and plain
            // images can be changed in place; a rounded or refreshing image is a wrapper, so its geometry is shape.
            bool wrapped = (Json.S(n, "type") == "image" && (Json.Has(n, "radius") || Json.Has(n, "refresh_ms"))) || Json.S(n, "type") == "mjpeg" || Json.S(n, "type") == "video";
            shape.Append(name).Append(':').Append(Json.S(n, "type")).Append(wrapped ? Json.Write(new[] { Json.Get(n, "x"), Json.Get(n, "y"), Json.Get(n, "w"), Json.Get(n, "h") }) : "")
                 .Append(Json.Has(n, "refresh_ms") || Json.S(n, "type") == "mjpeg" || Json.S(n, "type") == "video" ? "r" + Json.S(n, "src") : "").Append(Json.Has(n, "animate") ? "a" : "").Append(Json.Has(n, "radius") ? "c" : "")
                 .Append(Json.Has(n, "gradient") ? "g" : "").Append(Json.Has(n, "markup") ? "m" : "")
                 .Append('[').Append(string.Join(",", props.Keys.OrderBy(k => k, StringComparer.Ordinal))).Append("];");
            var kids = Json.A(n, "children");
            if (kids != null) foreach (var c in kids) Flatten(c as Dictionary<string, object>, flat, shape);
        }

        public Dictionary<string, object> Tree => shownTree;

        public Dictionary<string, object> StatusInfo()
        {
            lock (L)
                return new Dictionary<string, object>
                {
                    ["id"] = Opt.Id, ["dashboard"] = Opt.Dashboard, ["view"] = ViewTitle ?? Opt.View, ["status"] = Status, ["connected"] = Ha.Connected,
                    ["user"] = UserName, ["cards_drawn"] = (double)CardsDrawn, ["unsupported"] = Unsupported.Distinct().Cast<object>().ToList(), ["offscreen"] = Offscreen.Cast<object>().ToList(),
                    ["error"] = LastError ?? Ha.LastError, ["entities"] = (double)entSet.Count, ["templates"] = (double)templates.Count, ["redraws"] = (double)Shows, ["updates"] = (double)Updates,
                    ["last_render"] = LastRender == default(DateTime) ? null : LastRender.ToString("o"),
                };
        }

        public void Dispose()
        {
            disposed = true;
            Ha.OnConnected -= OnConnected;
            Ha.OnDisconnected -= OnDisconnected;
            try { debounce?.Dispose(); minute?.Dispose(); } catch (Exception) { }
            lock (L)
            {
                if (entSub >= 0) Ha.Unsubscribe(entSub);
                foreach (var t in templates.Values) if (t.sub >= 0) Ha.Unsubscribe(t.sub);
                foreach (var f in forecasts.Values) if (f.sub >= 0) Ha.Unsubscribe(f.sub);
            }
        }
    }
}
