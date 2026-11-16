// dash-host: render a Home Assistant dashboard the way the TV would, without a TV.
//   dotnet run -- <ha-url> <token-file> <dashboard> [--view N|path] [--scale S] [--as-user ID] [--wait SECONDS] [--out tree.json] [--theme JSON]
// Writes the node tree (the notifier's /notify schema) after the data settles. tools/preview.py draws it as a PNG.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace TizenNotifier
{
    internal sealed class FileHost : IDashHost
    {
        public Dictionary<string, object> Tree;
        public DateTime LastChange = DateTime.UtcNow;
        public int Shows, Updates;
        public string VideoUrl(string src, int widthPx) => null;   // no relay on the desktop: snapshots
        public void Log(string msg) => Console.Error.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff ") + msg);
        public void Show(DashSession s, Dictionary<string, object> tree) { lock (this) { Tree = tree; Shows++; LastChange = DateTime.UtcNow; } }
        public void Patch(DashSession s, Dictionary<string, object> ops) { lock (this) { Updates++; LastChange = DateTime.UtcNow; Tree = s.Tree; } }
        public void Update(DashSession s, Dictionary<string, Dictionary<string, object>> changes)
        {
            lock (this)
            {
                Updates++; LastChange = DateTime.UtcNow;
                Tree = s.Tree;   // the session keeps the full latest tree
            }
        }
    }

    internal static class Program
    {
        private static int Main(string[] a)
        {
            if (a.Length < 3) { Console.Error.WriteLine("usage: <ha-url> <token-file> <dashboard> [--view V] [--scale S] [--as-user ID] [--wait S] [--out F] [--theme JSON]"); return 2; }
            var req = new Dictionary<string, object>();
            if (a[2] != "-") req["dashboard"] = a[2];
            string outFile = "tree.json"; double wait = 8;
            for (int i = 3; i + 1 < a.Length; i += 2)
                switch (a[i])
                {
                    case "--view": req["view"] = a[i + 1]; break;
                    case "--scale": req["scale"] = double.Parse(a[i + 1], System.Globalization.CultureInfo.InvariantCulture); break;
                    case "--as-user": req["as_user"] = a[i + 1]; break;
                    case "--wait": wait = double.Parse(a[i + 1], System.Globalization.CultureInfo.InvariantCulture); break;
                    case "--out": outFile = a[i + 1]; break;
                    case "--theme": req["theme"] = Json.Parse(a[i + 1]); break;
                    case "--config": req["config"] = Json.Parse(File.ReadAllText(a[i + 1])); break;
                }
            Mdi.File = Path.Combine(AppContext.BaseDirectory, "mdi.tsv.gz");
            if (!File.Exists(Mdi.File)) Mdi.File = Path.GetFullPath("../src/res/mdi.tsv.gz");
            var host = new FileHost();
            var ha = new HaClient(a[0], File.ReadAllText(a[1]).Trim(), host.Log);
            var s = new DashSession(DashOptions.From(req), ha, host);
            s.Start();
            // wait until nothing has changed for 2 s (templates, history, forecasts arrive after the first render), or the limit
            var start = DateTime.UtcNow;
            while ((DateTime.UtcNow - start).TotalSeconds < wait)
            {
                Thread.Sleep(250);
                lock (host) if (host.Tree != null && (DateTime.UtcNow - host.LastChange).TotalSeconds > 2.5) break;
            }
            var st = s.StatusInfo();
            Console.Error.WriteLine("status: " + Json.Write(st));
            lock (host)
            {
                if (host.Tree == null) { Console.Error.WriteLine("no tree rendered"); return 1; }
                File.WriteAllText(outFile, Json.Write(host.Tree));
                Console.Error.WriteLine("wrote " + outFile + " (" + host.Shows + " shows, " + host.Updates + " updates)");
            }
            s.Dispose(); ha.Dispose();
            return 0;
        }
    }
}
