// Read-only Home Assistant client: websocket (requests + subscriptions) and REST GET. Pure C#, no NUI.
// It refuses every websocket command that is not on the read-only list below, so dashboards can never act on HA.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TizenNotifier
{
    internal sealed class HaClient : IDisposable
    {
        // Everything a dashboard needs to read; nothing that changes state. (Checked with a non-admin user, HA 2026.10.)
        private static readonly HashSet<string> ReadOnly = new HashSet<string>
        {
            "auth/current_user", "get_config", "get_states", "subscribe_entities", "unsubscribe_events",
            "lovelace/config", "lovelace/dashboards/list", "render_template", "frontend/get_icons",
            "history/history_during_period", "todo/item/list", "weather/subscribe_forecast",
            "config/entity_registry/list_for_display", "subscribe_events",
            "logbook/get_events", "recorder/statistic_during_period", "recorder/statistics_during_period",
            "config/area_registry/list", "config/device_registry/list", "camera/stream",
        };
        private static readonly HashSet<string> Events = new HashSet<string> { "lovelace_updated" };   // subscribe_events types

        public readonly string Url, Token;
        private readonly Action<string> log;
        private readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        private readonly object L = new object();
        private readonly Dictionary<int, TaskCompletionSource<object>> pending = new Dictionary<int, TaskCompletionSource<object>>();
        private readonly Dictionary<int, Action<object>> subs = new Dictionary<int, Action<object>>();
        private readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);
        private ClientWebSocket ws;
        private int nextId;
        private volatile bool stop;
        private Thread thread;

        public bool Connected { get; private set; }
        public string LastError { get; private set; }
        public event Action OnConnected;      // after auth; subscriptions from before are gone and must be redone
        public event Action OnDisconnected;

        public HaClient(string url, string token, Action<string> log)
        {
            Url = url.TrimEnd('/'); Token = token; this.log = log;
        }

        public void Start()
        {
            if (thread != null) return;
            thread = new Thread(Loop) { IsBackground = true, Name = "ha" };
            thread.Start();
        }

        private void Loop()
        {
            int backoff = 2;
            while (!stop)
            {
                try
                {
                    Connect();
                    backoff = 2;
                    LastError = null;
                    Connected = true;
                    log("ha: connected");
                    Task.Run(() => { try { OnConnected?.Invoke(); } catch (Exception e) { log("ha: on-connect failed: " + e); } });
                    Receive();
                }
                catch (Exception e)
                {
                    LastError = (e.InnerException ?? e).Message;
                    if (!stop) log("ha: " + LastError);
                }
                bool was = Connected;
                Connected = false;
                FailAll();
                try { ws?.Dispose(); } catch (Exception) { }
                ws = null;
                if (was) { try { OnDisconnected?.Invoke(); } catch (Exception) { } }
                for (int i = 0; i < backoff * 10 && !stop; i++) Thread.Sleep(100);
                backoff = Math.Min(60, backoff * 2);
            }
        }

        private void Connect()
        {
            var u = new Uri(Url);
            var wsu = (u.Scheme == "https" ? "wss://" : "ws://") + u.Authority + u.AbsolutePath.TrimEnd('/') + "/api/websocket";
            ws = new ClientWebSocket();
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            using (var cts = new CancellationTokenSource(15000)) ws.ConnectAsync(new Uri(wsu), cts.Token).Wait();
            var hello = Json.S(Read(), "type");
            if (hello != "auth_required") throw new InvalidOperationException("unexpected hello " + hello);
            SendRaw(new Dictionary<string, object> { ["type"] = "auth", ["access_token"] = Token });
            var a = Read();
            if (Json.S(a, "type") != "auth_ok") throw new InvalidOperationException("auth failed: " + (Json.S(a, "message") ?? Json.S(a, "type")));
        }

        private object Read()
        {
            var buf = new byte[64 * 1024];
            var ms = new MemoryStream();
            while (true)
            {
                var r = ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None).Result;
                if (r.MessageType == WebSocketMessageType.Close) throw new IOException("closed by HA");
                ms.Write(buf, 0, r.Count);
                if (r.EndOfMessage) break;
                if (ms.Length > 64 * 1024 * 1024) throw new IOException("message too large");
            }
            return Json.Parse(Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length));
        }

        private void Receive()
        {
            while (!stop)
            {
                var m = Read();
                if (m is List<object> batch) { foreach (var x in batch) Dispatch(x); }
                else Dispatch(m);
            }
        }

        private void Dispatch(object m)
        {
            int id = (int)Json.N(m, "id", -1);
            string type = Json.S(m, "type");
            if (type == "result")
            {
                TaskCompletionSource<object> t;
                lock (L) { if (pending.TryGetValue(id, out t)) pending.Remove(id); }
                if (t == null) return;
                if (Json.B(m, "success", false)) t.TrySetResult(Json.Get(m, "result"));
                else
                {
                    lock (L) subs.Remove(id);
                    t.TrySetException(new InvalidOperationException(Json.S(Json.O(m, "error"), "code") + ": " + Json.S(Json.O(m, "error"), "message")));
                }
            }
            else if (type == "event")
            {
                Action<object> cb;
                lock (L) subs.TryGetValue(id, out cb);
                if (cb == null) return;
                try { cb(Json.Get(m, "event")); } catch (Exception e) { log("ha: event handler failed: " + e.Message); }
            }
        }

        private void FailAll()
        {
            List<TaskCompletionSource<object>> ts;
            lock (L) { ts = new List<TaskCompletionSource<object>>(pending.Values); pending.Clear(); subs.Clear(); }
            foreach (var t in ts) t.TrySetException(new IOException("disconnected"));
        }

        private void SendRaw(Dictionary<string, object> msg)
        {
            var b = Encoding.UTF8.GetBytes(Json.Write(msg));
            sendLock.Wait();
            try { ws.SendAsync(new ArraySegment<byte>(b), WebSocketMessageType.Text, true, CancellationToken.None).Wait(); }
            finally { sendLock.Release(); }
        }

        private void Check(Dictionary<string, object> msg)
        {
            string t = Json.S(msg, "type");
            if (t == null || !ReadOnly.Contains(t)) throw new InvalidOperationException("refused: " + t + " is not a read-only command");
            if (t == "subscribe_events" && !Events.Contains(Json.S(msg, "event_type") ?? "")) throw new InvalidOperationException("refused: event " + Json.S(msg, "event_type"));
        }

        private (int id, Task<object> task) Send(Dictionary<string, object> msg, Action<object> onEvent)
        {
            Check(msg);
            var w = ws;
            if (!Connected || w == null) throw new IOException("not connected");
            var t = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            int id;
            // HA requires ids to increase in arrival order: number and send under one lock
            sendLock.Wait();
            try
            {
                lock (L)
                {
                    id = ++nextId;
                    pending[id] = t;
                    if (onEvent != null) subs[id] = onEvent;
                }
                var b = Encoding.UTF8.GetBytes(Json.Write(new Dictionary<string, object>(msg) { ["id"] = (double)id }));
                w.SendAsync(new ArraySegment<byte>(b), WebSocketMessageType.Text, true, CancellationToken.None).Wait();
            }
            finally { sendLock.Release(); }
            return (id, t.Task);
        }

        // Request -> result (throws on error or timeout).
        public object Call(Dictionary<string, object> msg, int timeoutMs = 30000)
        {
            var (_, task) = Send(msg, null);
            if (!task.Wait(timeoutMs)) throw new TimeoutException(Json.S(msg, "type") + " timed out");
            return task.Result;
        }

        // Subscription -> its id (events go to onEvent on the receive thread). Throws if HA refuses it.
        public int Subscribe(Dictionary<string, object> msg, Action<object> onEvent, int timeoutMs = 30000)
        {
            var (id, task) = Send(msg, onEvent);
            if (!task.Wait(timeoutMs)) { lock (L) subs.Remove(id); throw new TimeoutException(Json.S(msg, "type") + " timed out"); }
            var _ = task.Result;
            return id;
        }

        public void Unsubscribe(int id)
        {
            lock (L) subs.Remove(id);
            if (!Connected) return;
            try { Send(new Dictionary<string, object> { ["type"] = "unsubscribe_events", ["subscription"] = (double)id }, null); }
            catch (Exception) { }
        }

        // REST GET under /api (read-only by construction).
        public object Get(string path)
        {
            if (!path.StartsWith("/api/")) throw new ArgumentException("REST path must start with /api/");
            var req = new HttpRequestMessage(HttpMethod.Get, Url + path);
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Token);
            var r = http.SendAsync(req).Result;
            var body = r.Content.ReadAsStringAsync().Result;
            if (!r.IsSuccessStatusCode) throw new InvalidOperationException("GET " + path.Split('?')[0] + " -> HTTP " + (int)r.StatusCode);
            return Json.Parse(body);
        }

        public void Dispose()
        {
            stop = true;
            try { ws?.Abort(); } catch (Exception) { }
            try { http.Dispose(); } catch (Exception) { }
        }
    }
}
