// Small HTTP/1.1 server (one request per connection). All endpoints except GET /health need "Authorization: Bearer <token>".
//   GET  /health                 {ok, version}
//   GET  /info                   version, runtime, uptime, screen, capabilities (last selftest), active ids, allow list
//   GET  /log?n=100              recent log lines
//   POST /selftest               creates and disposes each optional feature; nothing is shown
//   POST /notify   {..}          show a message (same id = replace in place)
//   POST /update   {id, nodes:{name:{props}}, duration?}
//   POST /dismiss  {id} | {all:true}
//   POST /config   {allow:[hosts]}   persist the fetch/callback allow list
//   POST /host     {actions:[..]}    Tizen 5 build: report/drive the shared host window (diagnostics)
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace TizenNotifier
{
    internal static class Http
    {
        private const int MaxBody = 24 * 1024 * 1024;

        public static void Serve(int port)
        {
            while (true)
            {
                TcpListener l = null;
                try
                {
                    l = new TcpListener(IPAddress.Any, port);
                    l.Start();
                    Log.W("listening on " + port);
                    while (true)
                    {
                        var c = l.AcceptTcpClient();
                        ThreadPool.QueueUserWorkItem(_ => Handle(c));
                    }
                }
                catch (Exception e)
                {
                    Log.W("listener failed: " + e.Message + " (retry in 5 s)");
                    try { l?.Stop(); } catch (Exception) { }
                    Thread.Sleep(5000);
                }
            }
        }

        private static void Handle(TcpClient c)
        {
            using (c)
            {
                try
                {
                    c.ReceiveTimeout = 20000; c.SendTimeout = 20000;
                    var st = c.GetStream();
                    string head = ReadHead(st, out byte[] rest);
                    if (head == null) return;
                    var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
                    var first = lines[0].Split(' ');
                    if (first.Length < 2) { Send(st, 400, Err("bad request")); return; }
                    string method = first[0], target = first[1];
                    var hdr = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 1; i < lines.Length; i++) { int k = lines[i].IndexOf(':'); if (k > 0) hdr[lines[i].Substring(0, k).Trim()] = lines[i].Substring(k + 1).Trim(); }
                    int len = hdr.TryGetValue("Content-Length", out var cl) && int.TryParse(cl, out var n) ? n : 0;
                    if (len > MaxBody) { Send(st, 413, Err("body too large")); return; }
                    var body = new byte[len];
                    int got = Math.Min(rest.Length, len);
                    Array.Copy(rest, body, got);
                    while (got < len) { int r = st.Read(body, got, len - got); if (r <= 0) break; got += r; }
                    string path = target, query = "";
                    int q = target.IndexOf('?'); if (q >= 0) { path = target.Substring(0, q); query = target.Substring(q + 1); }

                    if (method == "GET" && path == "/health") { Send(st, 200, new Dictionary<string, object> { ["ok"] = true, ["version"] = Cfg.Version, ["build"] = BuildTime() }); return; }
                    if (!Authorized(hdr)) { Send(st, 401, Err("unauthorized")); return; }

                    object req = null;
                    if (len > 0)
                    {
                        try { req = Json.Parse(Encoding.UTF8.GetString(body, 0, got)); }
                        catch (Exception e) { Send(st, 400, Err("bad json: " + e.Message)); return; }
                    }
                    var (code, res) = Route(method, path, query, req);
                    Send(st, code, res);
                }
                catch (Exception e) { Log.W("request failed: " + e.Message); }
            }
        }

        private static (int, object) Route(string method, string path, string query, object req)
        {
            try
            {
                switch (method + " " + path)
                {
                    case "GET /info": return (200, Notes.Info());
                    case "GET /log":
                        int n = 100;
                        foreach (var p in query.Split('&')) if (p.StartsWith("n=") && int.TryParse(p.Substring(2), out var x)) n = Math.Max(1, Math.Min(300, x));
                        return (200, new Dictionary<string, object> { ["log"] = Log.Tail(n) });
                    case "POST /selftest": return (200, SelfTest.Run());
                    case "POST /host": return (200, App.OnMain(() => Surface.Host(Json.A(req, "actions"))));
                    case "POST /notify": return Notes.Notify(req);
                    case "POST /update": return Notes.Update(req);
                    case "POST /dismiss": return Notes.Dismiss(req);
                    case "POST /config":
                        var allow = new List<string>();
                        var a = Json.A(req, "allow");
                        if (a == null) return (400, Err("allow list missing"));
                        foreach (var h in a) if (h is string s) allow.Add(s);
                        Cfg.SaveAllow(allow);
                        Log.W("allow list set: " + string.Join(",", allow));
                        return (200, new Dictionary<string, object> { ["ok"] = true, ["allow"] = allow });
                }
                return (404, Err("no such endpoint"));
            }
            catch (Exception e)
            {
                Log.W(path + " failed: " + e);
                return (500, Err(e.GetType().Name + ": " + e.Message));
            }
        }

        private static string BuildTime()
        {
            try { return File.GetLastWriteTimeUtc(typeof(Http).Assembly.Location).ToString("yyyy-MM-dd HH:mm") + "Z"; } catch (Exception) { return null; }
        }

        public static Dictionary<string, object> Err(string m) => new Dictionary<string, object> { ["ok"] = false, ["error"] = m };

        private static bool Authorized(Dictionary<string, string> hdr)
        {
            if (string.IsNullOrEmpty(Cfg.Token) || !hdr.TryGetValue("Authorization", out var a) || !a.StartsWith("Bearer ")) return false;
            var x = Encoding.UTF8.GetBytes(a.Substring(7).Trim());
            var y = Encoding.UTF8.GetBytes(Cfg.Token);
            if (x.Length != y.Length) return false;
            int d = 0; for (int i = 0; i < x.Length; i++) d |= x[i] ^ y[i];
            return d == 0;
        }

        private static string ReadHead(Stream st, out byte[] rest)
        {
            var buf = new MemoryStream();
            var b = new byte[8192];
            rest = new byte[0];
            while (buf.Length < 65536)
            {
                int r = st.Read(b, 0, b.Length);
                if (r <= 0) return null;
                buf.Write(b, 0, r);
                var all = buf.ToArray();
                for (int i = 3; i < all.Length; i++)
                {
                    if (all[i - 3] == '\r' && all[i - 2] == '\n' && all[i - 1] == '\r' && all[i] == '\n')
                    {
                        rest = new byte[all.Length - i - 1];
                        Array.Copy(all, i + 1, rest, 0, rest.Length);
                        return Encoding.ASCII.GetString(all, 0, i - 3);
                    }
                }
            }
            return null;
        }

        private static void Send(Stream st, int code, object body)
        {
            var b = Encoding.UTF8.GetBytes(Json.Write(body));
            string reason = code == 200 ? "OK" : code == 401 ? "Unauthorized" : code == 404 ? "Not Found" : code == 400 ? "Bad Request" : "Error";
            var h = Encoding.ASCII.GetBytes("HTTP/1.1 " + code + " " + reason + "\r\nContent-Type: application/json\r\nContent-Length: " + b.Length + "\r\nConnection: close\r\n\r\n");
            st.Write(h, 0, h.Length); st.Write(b, 0, b.Length); st.Flush();
        }
    }
}
