# Tizen Notifier

Overlay notifications on Samsung Tizen TVs, driven by Home Assistant (or anything that can send HTTP).

A small resident .NET app on the TV draws messages on top of whatever is playing: streaming apps, live TV, a
Frame TV's Art Mode. Video keeps playing, and the remote keeps controlling the app underneath unless a message has buttons.
Messages can carry text, pictures, a live-ish camera view, GIFs, Lottie animations, gradients, progress bars, buttons and
sound. Every look is defined in the request, so new layouts never need a new install.

It can also show a **Home Assistant dashboard** as a live, read-only overlay (for example over Art Mode), in one of
three themes set from the dashboard itself (`default`, a faithful `lovelace`, and a quiet single-color `ambient`), and play
**video with sound in a window or full screen without the TV's video decoder**, so the picture underneath keeps playing.
Video goes through a small relay (`relay/`) that transcodes on another machine's GPU.

Tested on two Frame TVs (Tizen 6.0, tizen80) and a Tizen 5.0 (TIZEN5, tizen50) TV which has slightly less feature support.

Other models and firmware are untested.

---

This is a native NUI (.NET) app that opens a **transparent, non-focusable window of type `Notification`** and sets
its notification level. That needs the `http://tizen.org/privilege/window.priority.set` privilege, which is granted to a
Samsung **Partner**-level certificate. This requires logging into your Samsung account.

---

## How it works

- The app has no screen of its own: its main window is transparent, non-focusable and kept out of the way.
- **Tizen 6+**: each message is its own small `Notification` window, positioned on screen.
- **Tizen 5**: NUI API5 cannot create extra windows, so every message is a view inside the app's single main window.
  That window is made a `Notification` window. It is 1×1 pixel when idle and full screen only while a message is up.
- An HTTP server on the TV (port **9123**) takes JSON requests. Every request except `GET /health` needs
  `Authorization: Bearer <token>`. The token is per TV and baked into that TV's build.
- Pictures, sounds and animations are sent inline (`data:` URIs) or fetched by the app from **allow-listed hosts only**.
  The TV never holds credentials for your cameras. For dashboards it holds a read-only token of a **non-admin** Home
  Assistant user, and only when built with one (`HA_URL`, `HA_TOKEN_FILE`); its websocket client refuses every command
  that could change anything.

---

## Requirements

1. **Developer Mode** on each TV: Apps → type `12345` → on, set *Host PC IP* to the machine that will run `sdb`, then
   restart the TV. Check it with `curl http://<tv>:8001/api/v2/` (`developerMode: "1"`, `developerIP`).
2. **A Samsung Partner certificate** that lists each TV's DUID. This needs a Samsung account, but not Tizen Studio:
   - Read each DUID with `sdb connect <tv>` then `sdb -s <tv>:26101 shell 0 getduid`. Tizen 5.0 was harder but eventually could pull it.
   - Sign in at
     `https://account.samsung.com/accounts/TDC/signInGate?clientId=v285zxnl3h&tokenType=TOKEN` and save the JSON the
     page shows. The token in it goes only to Samsung's certificate server.
   - Create the author and distributor certificates with [tizen.js](https://github.com/reisxd/tizen.js)
     (`SamsungCertificateCreator`, `privilegeLevel: 'Partner'`).
   - **To add a TV later, keep the author certificate.** A new author certificate means installed apps can't be upgraded
     in place. `cert-tools/make_distributor.js` makes a new distributor certificate for a list of DUIDs and keeps the
     existing author.
   - Certificates last **one year**. After that you cannot sign or install new builds until you renew. I don't know if installed apps keep running, I haven't tried it.
3. **Build tools**: .NET SDK 6 (the `mcr.microsoft.com/dotnet/sdk:6.0` image works) with `Tizen.NET.Sdk` 1.1.9 from
   NuGet. No Tizen Studio or Tizen workload is needed. `tizen80` targets Tizen 6+ and `tizen50` targets Tizen 5.
   `build.sh` runs the build in that Docker image, so the build machine needs only Docker.
4. **Install tools**: `sdb` and the `tizen` CLI from the Tizen SDK command-line tools, on the machine set as the TV's
   *Host PC IP*, either installed there or in a Docker image of your choice.

---

## Build and install

Copy `build.env.example` to `build.env` (ignored by git) and set at least `CERT_DIR` and `ALLOW`. Then:

```
./build.sh build   <tv>        # -> out/TizNotify1-<tv>.tpk: signed, with that TV's token baked in
./build.sh install <tv> <ip>   # build, push device-profile.xml, tizen install, launch only if the install completed
```

- `<tv>` is a name of your choosing (`living_room`). The first build of a TV creates its token at `TOKEN_FILE`; put the
  same token in Home Assistant's `secrets.yaml`. List Tizen 5 TVs in `TIZEN5_TVS`.
- The source and certificates are streamed into the build container on stdin, with no bind mounts, so
  `BUILD_DOCKER` can be any command that runs Docker, local or remote (`docker`, `ssh buildhost docker`, ...).
  The certificates exist only inside the container while it builds.
- Install runs `sdb` and `tizen` on this machine, or inside `TIZEN_TOOLS_IMAGE` through `INSTALL_DOCKER`. Either way it
  must run on the TV's *Host PC IP*.

Details:

- `res/config.json` (generated, **contains the token**. Don't commit it.):
  `{"token": "...", "port": 9123, "allow": ["<ha-ip>", "<camera-ip>", ...]}`
- Signing: the csproj reads `AuthorPath`, `AuthorPass`, `DistributorPath` and `DistributorPass`. The passwords are read
  from a file at build time so they never appear on a command line.
- Tizen 5 build: TFM `tizen50`, `Tizen.NET` 5.0.0.*, `<DefineConstants>TIZEN5</DefineConstants>`, manifest
  `api-version="5.0"`. `build.sh` switches these for the TVs in `TIZEN5_TVS`.
- Before installing, the certificate's `device-profile.xml` goes to `/home/owner/share/tmp/sdk_tools/` on the TV
  (`build.sh install` does this).
- If developer shell (i.e., Tizen 5) is disabled, installs work, but remote uninstall doesn't.
  (`sdb shell` returns `closed`).

---

## HTTP API (on the TV, port 9123)

| Method | Path | |
|---|---|---|
| GET | `/health` | `{ok, version, build}`; no auth |
| GET | `/info` | version, runtime, uptime, capabilities (from the last selftest), active ids, allow list |
| GET | `/log?n=100` | log lines, read from the log file, so they survive crashes and restarts |
| POST | `/selftest` | creates and disposes each optional feature; shows nothing |
| POST | `/notify` | show a message; sending the same `id` again replaces it in place |
| POST | `/update` | `{id, nodes: {<name>: {props}}, duration?}`: change named nodes live |
| POST | `/dismiss` | `{id}` or `{all: true}` |
| POST | `/config` | `{allow: [hosts]}`: persist the allow list |
| POST | `/host` | Tizen 5 build only: report or drive the shared host window (diagnostics) |

**The full request reference, with every property, tested recipes, the button rules, video (`/play`) and dashboards, is
[usage.md](usage.md).** In short:

```json
{ "id": "front-door", "duration": 10, "w": 900, "h": 230, "anchor": "top-right", "margin": 60,
  "enter": {"type": "slide"}, "sound": {"src": "data:audio/wav;base64,...", "volume": 0.05},
  "callback": "http://ha:8123/api/webhook/xyz", "data": {"any": "thing"},
  "root": {"type": "box", "bg": "#0d0d12f0", "radius": 28, "children": [
      {"type": "image", "src": "http://camera/latest.jpg", "refresh_ms": 400, "x": 20, "y": 20, "w": 320, "h": 190, "fit": "cover"},
      {"type": "text", "x": 370, "y": 26, "w": 490, "text": "Front door", "size": 30, "bold": true, "color": "#73bfff"},
      {"type": "progress", "x": 370, "y": 196, "w": 490, "h": 8, "color": "#73bfff"} ]}}
```

- **Node types:** `box`, `text`, `image`, `gif`, `lottie`, `video`, `progress`, and `nui:<ClassName>` for any NUI view.
- **Common properties:** position, size, `bg`, `gradient`, `radius`, `opacity`, `scale`, `shadow`, `padding`,
  `layout: row|column`, `animate` (with keyframes), `props` / `call` (raw NUI properties and methods by name).
- **Buttons:** any node with `action` becomes a button. While a message has buttons it takes the remote: Left/Right
  move, OK picks, Back closes, with a hard time limit. Presses go to `callback` as `{id, action, data}`.
- **Stacking:** messages on the same anchor stack, and glide into place when one above them closes.
- Screen coordinates are 1920×1080.

---

## Measured behavior and limits (read before building on it)

- **Sound:** the default engine writes raw PCM through `AudioPlayback` (WAV 8/16-bit, or MP3 decoded in-app with NLayer).
  It **mixes over playback without interrupting it**. The other engine, `engine: "player"` (Tizen's media `Player`),
  **took the media resource from the playing app and stopped its video**; don't use it while something is playing.
  - `volume` is a linear sample scale: 0.05 is moderate and 0.3 is already loud. The Home Assistant script below maps
    a 0–1 loudness value to volume³.
- **Video:** the `video` node (`VideoView`) **blanks the picture of the app underneath, and that app does not recover**.
  An RTSP source even crashed the notifier. The app refuses a video node unless the request says `"takeover": true`.
  For a camera, use `image` + `refresh_ms` (2–5 fps, double-buffered, flicker-free, no media player involved).
- **Tizen 6 (Frames):**
  - `CornerRadius` on an image blanks it. The app clips rounded images inside a rounded box instead.
  - NUI cannot load an image from a URL by itself, so the app downloads it first.
  - Text markup needs single-quoted attributes and reads 8-digit colors as `#AARRGGBB`. The app converts
    `value="#RRGGBBAA"` automatically.
- **Tizen 5:** no rounded corners, box or text shadows, Lottie, or `LinearLayout` (row/column children are placed
  manually and need explicit sizes). GIFs work through the plain image view. Unsupported properties produce `warnings`
  in the response, not errors, so the same request works on every TV.
- **Lifecycle:** the app survives switching apps, but **a TV power-off kills it**. Restart it with
  `POST http://<tv>:8001/api/v2/applications/TizNotify1.notifier`. The restart is invisible and the app answers within
  about 2 seconds. The Home Assistant scripts do this automatically and never turn a TV on.
- Messages show over Art Mode on the Frame.

---

## Home Assistant

`ha/tizen_notifier.yaml` is a package (`homeassistant: packages: !include_dir_named packages`). It ships with one
example TV, `living_room` at `192.168.1.50`; replace it with yours, and set `ha` in `script.tizen_notify` to the
address the TV uses to reach Home Assistant (that host must be in `ALLOW`).

- `rest_command.tizen_notifier_<tv>`: one per TV, with its token from `secrets.yaml`
  (`tizen_notifier_<tv>_auth: "Bearer <token>"`), so the token never shows up in script traces.
- **`script.tizen_notify`** (call it with a response):
  - **Fields:** `tv`, `title`, `message`, `image` (`camera.*` entity, `/local/...` path or an allow-listed URL),
    `refresh_ms`, `duration`, `anchor`, `sound` + `volume` (0–1 loudness), `buttons` (labels or `{id, label}`),
    `webhook_id`, `id`, `markup`.
  - **Custom layouts:** pass `payload` (a full request) instead of the fields.
  - **Camera images** use `/api/camera_proxy/<entity>?token=<access_token>`, so the TV holds no Home Assistant login.
    These tokens rotate, so very long-lived `refresh_ms` views may stop updating.
- `script.tizen_notify_dismiss`: close one message (`id`) or all.
- `script.tizen_notifier_ensure`: health check, and a restart over `:8001` if the notifier is down. The TV must be on.
  It uses the package's `rest_command.tizen_notifier_tv_api_get` / `_post` (the TV's own API).

Example:

```yaml
action: script.tizen_notify
data:
  tv: living_room
  title: Front door
  message: Someone is at the door
  image: camera.front_door
  refresh_ms: 400
  duration: 20
  buttons: [Unlock, Ignore]
  webhook_id: tv_front_door_buttons
response_variable: r
```

Adding a TV: add its entry to the `tvs` maps and the `tv` selectors in the scripts, add a
`rest_command.tizen_notifier_<tv>`, add its secret, build with its token, and install.

---

## Repository layout

| Path | |
|---|---|
| `src/` | the app (current line; Tizen 6+ default, `TIZEN5` for Tizen 5) |
| `ha/tizen_notifier.yaml` | Home Assistant package |
| `build.sh`, `build.env.example` | build and install |
| `cert-tools/make_distributor.js` | distributor certificate for N DUIDs, keeping the author certificate |
| `relay/` | video relay: Dockerfile, compose file and server (ffmpeg, VA-API) |
| `dash-host/`, `tools/preview.py` | render a dashboard on a desktop and draw it as a PNG (layout checks without a TV) |
| `dash-tests/` | tests for the dashboard renderer (offline: `docker run --rm -v "$PWD":/w -w /w/dash-tests mcr.microsoft.com/dotnet/sdk:6.0 dotnet test`) |
| `src/res/fonts/` | Inter and Roboto (static cuts) with their licenses, for dashboard themes and `"font"` on text nodes |
| `docs/` | design notes |
| `examples/` | the requests used in testing |
| `testassets/` | test GIF, Lottie and sounds (generated for this project) |

The `examples/` requests use `homeassistant.local` and `camera.local` as placeholder hosts.

## Credits

- [NLayer](https://github.com/naudio/NLayer) (MIT): MP3 decoding, bundled as a NuGet package.
- [tizen.js](https://github.com/reisxd/tizen.js): Samsung certificate creation without Tizen Studio.
- Tizen.NET / TizenFX (Apache-2.0): the platform libraries.
- [Inter](https://github.com/rsms/inter) and [Roboto](https://github.com/googlefonts/roboto-classic) (SIL Open Font
  License 1.1): fonts for dashboard themes, as static weight cuts of the variable fonts. Licenses in `src/res/fonts`.

## License

MIT. See [LICENSE](LICENSE).
