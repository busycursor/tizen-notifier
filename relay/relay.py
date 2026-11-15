#!/usr/bin/env python3
"""Tizen Notifier video relay: turns a video source into the synced A/V stream the notifier's `mjpeg` node plays
without the TV's hardware decoder.

    GET /health                                  {"ok", "version", "hw"}  (no auth)
    GET /av?src=<source>[&w=1280][&start=s][&fps=N][&q=80][&audio=1]

/av answers multipart/x-mixed-replace. Every part has X-Timestamp (ms): image/jpeg frames, and audio/L16 parts
(16-bit little-endian PCM, 32 kHz stereo, +7 dB with a limiter: program audio at about -16 LUFS) unless audio=0 or the source has none. One ffmpeg
process decodes, scales and JPEG-encodes on the GPU (VA-API) when available, and on the CPU otherwise. Files are read
at real time with a short initial burst, so the TV holds about half a second of buffer.

Sources: files under RELAY_MEDIA (path relative to it, or absolute inside it), and http(s) / rtsp URLs whose host is in
RELAY_ALLOW_HOSTS. Nothing else, so the relay can't be used to fetch arbitrary URLs.

Settings (environment): RELAY_PORT (8792), RELAY_MEDIA (/media), RELAY_ALLOW_HOSTS (comma-separated), RELAY_TOKEN
(optional; then /av needs "Authorization: Bearer <token>"), RELAY_HW (auto|vaapi|cpu), RELAY_DEVICE (/dev/dri/renderD128).
"""
import collections, json, os, queue, subprocess, sys, threading, time, urllib.parse
from http.server import ThreadingHTTPServer, BaseHTTPRequestHandler

VERSION = "1.0.0"
PORT = int(os.environ.get("RELAY_PORT", "8792"))
MEDIA = os.path.realpath(os.environ.get("RELAY_MEDIA", "/media"))
HOSTS = {h.strip().lower() for h in os.environ.get("RELAY_ALLOW_HOSTS", "").split(",") if h.strip()}
TOKEN = os.environ.get("RELAY_TOKEN", "")
DEVICE = os.environ.get("RELAY_DEVICE", "/dev/dri/renderD128")
HW = os.environ.get("RELAY_HW", "auto")
if HW == "auto":
    HW = "vaapi" if os.path.exists(DEVICE) else "cpu"
RATE, CH = 32000, 2
# Program audio is mastered around -23 LUFS; +7 dB brings it to the -16 LUFS the notifier's volume 1.0 is matched to.
# A fixed gain plus a 5 ms limiter, because loudness normalization (loudnorm) holds 3 s of audio and would delay it.
GAIN_DB = float(os.environ.get("RELAY_GAIN_DB", "7"))
CHUNK = RATE * CH * 2 // 20          # 50 ms of audio per part


def log(msg):
    sys.stderr.write(time.strftime("%Y-%m-%d %H:%M:%S ") + msg + "\n")
    sys.stderr.flush()


def resolve(src):
    """-> (ffmpeg input, is_file) or raises ValueError."""
    if not src:
        raise ValueError("src missing")
    u = urllib.parse.urlparse(src)
    if u.scheme in ("http", "https", "rtsp", "rtsps"):
        host = (u.hostname or "").lower()
        if host not in HOSTS and "%s:%s" % (host, u.port) not in HOSTS:
            raise ValueError("source host not allowed: " + host)
        return src, False
    if u.scheme not in ("", "file"):
        raise ValueError("unsupported source scheme: " + u.scheme)
    p = u.path if u.scheme == "file" else src
    full = os.path.realpath(p if os.path.isabs(p) else os.path.join(MEDIA, p))
    if not (full == MEDIA or full.startswith(MEDIA + os.sep)) or not os.path.isfile(full):
        raise ValueError("no such media file")
    return full, True


def probe_fps(inp):
    try:
        out = subprocess.run(["ffprobe", "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=avg_frame_rate,r_frame_rate",
                              "-of", "json", inp], capture_output=True, text=True, timeout=15).stdout
        st = json.loads(out)["streams"][0]
        for k in ("avg_frame_rate", "r_frame_rate"):
            n, d = st.get(k, "0/0").split("/")
            if float(d) > 0 and 1 <= float(n) / float(d) <= 60:
                return float(n) / float(d)
    except Exception:  # noqa: BLE001
        pass
    return 25.0


def ffmpeg_cmd(inp, is_file, w, start, fps, q, audio, vfd, afd):
    c = ["ffmpeg", "-hide_banner", "-v", "error", "-nostdin"]
    if is_file:
        c += ["-readrate", "1", "-readrate_initial_burst", "0.5"]
    else:
        c += ["-fflags", "nobuffer", "-rtsp_transport", "tcp"] if inp.startswith("rtsp") else ["-fflags", "nobuffer"]
    if HW == "vaapi":
        c += ["-hwaccel", "vaapi", "-hwaccel_device", DEVICE, "-hwaccel_output_format", "vaapi"]
    if start > 0:
        c += ["-ss", "%.3f" % start]
    c += ["-i", inp, "-map", "0:v:0"]
    fps_s = "%.6f" % fps
    if HW == "vaapi":
        c += ["-vf", "fps=%s,scale_vaapi=w=%d:h=-2:format=nv12" % (fps_s, w), "-c:v", "mjpeg_vaapi", "-global_quality", str(q)]
    else:
        qv = max(2, min(31, round(2 + (100 - q) / 4)))   # 80 -> 7, 90 -> 4 ... rough match to the GPU scale
        c += ["-vf", "fps=%s,scale=%d:-2" % (fps_s, w), "-c:v", "mjpeg", "-q:v", str(qv)]
    c += ["-f", "mjpeg", "pipe:%d" % vfd]
    if audio:
        c += ["-map", "0:a:0?", "-vn", "-ac", str(CH), "-ar", str(RATE), "-af", "volume=%sdB,alimiter=limit=0.89:level=false" % GAIN_DB,
              "-c:a", "pcm_s16le", "-f", "s16le", "pipe:%d" % afd]
    return c


def read_jpegs(f, out, stop):
    """Split a concatenated MJPEG byte stream into JPEGs (SOI ... EOI)."""
    buf = b""
    while not stop.is_set():
        d = os.read(f, 256 * 1024)
        if not d:
            break
        buf += d
        while True:
            s = buf.find(b"\xff\xd8")
            if s < 0:
                buf = b""
                break
            e = buf.find(b"\xff\xd9", s + 2)
            if e < 0:
                buf = buf[s:]
                break
            out.put(("v", buf[s:e + 2]))
            buf = buf[e + 2:]
    out.put(("v", None))


def read_audio(f, out, stop):
    buf = b""
    while not stop.is_set():
        d = os.read(f, 64 * 1024)
        if not d:
            break
        buf += d
        while len(buf) >= CHUNK:
            out.put(("a", buf[:CHUNK]))
            buf = buf[CHUNK:]
    if buf:
        out.put(("a", buf))
    out.put(("a", None))


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.0"

    def log_message(self, f, *a):
        log("%s %s" % (self.address_string(), f % a))

    def reply(self, code, obj):
        b = json.dumps(obj).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(b)))
        self.end_headers()
        self.wfile.write(b)

    def do_GET(self):
        url = urllib.parse.urlparse(self.path)
        qs = {k: v[-1] for k, v in urllib.parse.parse_qs(url.query).items()}
        if url.path == "/health":
            return self.reply(200, {"ok": True, "version": VERSION, "hw": HW})
        if TOKEN and self.headers.get("Authorization", "") != "Bearer " + TOKEN:
            return self.reply(401, {"ok": False, "error": "unauthorized"})
        if url.path != "/av":
            return self.reply(404, {"ok": False, "error": "no such endpoint"})
        try:
            inp, is_file = resolve(qs.get("src", ""))
            w = max(160, min(1920, int(float(qs.get("w", "1280"))))) // 2 * 2
            start = max(0.0, float(qs.get("start", "0")))
            q = max(10, min(100, int(float(qs.get("q", "80")))))
            audio = qs.get("audio", "1") not in ("0", "false", "no")
            # a live source's frame rate can't be probed quickly (RTSP takes seconds); use a fixed rate unless given
            fps = float(qs["fps"]) if "fps" in qs else (probe_fps(inp) if is_file else 15.0)
        except (ValueError, KeyError) as e:
            return self.reply(400, {"ok": False, "error": str(e)})
        self.stream(inp, is_file, w, start, fps, q, audio)

    def stream(self, inp, is_file, w, start, fps, q, audio):
        vr, vw = os.pipe()
        ar, aw = os.pipe() if audio else (None, None)
        cmd = ffmpeg_cmd(inp, is_file, w, start, fps, q, audio, vw, aw if audio else 0)
        fds = (vw, aw) if audio else (vw,)
        p = subprocess.Popen(cmd, pass_fds=fds, stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
        for fd in fds:
            os.close(fd)
        # drain ffmpeg's messages continuously: a live source's warnings would otherwise fill the pipe and stall it
        errs = collections.deque(maxlen=5)
        def drain():
            for line in iter(p.stderr.readline, b""):
                errs.append(line.decode(errors="replace").strip())
        threading.Thread(target=drain, daemon=True).start()
        stop = threading.Event()
        q_ = queue.Queue(maxsize=400)
        threading.Thread(target=read_jpegs, args=(vr, q_, stop), daemon=True).start()
        if audio:
            threading.Thread(target=read_audio, args=(ar, q_, stop), daemon=True).start()
        self.send_response(200)
        self.send_header("Content-Type", "multipart/x-mixed-replace; boundary=frame")
        self.send_header("Cache-Control", "no-cache")
        self.end_headers()
        log("av start %s w=%d fps=%.3f hw=%s audio=%s" % (inp if not is_file else os.path.relpath(inp, MEDIA), w, fps, HW, audio))
        nv = na = 0
        abytes = 0
        vdone, adone = False, not audio
        pend_v, pend_a = [], []      # interleave in timestamp order
        t0 = time.monotonic()
        try:
            def take(item):
                nonlocal vdone, adone, nv, na, abytes
                kind, data = item
                if kind == "v":
                    if data is None: vdone = True
                    else: pend_v.append((int(round(nv * 1000 / fps)), data)); nv += 1
                else:
                    if data is None: adone = True
                    else: pend_a.append((int(abytes * 1000 / (RATE * CH * 2)), data)); abytes += len(data); na += 1

            while not (vdone and adone and not pend_v and not pend_a):
                # take everything already queued, without waiting
                while True:
                    try: take(q_.get_nowait())
                    except queue.Empty: break
                # a live source whose audio never starts: treat as video-only after 5 s
                if not adone and not pend_a and abytes == 0 and nv > 0 and time.monotonic() - t0 > 5:
                    adone = True
                # send the earlier head; while both tracks are live, wait until each has something queued
                if pend_v and pend_a:
                    kind = "a" if pend_a[0][0] <= pend_v[0][0] else "v"
                elif pend_v and adone:
                    kind = "v"
                elif pend_a and vdone:
                    kind = "a"
                else:
                    try: take(q_.get(timeout=0.2))   # nothing can be sent yet: wait for the next item
                    except queue.Empty: pass
                    continue
                if kind == "v":
                    ts, data = pend_v.pop(0)
                    head = b"--frame\r\nContent-Type: image/jpeg\r\nX-Timestamp: %d\r\nContent-Length: %d\r\n\r\n" % (ts, len(data))
                else:
                    ts, data = pend_a.pop(0)
                    head = b"--frame\r\nContent-Type: audio/L16;rate=%d;channels=%d\r\nX-Timestamp: %d\r\nContent-Length: %d\r\n\r\n" % (RATE, CH, ts, len(data))
                self.wfile.write(head + data + b"\r\n")
        except (BrokenPipeError, ConnectionResetError):
            pass
        finally:
            stop.set()
            try:
                p.kill()
            except Exception:  # noqa: BLE001
                pass
            err = list(errs)[-3:]
            for fd in (vr, ar):
                if fd is not None:
                    try: os.close(fd)
                    except OSError: pass
            log("av end: %d frames, %d audio parts, %.1f s%s" % (nv, na, time.monotonic() - t0, (" | ffmpeg: " + " / ".join(err)) if err else ""))


if __name__ == "__main__":
    log("relay %s on :%d, hw=%s, media=%s, hosts=%s, token=%s" % (VERSION, PORT, HW, MEDIA, ",".join(sorted(HOSTS)) or "-", "yes" if TOKEN else "no"))
    ThreadingHTTPServer(("0.0.0.0", PORT), Handler).serve_forever()
