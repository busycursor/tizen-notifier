// mjpeg node: video without the TV's video decoder, so it never takes the decoder from the app or Art Mode underneath.
// Frames are JPEGs decoded by NUI's native image loader into two layers that swap when the next frame is ready.
//
// Two stream kinds (multipart/x-mixed-replace):
//   plain MJPEG (e.g. go2rtc /api/stream.mjpeg): every frame is shown as it arrives; frames arriving while one is
//     still loading are dropped, so the picture never lags.
//   synced A/V: parts carry X-Timestamp (ms); audio parts are audio/L16;rate=R;channels=C (16-bit PCM, little-endian).
//     The audio goes to this node's own audio output as it arrives and is the clock: play position = audio written
//     minus what is still buffered in the device. Frames start loading just before they are due (by the measured
//     load time); late frames are dropped and early ones wait, so if audio stalls the picture waits with it.
//     Closing the message stops its audio at once.
//   {"type": "mjpeg", "src": "http://relay:8791/clip.av", "w": 1280, "h": 534, "volume": 1.0, "stats": true}
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using Path = System.IO.Path;

namespace TizenNotifier
{
    internal static class Mjpeg
    {
        private static readonly HttpClient Client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        private static string dir;
        private static long fileSeq;   // frame files are named uniquely across all streams and never reused (the image
                                       // loader caches by path: a reused name would show another stream's frame)

        // Frames go to RAM (tmpfs) when the app may write there, else to the data dir.
        private static string Dir()
        {
            if (dir != null) return dir;
            foreach (var d in new[] { "/tmp", Cfg.DataDir })
            {
                try
                {
                    if (string.IsNullOrEmpty(d) || !Directory.Exists(d)) continue;
                    var p = Path.Combine(d, "tiznotify-mjpeg");
                    Directory.CreateDirectory(p);
                    File.WriteAllBytes(Path.Combine(p, "probe"), new byte[1]);
                    File.Delete(Path.Combine(p, "probe"));
                    foreach (var f in Directory.GetFiles(p)) { try { File.Delete(f); } catch (Exception) { } }
                    Log.W("mjpeg frames in " + p);
                    return dir = p;
                }
                catch (Exception) { }
            }
            return dir = Cfg.DataDir;
        }

        private sealed class Stopper : IDisposable
        {
            public readonly CancellationTokenSource Cts = new CancellationTokenSource();
            public Action OnStop;
            public void Dispose() { try { Cts.Cancel(); } catch (Exception) { } try { OnStop?.Invoke(); } catch (Exception) { } }
        }

        private sealed class Part { public string Type; public long Ts = -1; public byte[] Data; }

        // Everything one node needs; fields shared by the network, audio, scheduler and main threads are guarded by L.
        private sealed class Player
        {
            public readonly object L = new object();
            public Note N; public ImageView[] Layers; public string Folder, Id; public bool Stats; public float Volume = 1f;
            public string[] Files = new string[2];
            public int Cur, Pending = -1;
            public DateTime PendSince;
            public long PendTs;
            public long Seq;
            // stats (reset every 5 s)
            public long Got, Shown, Dropped, Bytes, Late; public double LoadMs, OffSum, OffAbsMax; public DateTime T0 = DateTime.UtcNow;
            public double LoadAvg = 15;   // running estimate of JPEG load time (ms)
            // A/V
            public readonly Queue<Part> Frames = new Queue<Part>();
            public readonly Queue<byte[]> Audio = new Queue<byte[]>();
            public bool HasAudio; public int Rate, Channels; public double BytesPerMs;
            public long AudioStartTs = -1;     // timestamp of the first audio chunk
            public long WrittenBytes;          // PCM written to the device
            public double DeviceBufferMs;      // what the device holds before it plays
            public DateTime AudioStarted, LastWrite;
            public double LastChunkMs;
            public long Underruns;
            public bool NetDone, AudioDone;
            public double TrimMs;              // manual A/V trim: + shows video later relative to the sound

            // Current media time (ms) from the audio clock, or -1 before audio starts.
            public double Clock()
            {
                if (AudioStartTs < 0 || BytesPerMs <= 0) return -1;
                double written = WrittenBytes / BytesPerMs;
                var now = DateTime.UtcNow;
                double sinceStart = (now - AudioStarted).TotalMilliseconds;
                // the device plays continuously between writes: advance smoothly from the last write, at most one chunk
                double since = Math.Min((now - LastWrite).TotalMilliseconds, LastChunkMs);
                // until the device buffer has filled, playback is behind the write position by however much is queued
                return AudioStartTs + Math.Max(0, Math.Min(written - DeviceBufferMs + since, sinceStart)) - TrimMs;
            }
        }

        public static View Create(Note n, Dictionary<string, object> spec, int pw, int ph, bool parentLayout, List<object> warn)
        {
            var box = new Dictionary<string, object>(spec);
            box["type"] = "box"; box.Remove("src"); box.Remove("fit"); box.Remove("stats"); box.Remove("volume"); box.Remove("sync_trim_ms");
            var c = Build.Node(n, box, pw, ph, parentLayout, warn);
            int w = (int)c.SizeWidth, h = (int)c.SizeHeight;
            var p = new Player
            {
                N = n, Stats = Json.B(spec, "stats", false), Volume = (float)Math.Max(0, Math.Min(1, Json.N(spec, "volume", 1.0))), Id = n.Id.Replace(':', '_'), TrimMs = Json.N(spec, "sync_trim_ms", 0),
                Layers = new[] { new ImageView { Size = Build.Sz(w, h), Position = Build.Ps(0, 0) }, new ImageView { Size = Build.Sz(w, h), Position = Build.Ps(0, 0) } },
            };
            c.Add(p.Layers[0]); c.Add(p.Layers[1]); p.Layers[1].Hide();
            for (int i = 0; i < 2; i++)
            {
                int idx = i;
                p.Layers[i].ResourceReady += (s, e) => Ready(p, idx);
            }
            var stop = new Stopper();
            n.Keep.Add(stop);
            string url = Json.S(spec, "src");
            new Thread(() => Net(p, url, stop)) { IsBackground = true, Name = "mjpeg-net" }.Start();
            return c;
        }

        // main thread: a layer finished loading its frame
        private static void Ready(Player p, int idx)
        {
            if (idx != p.Pending || p.N.Closing) return;
            p.Layers[idx].Show(); p.Layers[1 - idx].Hide();
            var old = p.Files[1 - idx]; p.Files[1 - idx] = null;
            if (old != null) { try { File.Delete(old); } catch (Exception) { } }
            double load = (DateTime.UtcNow - p.PendSince).TotalMilliseconds;
            lock (p.L)
            {
                p.Cur = idx; p.Pending = -1; p.Shown++; p.LoadMs += load;
                p.LoadAvg = p.LoadAvg * 0.9 + load * 0.1;
                if (p.HasAudio && p.PendTs >= 0)
                {
                    double clock = p.Clock();
                    if (clock >= 0) { double off = clock - p.PendTs; p.OffSum += off; p.OffAbsMax = Math.Max(p.OffAbsMax, Math.Abs(off)); }
                }
            }
        }

        // any thread: start loading a frame into the hidden layer
        private static void Load(Player p, byte[] jpg, long ts)
        {
            string f = Path.Combine(p.Folder, "f" + Interlocked.Increment(ref fileSeq) + "-" + DateTime.UtcNow.Ticks.ToString("x") + ".jpg");
            File.WriteAllBytes(f, jpg);
            App.Post(() =>
            {
                if (p.N.Closing || p.Pending >= 0) { try { File.Delete(f); } catch (Exception) { } return; }
                int next = 1 - p.Cur;
                p.Files[next] = f; p.Pending = next; p.PendSince = DateTime.UtcNow; p.PendTs = ts;
                p.Layers[next].ResourceUrl = f;
            });
        }

        private static void Net(Player p, string url, Stopper stop)
        {
            p.Folder = Dir();
            Thread audio = null, sched = null;
            while (!p.N.Closing && !stop.Cts.IsCancellationRequested)
            {
                try
                {
                    var req = new HttpRequestMessage(HttpMethod.Get, url);
                    if (Cfg.IsHa(url)) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Cfg.HaToken);
                    using (var r = Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, stop.Cts.Token).Result)
                    using (var st = r.Content.ReadAsStreamAsync().Result)
                    {
                        if (!r.IsSuccessStatusCode) throw new IOException("HTTP " + (int)r.StatusCode);
                        var rd = new PartReader(st);
                        Part part;
                        while (!p.N.Closing && (part = rd.Next()) != null)
                        {
                            bool isAudio = part.Type != null && part.Type.StartsWith("audio/L16", StringComparison.OrdinalIgnoreCase);
                            if (isAudio)
                            {
                                if (!p.HasAudio)
                                {
                                    p.Rate = Param(part.Type, "rate", 48000); p.Channels = Param(part.Type, "channels", 2);
                                    p.BytesPerMs = p.Rate * p.Channels * 2 / 1000.0;
                                    p.HasAudio = true;
                                    audio = new Thread(() => AudioLoop(p, stop)) { IsBackground = true, Name = "mjpeg-audio" }; audio.Start();
                                    sched = new Thread(() => Scheduler(p, stop)) { IsBackground = true, Name = "mjpeg-sched" }; sched.Start();
                                }
                                lock (p.L)
                                {
                                    if (p.AudioStartTs < 0 && part.Ts >= 0) p.AudioStartTs = part.Ts;
                                    p.Audio.Enqueue(part.Data);
                                    Monitor.PulseAll(p.L);
                                }
                                continue;
                            }
                            lock (p.L) { p.Got++; p.Bytes += part.Data.Length; }
                            if (p.HasAudio && part.Ts >= 0)
                            {
                                lock (p.L)
                                {
                                    p.Frames.Enqueue(part);
                                    while (p.Frames.Count > 120) { p.Frames.Dequeue(); p.Dropped++; }   // ~5 s of video: the audio has stalled
                                    Monitor.PulseAll(p.L);
                                }
                            }
                            else
                            {
                                // plain MJPEG: show as it comes, drop while busy
                                if (p.Pending >= 0) { lock (p.L) p.Dropped++; }
                                else Load(p, part.Data, -1);
                            }
                            StatsTick(p);
                        }
                    }
                    if (p.HasAudio) break;   // a synced stream that ended is finished (no reconnect mid-programme)
                }
                catch (Exception e)
                {
                    if (p.N.Closing || stop.Cts.IsCancellationRequested) break;
                    Log.W("mjpeg " + p.N.Id + ": " + (e.InnerException ?? e).Message + " (retrying)");
                    for (int i = 0; i < 20 && !p.N.Closing; i++) Thread.Sleep(100);
                }
            }
            lock (p.L) { p.NetDone = true; Monitor.PulseAll(p.L); }
            App.Post(() => { foreach (var f in p.Files) if (f != null) { try { File.Delete(f); } catch (Exception) { } } });
        }

        private static int Param(string type, string key, int def)
        {
            foreach (var kv in type.Split(';'))
            {
                var t = kv.Trim();
                if (t.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase) && int.TryParse(t.Substring(key.Length + 1), out var v)) return v;
            }
            return def;
        }

        // Writes PCM to this node's own output as it arrives. Write blocks when the device buffer is full, which paces it.
        private static void AudioLoop(Player p, Stopper stop)
        {
            Tizen.Multimedia.AudioPlayback a = null;
            try
            {
                a = new Tizen.Multimedia.AudioPlayback(p.Rate, p.Channels == 1 ? Tizen.Multimedia.AudioChannel.Mono : Tizen.Multimedia.AudioChannel.Stereo, Tizen.Multimedia.AudioSampleType.S16Le);
                a.Prepare();
                var dev = a;
                stop.OnStop = () => { try { dev.Pause(); } catch (Exception) { } };
                lock (p.L) p.DeviceBufferMs = Math.Max(20, a.GetBufferSize() / p.BytesPerMs) * 2;   // device buffer + the one being played
                bool started = false;
                while (!stop.Cts.IsCancellationRequested && !p.N.Closing)
                {
                    byte[] chunk;
                    lock (p.L)
                    {
                        while (p.Audio.Count == 0 && !p.NetDone && !stop.Cts.IsCancellationRequested && !p.N.Closing)
                        {
                            if (started) p.Underruns++;
                            Monitor.Wait(p.L, 200);
                        }
                        if (p.Audio.Count == 0) break;
                        chunk = p.Audio.Dequeue();
                        if (!started) { started = true; p.AudioStarted = DateTime.UtcNow; }
                    }
                    if (p.Volume < 0.999f)
                        for (int i = 0; i + 1 < chunk.Length; i += 2)
                        {
                            short s = (short)(chunk[i] | chunk[i + 1] << 8);
                            s = (short)(s * p.Volume);
                            chunk[i] = (byte)s; chunk[i + 1] = (byte)(s >> 8);
                        }
                    a.Write(chunk);
                    lock (p.L) { p.WrittenBytes += chunk.Length; p.LastWrite = DateTime.UtcNow; p.LastChunkMs = chunk.Length / p.BytesPerMs; Monitor.PulseAll(p.L); }
                }
                if (!stop.Cts.IsCancellationRequested && !p.N.Closing) { try { a.Drain(); } catch (Exception) { } }
            }
            catch (Exception e) { Log.W("mjpeg " + p.N.Id + " audio: " + e.GetType().Name + " " + e.Message); }
            finally
            {
                try { a?.Unprepare(); } catch (Exception) { }
                try { a?.Dispose(); } catch (Exception) { }
                lock (p.L) { p.AudioDone = true; Monitor.PulseAll(p.L); }
            }
        }

        // Shows each frame when the audio clock reaches it (starting the load early by the measured load time).
        private static void Scheduler(Player p, Stopper stop)
        {
            while (!stop.Cts.IsCancellationRequested && !p.N.Closing)
            {
                Part show = null;
                lock (p.L)
                {
                    if (p.AudioDone && p.Frames.Count == 0) break;
                    double clock = p.Clock();
                    if (clock < 0 || p.Frames.Count == 0) { Monitor.Wait(p.L, 20); continue; }
                    double lead = p.LoadAvg + 5;
                    // drop frames whose time has passed (keep the newest one that is due)
                    while (p.Frames.Count > 1)
                    {
                        var next = p.Frames.ToArray()[1];
                        if (next.Ts <= clock + lead) { p.Frames.Dequeue(); p.Dropped++; p.Late++; }
                        else break;
                    }
                    var head = p.Frames.Peek();
                    if (head.Ts <= clock + lead && p.Pending < 0) show = p.Frames.Dequeue();
                    else if (p.AudioDone) { p.Frames.Dequeue(); continue; }
                }
                if (show != null) { Load(p, show.Data, show.Ts); StatsTick(p); }
                else Thread.Sleep(4);
            }
        }

        private static void StatsTick(Player p)
        {
            if (!p.Stats) return;
            string line = null;
            lock (p.L)
            {
                double secs = (DateTime.UtcNow - p.T0).TotalSeconds;
                if (secs < 5) return;
                line = "mjpeg " + p.N.Id + ": received " + (p.Got / secs).ToString("0.0") + " fps, shown " + (p.Shown / secs).ToString("0.0") + " fps, dropped " + p.Dropped +
                       ", " + (p.Bytes / secs / 1024).ToString("0") + " KB/s, load " + (p.Shown > 0 ? (p.LoadMs / p.Shown).ToString("0") : "-") + " ms/frame";
                if (p.HasAudio)
                    line += ", a/v offset avg " + (p.Shown > 0 ? (p.OffSum / p.Shown).ToString("0", CultureInfo.InvariantCulture) : "-") + " ms max " + p.OffAbsMax.ToString("0") +
                            " ms, late " + p.Late + ", audio queue " + p.Audio.Count + ", underruns " + p.Underruns + ", device buffer " + p.DeviceBufferMs.ToString("0") + " ms";
                p.T0 = DateTime.UtcNow; p.Got = p.Shown = p.Dropped = p.Bytes = p.Late = 0; p.LoadMs = p.OffSum = p.OffAbsMax = 0;
            }
            Log.W(line);
        }

        // multipart/x-mixed-replace reader: part headers (Content-Type, Content-Length, X-Timestamp), then the body
        // (Content-Length bytes, or for a JPEG without one, up to the end marker).
        private sealed class PartReader
        {
            private readonly Stream s;
            private readonly byte[] buf = new byte[512 * 1024];
            private int pos, len;
            public PartReader(Stream s) { this.s = s; }

            private bool Fill()
            {
                if (pos > 0 && pos < len) { Buffer.BlockCopy(buf, pos, buf, 0, len - pos); len -= pos; pos = 0; }
                else if (pos >= len) { pos = len = 0; }
                if (len == buf.Length) return false;
                int r = s.Read(buf, len, buf.Length - len);
                if (r <= 0) return false;
                len += r;
                return true;
            }

            private string Line()
            {
                while (true)
                {
                    for (int i = pos; i + 1 < len; i++)
                        if (buf[i] == '\r' && buf[i + 1] == '\n') { var l = Encoding.ASCII.GetString(buf, pos, i - pos); pos = i + 2; return l; }
                    if (!Fill()) return null;
                }
            }

            public Part Next()
            {
                var part = new Part();
                int clen = -1;
                bool inHeaders = false;
                while (true)
                {
                    var l = Line();
                    if (l == null) return null;
                    int colon = l.IndexOf(':');
                    if (colon > 0)
                    {
                        inHeaders = true;
                        string k = l.Substring(0, colon).Trim(), v = l.Substring(colon + 1).Trim();
                        if (k.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) int.TryParse(v, out clen);
                        else if (k.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) part.Type = v;
                        else if (k.Equals("X-Timestamp", StringComparison.OrdinalIgnoreCase) && long.TryParse(v, out var ts)) part.Ts = ts;
                    }
                    else if (l.Length == 0 && inHeaders) break;
                    else if (l.Length == 0 && pos < len && buf[pos] == 0xFF) break;
                }
                if (clen >= 0)
                {
                    var o = new byte[clen]; int got = 0;
                    while (got < clen)
                    {
                        if (pos >= len && !Fill()) return null;
                        int k = Math.Min(clen - got, len - pos);
                        Buffer.BlockCopy(buf, pos, o, got, k); pos += k; got += k;
                    }
                    part.Data = o;
                    return part;
                }
                var ms = new MemoryStream();
                while (true)
                {
                    for (int i = pos; i + 1 < len; i++)
                        if (buf[i] == 0xFF && buf[i + 1] == 0xD9) { ms.Write(buf, pos, i + 2 - pos); pos = i + 2; part.Data = ms.ToArray(); return part; }
                    if (len - pos > 1) { ms.Write(buf, pos, len - pos - 1); pos = len - 1; }
                    if (!Fill()) return null;
                }
            }
        }
    }
}
