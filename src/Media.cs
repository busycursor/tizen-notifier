// Media for notes: inline data: URIs and allow-listed URLs become files in the app's data dir (deleted when the note closes).
// Video may stream from an allow-listed URL directly. Sound: raw PCM via AudioPlayback (default) or Tizen.Multimedia.Player.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace TizenNotifier
{
    internal static class Media
    {
        private static readonly HttpClient Client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        private const long MaxFile = 16 * 1024 * 1024;
        private static readonly List<object> Playing = new List<object>();   // keep players rooted while they play

        public static void Prepare(Dictionary<string, object> node, List<string> files, List<object> warn)
        {
            if (node == null) return;
            string type = (Json.S(node, "type") ?? "box").ToLowerInvariant();
            if (type == "icon" || type == "graph" || type == "svg") { SvgNode(node, warn); type = "image"; }
            if (node.ContainsKey("src") && (type == "image" || type == "gif" || type == "lottie" || type == "video" || type == "mjpeg"))
            {
                string orig = Json.S(node, "src");
                if (type == "mjpeg" && !orig.StartsWith("http")) throw new ArgumentException("mjpeg src must be an http(s) stream URL");
                node["src"] = Resolve(orig, files, type == "video" || type == "mjpeg");
                if (type == "image" && node.ContainsKey("refresh_ms"))
                {
                    if (orig.StartsWith("http")) node["__refresh_url"] = orig;
                    else { warn.Add("refresh_ms needs an http(s) src; ignored"); node.Remove("refresh_ms"); }
                }
            }
            var kids = Json.A(node, "children");
            if (kids != null) foreach (var c in kids) Prepare(c as Dictionary<string, object>, files, warn);
        }

        // icon / graph / svg node -> image node with an SVG data: URI (also used by /update when it names one of those types)
        public static void SvgNode(Dictionary<string, object> n, List<object> warn)
        {
            string type = (Json.S(n, "type") ?? "").ToLowerInvariant();
            string svg = null;
            int w = (int)Json.N(n, "w", type == "icon" ? 48 : 300), h = (int)Json.N(n, "h", type == "icon" ? 48 : 100);
            if (type == "icon")
            {
                if (Mdi.Path(Json.S(n, "icon")) == null) warn.Add("unknown icon " + Json.S(n, "icon"));
                svg = Svg.Icon(Json.S(n, "icon"), Json.S(n, "color") ?? "#ffffff");
            }
            else if (type == "graph")
            {
                var xs = new List<double>(); var ys = new List<double>();
                var pts = Json.A(n, "points"); var vals = Json.A(n, "values");
                if (pts != null) foreach (var p in pts) { if (p is List<object> pl && pl.Count >= 2 && pl[0] is double x && pl[1] is double y) { xs.Add(x); ys.Add(y); } }
                else if (vals != null) for (int i = 0; i < vals.Count; i++) if (vals[i] is double y) { xs.Add(i); ys.Add(y); }
                double? mn = Json.Has(n, "min") ? Json.N(n, "min", 0) : (double?)null, mx = Json.Has(n, "max") ? Json.N(n, "max", 0) : (double?)null;
                svg = Svg.Graph(xs, ys, w, h, Json.S(n, "color") ?? "#73bfff", Json.N(n, "width", 3), Json.N(n, "fill_opacity", 0.25), mn, mx);
            }
            else if (type == "svg") svg = Json.S(n, "svg");
            foreach (var k in new[] { "icon", "points", "values", "width", "fill_opacity", "min", "max", "svg" }) n.Remove(k);
            if (type == "icon" || type == "graph") n.Remove("color");
            n["type"] = "image"; n["fit"] = "fill";
            if (svg != null) n["src"] = Svg.DataUri(svg);
        }

        // SVGs are small and repeat a lot (icons, live graphs): one cached file per distinct content, never per note.
        private static string SvgFile(byte[] bytes)
        {
            string dir = Path.Combine(Cfg.DataDir, "svg");
            Directory.CreateDirectory(dir);
            string name;
            using (var sha = System.Security.Cryptography.SHA1.Create()) name = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            string p = Path.Combine(dir, name + ".svg");
            if (!File.Exists(p))
            {
                if (Directory.GetFiles(dir).Length > 3000) foreach (var f in Directory.GetFiles(dir)) { try { File.Delete(f); } catch (Exception) { } }
                string tmp = p + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(tmp, bytes);
                try { File.Move(tmp, p); } catch (IOException) { try { File.Delete(tmp); } catch (Exception) { } }   // another thread wrote it first
            }
            return p;
        }

        // data: URI -> file; allow-listed URL -> file (or the URL itself when streaming is fine)
        public static string Resolve(string src, List<string> files, bool streamOk)
        {
            if (string.IsNullOrEmpty(src)) throw new ArgumentException("empty src");
            if (src.StartsWith("data:image/svg+xml;base64,"))
                return SvgFile(Convert.FromBase64String(src.Substring(26)));
            if (src.StartsWith("data:"))
            {
                int comma = src.IndexOf(',');
                if (comma < 0 || !src.Substring(0, comma).Contains(";base64")) throw new ArgumentException("data: URI must be base64");
                string mime = src.Substring(5, src.IndexOf(';') - 5);
                var bytes = Convert.FromBase64String(src.Substring(comma + 1));
                if (bytes.Length > MaxFile) throw new ArgumentException("inline media too large");
                return Save(bytes, Ext(mime, null), files);
            }
            if (!Cfg.Allowed(src)) throw new ArgumentException("host not in allow list: " + SafeHost(src));
            if (streamOk) return src;
            var req = new HttpRequestMessage(HttpMethod.Get, src);
            if (Cfg.IsHa(src)) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Cfg.HaToken);
            var r = Client.SendAsync(req).Result;
            if (!r.IsSuccessStatusCode) throw new ArgumentException("fetch " + SafeHost(src) + " -> HTTP " + (int)r.StatusCode);
            if (r.Content.Headers.ContentLength > MaxFile) throw new ArgumentException("fetched media too large");
            var data = r.Content.ReadAsByteArrayAsync().Result;
            if (data.Length > MaxFile) throw new ArgumentException("fetched media too large");
            return Save(data, Ext(r.Content.Headers.ContentType?.MediaType, src), files);
        }

        // One more frame of a refreshing image: download to a new file (caller owns and deletes it).
        public static string FetchFile(string url)
        {
            var tmp = new List<string>();
            return Resolve(url, tmp, false);
        }

        private static string SafeHost(string url) { try { return new Uri(url).Host; } catch (Exception) { return "?"; } }

        private static string Save(byte[] b, string ext, List<string> files)
        {
            string p = Path.Combine(Cfg.DataDir, "media-" + Guid.NewGuid().ToString("N") + "." + ext);
            File.WriteAllBytes(p, b);
            files.Add(p);
            return p;
        }

        private static string Ext(string mime, string url)
        {
            switch ((mime ?? "").ToLowerInvariant())
            {
                case "image/png": return "png";
                case "image/jpeg": case "image/jpg": return "jpg";
                case "image/gif": return "gif";
                case "image/webp": return "webp";
                case "image/svg+xml": return "svg";
                case "application/json": return "json";
                case "audio/mpeg": case "audio/mp3": return "mp3";
                case "audio/wav": case "audio/x-wav": case "audio/wave": return "wav";
                case "audio/ogg": return "ogg";
                case "audio/flac": return "flac";
                case "video/mp4": return "mp4";
            }
            if (url != null)
            {
                try { var e = Path.GetExtension(new Uri(url).AbsolutePath).TrimStart('.'); if (e.Length > 0 && e.Length <= 5) return e.ToLowerInvariant(); } catch (Exception) { }
            }
            return "bin";
        }

        public static void Delete(List<string> files)
        {
            foreach (var f in files) { try { if (f.StartsWith(Cfg.DataDir) && File.Exists(f)) File.Delete(f); } catch (Exception) { } }
        }

        public static void PlaySound(string src, Dictionary<string, object> spec, List<object> warn)
        {
            float vol = (float)Json.N(spec, "volume", 1.0);
            string engine = (Json.S(spec, "engine") ?? "raw").ToLowerInvariant();
            try
            {
                if (engine == "player") StartPlayer(src, vol, (Json.S(spec, "stream") ?? "notification").ToLowerInvariant());
                else
                {
                    var pcm = Decode(File.ReadAllBytes(src), out int rate, out int ch);   // read now: the file goes when the note closes
                    Task.Run(() => RawPlay(pcm, rate, ch, vol));
                }
            }
            catch (Exception e) { warn.Add("sound: " + e.GetType().Name + " " + e.Message); Log.W("sound failed: " + e); }
        }

        // ---- raw engine: decode to 16-bit PCM and write it to AudioPlayback (no media-player resource involved) ----
        private static readonly object RawLock = new object();

        private static short[] Decode(byte[] b, out int rate, out int ch)
        {
            if (b.Length > 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F') return Wav(b, out rate, out ch);
            return Mp3(b, out rate, out ch);
        }

        private static short[] Wav(byte[] b, out int rate, out int ch)
        {
            int i = 12, fmt = 0, bits = 0; rate = 0; ch = 0;
            while (i + 8 <= b.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(b, i, 4);
                int len = BitConverter.ToInt32(b, i + 4);
                if (id == "fmt ") { fmt = BitConverter.ToInt16(b, i + 8); ch = BitConverter.ToInt16(b, i + 10); rate = BitConverter.ToInt32(b, i + 12); bits = BitConverter.ToInt16(b, i + 22); }
                else if (id == "data")
                {
                    if (fmt != 1 || (bits != 16 && bits != 8)) throw new NotSupportedException("WAV must be PCM 8/16-bit (got format " + fmt + ", " + bits + " bit)");
                    len = Math.Min(len, b.Length - i - 8);
                    if (bits == 16) { var o = new short[len / 2]; Buffer.BlockCopy(b, i + 8, o, 0, o.Length * 2); return o; }
                    var o8 = new short[len]; for (int k = 0; k < len; k++) o8[k] = (short)((b[i + 8 + k] - 128) << 8); return o8;
                }
                i += 8 + len + (len & 1);
            }
            throw new FormatException("WAV has no data chunk");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static short[] Mp3(byte[] b, out int rate, out int ch)
        {
            using (var ms = new MemoryStream(b))
            using (var f = new NLayer.MpegFile(ms))
            {
                rate = f.SampleRate; ch = f.Channels;
                var outp = new List<short>(b.Length * 12);
                var buf = new float[4096];
                int n;
                while ((n = f.ReadSamples(buf, 0, buf.Length)) > 0)
                    for (int k = 0; k < n; k++) outp.Add((short)Math.Max(-32768, Math.Min(32767, buf[k] * 32767f)));
                return outp.ToArray();
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RawPlay(short[] pcm, int rate, int ch, float vol)
        {
            lock (RawLock)   // one sound at a time
            {
                try
                {
                    if (ch > 2) throw new NotSupportedException(ch + " channels");
                    if (rate < 8000 || rate > 48000) throw new NotSupportedException("sample rate " + rate);
                    vol = Math.Max(0f, Math.Min(1f, vol));
                    var bytes = new byte[pcm.Length * 2];
                    for (int k = 0; k < pcm.Length; k++) { short v = (short)(pcm[k] * vol); bytes[2 * k] = (byte)v; bytes[2 * k + 1] = (byte)(v >> 8); }
                    var started = DateTime.Now;
                    using (var a = new Tizen.Multimedia.AudioPlayback(rate, ch == 1 ? Tizen.Multimedia.AudioChannel.Mono : Tizen.Multimedia.AudioChannel.Stereo, Tizen.Multimedia.AudioSampleType.S16Le))
                    {
                        a.Prepare();
                        int chunk = Math.Max(1024, a.GetBufferSize());
                        for (int off = 0; off < bytes.Length; off += chunk)
                        {
                            int len = Math.Min(chunk, bytes.Length - off);
                            var part = new byte[len]; Buffer.BlockCopy(bytes, off, part, 0, len);
                            a.Write(part);
                        }
                        a.Drain();
                        a.Unprepare();
                    }
                    Log.W("sound done (raw, " + rate + " Hz, " + ch + " ch, " + (pcm.Length / ch / (double)rate).ToString("0.0") + " s audio, " + (DateTime.Now - started).TotalSeconds.ToString("0.0") + " s wall)");
                }
                catch (Exception e) { Log.W("raw sound failed: " + e.GetType().Name + " " + e.Message); }
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void StartPlayer(string src, float volume, string stream)
        {
            var p = new Tizen.Multimedia.Player();
            var type = stream == "media" ? Tizen.Multimedia.AudioStreamType.Media : stream == "alarm" ? Tizen.Multimedia.AudioStreamType.Alarm
                : stream == "system" ? Tizen.Multimedia.AudioStreamType.System : Tizen.Multimedia.AudioStreamType.Notification;
            var policy = new Tizen.Multimedia.AudioStreamPolicy(type);
            p.SetSource(new Tizen.Multimedia.MediaUriSource(src));
            try { p.ApplyAudioStreamPolicy(policy); }
            catch (NotSupportedException) { Log.W("sound: no stream policy support on this TV; playing as plain audio"); }
            p.Volume = Math.Max(0f, Math.Min(1f, volume));
            lock (Playing) { Playing.Add(p); Playing.Add(policy); }
            void Done()
            {
                lock (Playing) { Playing.Remove(p); Playing.Remove(policy); }
                try { p.Dispose(); } catch (Exception) { }
                try { policy.Dispose(); } catch (Exception) { }
            }
            p.PlaybackCompleted += (s, e) => Task.Run(Done);
            p.ErrorOccurred += (s, e) => { Log.W("sound error: " + e.Error); Task.Run(Done); };
            Task.Run(async () =>
            {
                try { await p.PrepareAsync(); p.Start(); Log.W("sound playing (" + stream + ")"); }
                catch (Exception e) { Log.W("sound failed: " + e.GetType().Name + " " + e.Message); Done(); }
            });
        }
    }
}
