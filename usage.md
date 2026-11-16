# Tizen Notifier: usage

This is the complete reference for what a notification can contain. It is written so that someone (or an agent) with
no access to the source can build correct requests. Every key the app reads is listed here. Anything not listed is
either ignored or produces an `"unknown property"` warning.

Conventions in this file:

- **Tested** means a request using that feature was run on a TV and looked right. The saved tested requests are in
  `examples/`. Everything else is described from the source.
- Coordinates are pixels on a 1920×1080 screen, whatever the TV's panel resolution.
- `T6` = Tizen 6+ build (the Frame TVs), `T5` = Tizen 5 build. Where they differ it is called out.

---

## 1. Rules that break notifications if you ignore them

Read these first. Most of these failures are silent.

1. **Types must be real JSON types.** Numbers must be numbers and booleans must be booleans. `"w": "900"`,
   `"duration": "10"` and `"multiline": "true"` are ignored without a warning, and the default is used instead.
2. **Every text node needs `color`.** Don't rely on the default text color. Every tested layout sets it.
3. **Every card needs a background.** The window is fully transparent. If the root has no `bg` or `gradient`, only the
   loose text and pictures show over the video.
4. **Text without `h` sizes its own height**, so `valign` does nothing on it. To center a label in a button or box,
   give the text `x: 0, y: 0`, the **same `w` and `h` as its parent**, `"align": "center"` and `"valign": "center"`.
5. **Children are positioned relative to their parent**, not the window. They are drawn in array order, so later
   children appear on top.
6. **The window clips everything outside its `w`×`h`** (T6), including shadows, focus scale-ups and slide animations.
   Leave room inside the window for anything that grows or casts a shadow.
7. **A media fetch failure fails the whole request.** If an `image`, `gif`, `lottie` or `video` `src` cannot be
   fetched or decoded, `/notify` returns 400 and nothing is shown. A failed `sound` is only a warning.
8. **URLs must be on the TV's allow list** (host, or host:port). This applies to every `src` URL and the `callback`.
   Check the list with `GET /info` (`allow`).
9. **Always read `warnings` in the response.** Unsupported properties, a skipped video or an ignored callback are
   reported there and not as errors.
10. **Buttons:** see section 7. The look of a selected button changes **only the button node itself**, never its
    children.

---

## 2. Sending a request

```
curl -sS -X POST http://<tv-ip>:9123/notify \
  -H "Authorization: Bearer <token>" \
  -H "Content-Type: application/json" \
  --data @message.json
```

- The server needs a `Content-Length` header. It does not read chunked bodies; `curl --data` and Home Assistant's
  `rest_command` both send the header.
- Max request body: 24 MB. Max single media file (inline or fetched): 16 MB.
- Response on success: `{"ok": true, "id": "<id>", "warnings": [ ... ]}`.
- Response on failure: `{"ok": false, "error": "..."}` with 400 (bad request or media failure), 401 (bad or missing
  token), 404 (unknown endpoint or, for `/update`, no such active message) or 500.

### Endpoints

| Method | Path | Body | Result |
|---|---|---|---|
| GET | `/health` | none, **no auth** | `{ok, version, build}` |
| GET | `/info` | none | `version`, `runtime`, `uptime_s`, `screen`, `active` (ids on screen), `allow`, `port`, `capabilities` (from the last selftest) |
| GET | `/log?n=100` | none | `{log: [...]}`, last n lines (max 300), survives crashes |
| POST | `/selftest` | none | `{capabilities: {text, image, gif, lottie, video, layout, shadow, gradient, keyframes, notification_window, audio_player}}`, each `"ok"` or an error. Nothing is shown. |
| POST | `/notify` | a message (section 3) | shows it, or replaces the message with the same `id` |
| POST | `/update` | `{id, nodes, duration?}` (section 9) | changes named nodes of a message on screen |
| POST | `/dismiss` | `{"id": "..."}` or `{"all": true}` | `{ok, closed: <count>}` |
| POST | `/config` | `{"allow": ["host", "host:port", ...]}` | replaces and saves the allow list |
| POST | `/host` | `{"actions": [...]}` | T5 only: diagnostics for the shared host window |
| POST | `/play` | `{src, ...}` (section 12) | play a video through the relay: full screen or a window |
| POST | `/dashboard` | `{dashboard, ...}` (section 13) | show a Home Assistant dashboard, live |
| GET | `/dashboard` | none | dashboard sessions and their status |
| POST | `/dashboard/stop` | `{"id": "..."}` or `{"all": true}` | stop a dashboard |
| GET | `/probe` | none | what the TV's media stack offers (nothing is shown) |

Run `POST /selftest` once after the app starts, then read `capabilities` to see which node types a TV supports.

### Inline media (`data:` URIs)

Any `src` can be a base64 data URI. It **must** contain `;base64,`. The MIME type sets the file type, so it must be right:

| MIME type | Use for |
|---|---|
| `image/png`, `image/jpeg`, `image/webp`, `image/svg+xml` | `image` |
| `image/gif` | `gif` |
| `application/json` | `lottie` (required: a Lottie file with another MIME type is saved as `.bin` and will not load) |
| `audio/wav` (`audio/x-wav`, `audio/wave`), `audio/mpeg` (`audio/mp3`) | `sound` |
| `video/mp4` | `video` |

On macOS: `printf 'data:image/png;base64,%s' "$(base64 -i pic.png | tr -d '\n')"`.

URLs work too, but only `http`/`https` on the allow list (`rtsp` only for `video`). Local file paths are not accepted.
For a fetched URL, the file type comes from the response `Content-Type`, then from the URL's extension.

---

## 3. The message (top level of `/notify`)

| Key | Type | Default | Meaning |
|---|---|---|---|
| `root` | node | **required** | the content (section 4) |
| `id` | string | generated | sending the same `id` again **replaces** the message in place (no new enter animation, no callback) |
| `duration` | number (s) | `10` | time on screen. `0` = until dismissed. With buttons, see section 7. |
| `w`, `h` | number | `900`, `200` | window size, clamped to 10..1920 and 10..1080 |
| `anchor` | string | `"top-right"` | `top-left`, `top`, `top-right`, `left`, `center`, `right`, `bottom-left`, `bottom`, `bottom-right` |
| `margin` | number or `[mx, my]` | `60` | distance from the screen edges for the anchor |
| `x`, `y` | number | none | absolute window position. Giving either one turns off `anchor` and stacking. |
| `stack` | bool | `true` | messages on the same **`top*` or `bottom*`** anchor stack instead of overlapping. `left`, `center`, `right` never stack. |
| `gap` | number | `20` | space between stacked messages |
| `level` | string | `"base"` | window layer: `base`, `medium`, `high`, `top`. Ignored on T5. |
| `enter` | object | slide, 400 ms | entrance motion (section 8) |
| `exit` | object | same as `enter`, 300 ms | exit motion (section 8) |
| `reanimate` | bool | `false` | when replacing by `id`, play the `enter` motion again |
| `sound` | object | none | sound played when the request arrives (section 10) |
| `callback` | URL | none | gets `POST {id, action, data}` on button press, Back, timeout and dismiss (section 7) |
| `data` | any JSON | none | sent back unchanged in every callback |
| `timeout` | number (s) | `60` | hard limit for messages **with buttons**, clamped to 5..600 |
| `takeover` | bool | `false` | must be `true` for a `video` node to be built (section 6.6) |

Stacking: messages are ordered by arrival. On a top anchor the oldest is nearest the edge and new ones go below it.
When one closes, the others glide into place. A replaced message keeps its place.

---

## 4. Nodes

A node is a JSON object. `root` is a node and every node can have `children`.

```json
{"type": "box", "name": "card", "x": 0, "y": 0, "w": 900, "h": 230, "bg": "#0d0d12f0", "radius": 28, "children": []}
```

### Node types

| `type` | What it is | T6 | T5 |
|---|---|---|---|
| `box` (default) | container / rectangle | yes | yes |
| `text` | a label | yes | yes |
| `image` | a still picture, optionally refreshing (camera view) | yes | yes |
| `gif` | animated GIF, plays and loops automatically | yes | yes |
| `lottie` | Lottie JSON animation | yes | **no** (warning, empty box) |
| `progress` | countdown bar | yes | yes |
| `video` | video player. **Blanks the app underneath, and Art Mode, in either mode.** | needs `takeover` | needs `takeover` |
| `nui:<ClassName>` | any concrete view class in `Tizen.NUI` (e.g. `nui:TextLabel`, `nui:ImageView`), set up with `props`/`call` | yes | yes |

An unknown `type` becomes a `box` with a warning. There is no `button` type: any node with `action` is a button.
Classes from `Tizen.NUI.Components` (Button, Slider...) are **not** available through `nui:`.

### Properties every node accepts

| Key | Type | Meaning |
|---|---|---|
| `name` | string | handle for `/update` |
| `x`, `y` | number | position inside the parent, default `0` |
| `w`, `h` | number | size. If missing: the rest of the parent (`parent_w - x`, `parent_h - y`). Text without `h` sizes its own height. Inside a `layout`, see below. |
| `bg` | color | background color |
| `gradient` | object | `{"colors": [c1, c2, ...], "stops": [0, ..., 1], "angle": 90}`. At least 2 colors. `angle` 0 = left to right, 90 (default) = top to bottom. `stops` default to even spacing. Don't combine with `bg` on the same node. |
| `radius` | number | rounded corners. T5: not supported (warning). On `image`, handled by a clipping box (section 6.2). |
| `opacity` | number 0..1 | |
| `scale` | number or `[sx, sy]` | scales around the node's center |
| `visible` | bool | `false` hides it |
| `shadow` | object | `{"blur": 20, "x": 0, "y": 6, "color": "#000000aa", "spread": 0}` (those are the defaults). T6 only. Clipped by the window (rule 6). |
| `padding` | number or `[left, right, top, bottom]` | inner spacing. Used by `layout`. |
| `layout` | `"row"` or `"column"` | lays children out automatically (below) |
| `gap` | number | space between children in a `layout` |
| `animate` | array | property animations (section 8) |
| `action` | string | makes the node a button (section 7) |
| `focus` | object | the button's selected look (section 7) |
| `props` | object | raw NUI properties (section 11) |
| `call` | array | raw NUI method calls (section 11) |
| `children` | array of nodes | |

### Colors

Accepted everywhere a color goes (`bg`, `color`, gradient `colors`, shadow `color`, focus):

- `"#RGB"`, `"#RGBA"`, `"#RRGGBB"`, `"#RRGGBBAA"`. **Alpha is last.** `"#0d0d12f0"` is near-black at 94%.
- `[r, g, b]` or `[r, g, b, a]`, either 0..1 or 0..255. If any of r, g, b is above 1, the list is read as 0..255. An
  alpha above 1 is read as 0..255.
- Names: `transparent`, `white`, `black`, `red`, `green`, `blue`, `yellow`. **No other names, no `rgba(...)`.** A bad
  color is a warning and that property is not set.

Colors **inside text markup** follow different rules (section 5).

### Row / column layout

`"layout": "row"` or `"column"` (with `gap`, `padding`) places children one after another. Children's `x`/`y` are ignored.

- T6: a child's `w`/`h` may be a number, `"fill"` (match parent), or left out (wrap content).
- **Give explicit `w` and `h` to every child that has its own children or is a `progress`.** A child without a number
  size gives its own children a 1×1 area to fill.
- T5: there is no layout engine. Children are placed manually along the axis, starting at the leading padding. **Every
  child needs explicit `w` and `h`.** The cross axis uses the child's own `x`/`y`.
- For notifications, absolute `x`/`y` is simpler and identical on both TV types. The tested layouts all use it.

---

## 5. Text

```json
{"type": "text", "x": 40, "y": 30, "w": 820, "text": "Front door", "size": 34, "bold": true, "color": "#ffffff"}
```

| Key | Type | Meaning |
|---|---|---|
| `text` | string | the text. Numbers are converted. |
| `color` | color | **set it every time** |
| `size` | number | point size. Tested range 24..44 on a 1920×1080 card: 24 caption, 28..30 body, 34..36 heading. |
| `pixel_size` | number | size in pixels instead of points |
| `bold` | bool | bold |
| `weight` | string | font weight name, e.g. `"light"`, `"normal"`, `"bold"` (use instead of `bold`, not with it) |
| `font` | string | font family; must exist on the TV. Leave it out for the system font. |
| `align` | `left`/`begin`, `center`, `right`/`end` | horizontal |
| `valign` | `top`, `center`/`middle`, `bottom` | vertical. **Needs an explicit `h`.** |
| `multiline` | bool | wrap long text inside `w` (otherwise one line) |
| `ellipsis` | bool | cut overflow with "..." |
| `line_spacing` | number | extra space between lines |
| `markup` | bool | enable inline markup |
| `text_shadow` | object | `{"color": "#000000", "x": 2, "y": 2, "blur": 2}` (defaults). T6 only. |
| `outline` | object | `{"color": "#000000", "width": 2}`: an outline around every glyph, TV px (app 1.2.0+). |

Wrapping text: set `w`, `"multiline": true`, and either no `h` (grows) or an `h` with `"ellipsis": true` (cut off).

### Markup (tested: `<color>`)

```json
{"type": "text", "x": 40, "y": 40, "w": 820, "markup": true, "size": 30, "color": "#cccccc",
 "text": "<color value='#73bfff'>mixed</color> color <color value='#ffcc66'>markup</color>"}
```

- Set `"markup": true`.
- Use single quotes on attribute values. The app also rewrites `value="..."` to `value='...'`.
- Use `#RRGGBB`. For `#RRGGBBAA` (alpha last) the app converts to the TV's `#AARRGGBB` form.
- `<color value='...'>` is the tested tag. NUI also defines `<font family='..' size='..' weight='..' slant='..'>`. It
  is not tested on these TVs.
- With markup on, write `&lt;`, `&gt;`, `&amp;` for literal `<`, `>`, `&`.

---

## 6. Pictures, animation and video

### 6.1 `image` (tested)

```json
{"type": "image", "src": "http://camera.local/snapshot.jpg", "x": 20, "y": 20, "w": 320, "h": 180, "fit": "cover"}
```

| Key | Meaning |
|---|---|
| `src` | `data:` URI or allow-listed URL (downloaded by the app before the message is shown) |
| `fit` | `contain` (default, shrink to fit), `cover` (fill and crop; the tested card uses this), `fill` (stretch), `width`, `height` |
| `radius` | rounded corners (see 6.2) |
| `refresh_ms` | re-download `src` this often, for a camera view (see 6.3) |

NUI cannot load a URL by itself. `nui:ImageView` with `props.ResourceUrl` set to an http URL does **not** work; use `image`.

### 6.2 Rounded pictures (tested)

`"radius"` on an `image` is safe on T6. Setting the corner radius on the image itself blanks it, so the app wraps it
in a rounded box that clips it. On T5 the corners stay square.

For a rounded `gif` (or anything else), build the clip box yourself:

```json
{"type": "box", "x": 40, "y": 30, "w": 120, "h": 120, "radius": 60, "props": {"ClippingMode": "ClipChildren"},
 "children": [{"type": "gif", "src": "data:image/gif;base64,...", "x": 0, "y": 0, "w": 120, "h": 120}]}
```

### 6.3 Live camera view (tested)

```json
{"type": "image", "name": "cam", "src": "http://homeassistant.local:8123/api/camera_proxy/camera.front?token=...",
 "refresh_ms": 400, "x": 20, "y": 20, "w": 320, "h": 190, "radius": 16, "fit": "cover"}
```

- `src` must be http(s). With a `data:` URI, `refresh_ms` is dropped with a warning.
- The minimum is 200 ms; 2..5 fps is the practical range. Each frame loads hidden and swaps in when ready, so it
  doesn't flicker. Fetches never overlap.
- The first frame is fetched before the message shows. A failure there fails the request. Later failures keep the
  last frame.
- No media player is involved, so the video underneath keeps playing.
- Home Assistant camera proxy tokens rotate, so a very long-lived view may stop updating.

### 6.4 `gif` (tested)

```json
{"type": "gif", "src": "data:image/gif;base64,...", "x": 40, "y": 40, "w": 120, "h": 120}
```

- Plays immediately and loops forever. It is stretched to `w`×`h`, so keep the GIF's aspect ratio.
- `fit` and `radius` do nothing on a gif (use the clip box in 6.2).
- T5 plays GIFs through the plain image view; same request.

### 6.5 `lottie` (tested on T6, app 1.0.8+)

```json
{"type": "lottie", "src": "data:application/json;base64,...", "x": 30, "y": 20, "w": 160, "h": 160, "loop": -1}
```

- `loop`: `-1` = forever (default), `N` = play N times.
- A URL must serve `application/json` or end in `.json`.
- T5: not supported. The node is an empty box with a warning, so the rest of the message still shows.
- **Needs app 1.0.8 or later** (`GET /health` shows `version`). On older builds, closing a message that contains a
  Lottie kills the notifier. On 1.0.8, closing by timeout, `/dismiss` and replacement were all tested.

### 6.6 `video` (tested on T6)

```json
{"type": "video", "src": "shows/Some Show/Season 1/episode.mkv", "start": 480, "x": 0, "y": 0, "w": 640, "h": 360,
 "volume": 1.0, "fallback": {"src": "http://camera.local/snapshot.jpg", "refresh_ms": 1000}}
```

With a **video relay** configured (section 12), a `video` node plays through it without the TV's hardware decoder: the
picture underneath (Art Mode, another app's video) stays. The relay transcodes the source on its own machine (GPU when
it has one) into a synced MJPEG + audio stream, and the node becomes an `mjpeg` node (6.7).

- `src`: a file under the relay's media folder (path relative to it), or an `http(s)` / `rtsp` URL whose host is on
  the **relay's** allow list. Cameras: go2rtc's RTSP restream, e.g. `rtsp://camera.local:8554/camera1`.
- `start` (seconds into a file), `volume` (0..1, default 1.0), `mute` (true = volume 0), `audio` (false = no sound and no
  audio clock), `quality` (JPEG quality 10..100, default 80).
- The picture is letterboxed to the source's shape inside `w`×`h`.
- **Relay not configured or not answering:** the node shows `fallback` (an image refreshed every `refresh_ms`, e.g. a
  camera snapshot) when given. Otherwise it is left out, with a warning.
- Live sources reconnect by themselves. A file plays to its end.

**Hardware playback** (`"takeover": true` at the top level of the message) uses the TV's own player instead:
- Measured: it **blanks the picture of the app underneath and that app does not recover.** An RTSP source crashed the
  notifier.
- URLs stream directly (`http`, `https`, `rtsp` on the TV's allow list). `data:` is saved to a file first.
- `loop` (bool, default `true`) and `mute` (bool, default `true`).
- `underlay` (bool, default `true`). `false` draws the video as a texture inside the message window instead of
  the TV's video layer. It plays at full speed (tested 2026-11-15 on a Frame, Tizen 6.0, H.264 MP4 over HTTP).
  **It still blanks Art Mode.** The art does not come back until Art Mode is switched on again. The hardware decoder
  is what the other app loses, not only the video layer.
- **No hardware route leaves the picture underneath intact.** Tizen's MediaCodec answers "Not Supported Feature" on
  these TVs, and the player only draws to a window's video layer or as a texture. That is why the relay exists.

### 6.7 `mjpeg`: video without the video decoder (tested on T6)

```json
{"type": "mjpeg", "src": "http://camera.local:1984/api/stream.mjpeg?src=camera", "x": 20, "y": 20, "w": 640, "h": 268, "stats": true}
```

- `src`: an allow-listed `multipart/x-mixed-replace` MJPEG stream, such as go2rtc's `/api/stream.mjpeg` or Frigate's
  MJPEG feed.
- Each frame is a JPEG decoded by the TV's image loader (native, about 5 ms for 640 px wide). Two layers swap when
  the next frame is ready. A frame that arrives while the previous one is still loading is dropped, so the picture never
  lags behind.
- **It never uses the hardware video decoder**, so the picture underneath stays: Art Mode and a video playing in another app both
  kept playing (2026-11-15, Frame, Tizen 6.0). Measured: 640 px wide at 23.976 and 25 fps with no drops to speak of, starting
  immediately, using 0.2–0.8 MB/s.
- The frame is stretched to `w`×`h`. Size the node to the stream's aspect ratio.
- Picture quality is set where the stream is encoded. go2rtc's default MJPEG quality is low and looks blocky; use
  `-q:v 3` (in go2rtc's config, because its API refuses sources containing spaces).
- `stats: true` logs received / shown / dropped fps, bandwidth and load time every 5 s (`GET /log`).
- **Synced audio and video** in one stream: the parts carry `X-Timestamp: <ms>`, and audio parts are
  `audio/L16;rate=R;channels=C` (16-bit little-endian PCM, e.g. 50 ms chunks). Send them in timestamp order, about 0.5 s
  ahead of real time. The node plays the audio on its own output, which is the clock, and shows each frame when that clock
  reaches it. Late frames are dropped and early ones wait; closing the message stops the audio. `volume` (0..1, default
  1.0) applies to that audio. `sync_trim_ms` shifts the picture against the sound by hand if a source needs it.
  Measured: average A/V offset 5–20 ms, under one frame.
- **Budget:** about 30 megapixels per second of JPEG decoding in total, however it is divided. At 24 fps that is roughly
  1.25 Mpx of video on screen at once. Measured (2026-11-15, Frame, Tizen 6.0, video playing in another app underneath):
  - one 1920×800 stream: 20–22 fps
  - one 1280×534 stream: 23–24 fps
  - six different 640-wide clips in one message, all with audio: 22.8–24 fps each, A/V offset 6–9 ms
  - nine 640-wide streams: 16–20 fps each, too many
- Put several streams in **one message** rather than one message each: separate windows update at different times
  and the screen visibly "paints" across them.
- `opacity` on the node, or on a named box around it, blends it with the picture underneath, and can be changed
  live with `/update`.

### 6.8 `progress` (tested)

```json
{"type": "progress", "x": 370, "y": 196, "w": 500, "h": 8, "radius": 4, "bg": "#ffffff22", "color": "#73bfff"}
```

- `bg` is the track and `color` is the fill (default `#4aa3ff`). `radius` rounds both.
- By default it drains from full to empty over the message's `duration`.
- `ms`: run time in ms instead of `duration`. `"reverse": true` fills from empty instead.
- With `duration: 0` and no `ms` it stays full. With buttons and `duration: 0`, set `ms` to `timeout * 1000`.
- It needs a number `w` and `h`.

---

## 7. Buttons (tested)

Any node with `"action": "<id>"` is a button. While a message has buttons:

- **It takes the remote.** Left/Up selects the previous button, Right/Down the next one. Selection wraps around. The
  app underneath gets no keys until the message closes.
- The first button in the document order is selected first. Order is depth-first in `children` order. Don't nest
  buttons inside buttons.
- **OK** sends `action` to the callback and **always closes the message**. To keep something on screen after a press,
  send a new message from the callback handler.
- **Back** (also Escape/Backspace) sends `"back"` and closes.
- **Lifetime:** the normal `duration` (default 10 s) still applies, capped by `timeout` (default 60 s, 5..600).
  `"duration": 0` means "until `timeout`", **not** forever. At the end it sends `"timeout"`.

### The selected look: `focus`

`focus` is a set of properties applied to the **button node itself** when it is selected, and undone when it is not.

- Default when `focus` is missing: `{"scale": 1.08}`. On a button without a visible `bg` that is nearly invisible,
  so always give a button a `bg` and a `focus` with a different `bg`.
- Keys that work: `bg`, `scale`, `opacity`, `radius`, `gradient` (see below), `color` (**only when the button is
  itself a `text` node**), `x`, `y`, `w`, `h`, `props`.
- **`focus.color` on a `box` button does nothing.** Its label is a child and children are never restyled. To change
  label color on selection, make the button a `text` node (with `bg`, `radius`, `h`, `align`, `valign`).
- Undoing the look restores the button's own value for each key. If the button doesn't set that key: `scale` and
  `opacity` go back to 1, `bg` to transparent, `color` to white. **Any other `focus` key must also be set on the button**,
  or it won't be undone.
- **Gradient buttons:** don't use `focus.bg` on a button that has a `gradient`; unselecting sets `bg` to transparent and
  the button disappears. Put a `gradient` in both the button and `focus`, and repeat `radius` inside `focus` so the
  gradient keeps its rounded corners.
- **Leave room for the scale-up.** `scale: 1.08` on a 250 px button adds 10 px on each side. Keep that margin inside the
  window or it is clipped.
- Errors in `focus` styling are not reported anywhere. Check the look on the TV.

### Callback

```
POST <callback>
{"id": "door", "action": "unlock", "data": {"camera": "front"}}
```

- `action` is a button id, `"back"`, `"timeout"` or `"dismissed"` (from `/dismiss`). `"timeout"` and `"dismissed"` are
  also sent for messages without buttons, when `callback` is set.
- Not sent when a message is replaced by `id`.
- The callback host must be on the allow list, otherwise it is dropped with the warning
  `callback host not in allow list; ignored`.
- Home Assistant: use a webhook trigger, `callback: http://<ha>:8123/api/webhook/<webhook_id>`.

### Canonical button message (tested)

The label is a text child with the button's exact `w`/`h`, `x: 0, y: 0`, and center alignment. The buttons sit
40 px in from the card edges, so the 1.08 scale has room.

```json
{
  "id": "door", "duration": 30, "timeout": 60, "w": 900, "h": 250, "anchor": "center",
  "enter": {"type": "fade"},
  "callback": "http://homeassistant.local:8123/api/webhook/tv_buttons",
  "data": {"camera": "front"},
  "root": {"type": "box", "bg": "#0d0d12f0", "radius": 28, "children": [
    {"type": "text", "x": 40, "y": 30, "w": 820, "text": "Front door: someone is here", "size": 34, "bold": true, "color": "#ffffff"},
    {"type": "text", "x": 40, "y": 88, "w": 820, "text": "Choose an action", "size": 28, "color": "#cccccc"},
    {"type": "box", "action": "unlock", "x": 40, "y": 150, "w": 250, "h": 70, "radius": 35, "bg": "#2a2a33",
     "focus": {"bg": "#2f7fd6", "scale": 1.08},
     "children": [{"type": "text", "x": 0, "y": 0, "w": 250, "h": 70, "text": "Unlock", "size": 28, "color": "#ffffff", "align": "center", "valign": "center"}]},
    {"type": "box", "action": "talk", "x": 325, "y": 150, "w": 250, "h": 70, "radius": 35, "bg": "#2a2a33",
     "focus": {"bg": "#2f7fd6", "scale": 1.08},
     "children": [{"type": "text", "x": 0, "y": 0, "w": 250, "h": 70, "text": "Talk", "size": 28, "color": "#ffffff", "align": "center", "valign": "center"}]},
    {"type": "box", "action": "ignore", "x": 610, "y": 150, "w": 250, "h": 70, "radius": 35, "bg": "#2a2a33",
     "focus": {"bg": "#2f7fd6", "scale": 1.08},
     "children": [{"type": "text", "x": 0, "y": 0, "w": 250, "h": 70, "text": "Ignore", "size": 28, "color": "#ffffff", "align": "center", "valign": "center"}]}
  ]}
}
```

Spacing for N buttons across a card of width W with 40 px edges and 35 px gaps:
`bw = (W - 80 - 35 * (N - 1)) / N`, `x_i = 40 + i * (bw + 35)`.

### Button whose label changes color (text node as the button)

```json
{"type": "text", "action": "ok", "x": 40, "y": 150, "w": 250, "h": 70, "text": "OK", "size": 28,
 "color": "#cccccc", "bg": "#2a2a33", "radius": 35, "align": "center", "valign": "center",
 "focus": {"bg": "#ffffff", "color": "#0d0d12", "scale": 1.08}}
```

### Mistakes that produce broken buttons

| Mistake | Result |
|---|---|
| `"type": "button"`, `"label": "OK"` | unknown type and property: a blank box, no text |
| label text without `h`, or with `w`/`h` different from the button | text stuck at the top-left, not centered |
| no `bg` on the button and no `focus` | can't see which button is selected |
| `focus.color` on a box button | label never changes color |
| `focus.bg` on a gradient button | button disappears after the selection moves off it |
| button touching the window edge | scale-up is clipped |
| `"duration": 0` expecting it to stay forever | closes at `timeout` (60 s) |
| callback host not allow-listed | presses go nowhere (warning in the response) |

---

## 8. Motion

### Enter / exit

```json
"enter": {"type": "slide", "from": "right", "ms": 450},
"exit":  {"type": "fade", "ms": 300}
```

- `type`: `slide` (default), `fade`, `scale` (from 0.8 to 1, with fade), `none`. All types fade.
- `from` (slide only): `top`, `bottom`, `left`, `right`. Default follows the anchor: `*-right` from the right, `*-left`
  from the left, `bottom*` from the bottom, otherwise from the top.
- The slide happens inside the message's own window. It wipes in from the window's edge, not from the screen edge.
- `ms`: 0..5000. Default 400 in, 300 out. `exit` defaults to the `enter` settings.

### `animate`: property animations

Any node can have a list of animations. They start when the message shows and stop when it closes.

```json
"animate": [
  {"prop": "opacity", "from": 0, "to": 1, "ms": 600, "delay": 200, "ease": "ease_out"},
  {"prop": "scale", "keyframes": [[0, 1], [0.5, 1.15], [1, 1]], "ms": 1200, "loop": true}
]
```

| Key | Meaning |
|---|---|
| `prop` | `opacity`, `x`, `y`, `w`, `h`, `scale`, or a NUI property name (`PositionX`, `SizeWidth`, `ScaleX`, ...). `color` (text) and `bg` map to `TextColor` and `BackgroundColor`; the TV may refuse to animate those (check `warnings`). |
| `from` | start value, set before the animation starts (optional; otherwise the current value) |
| `to` | end value |
| `keyframes` | `[[progress, value], ...]` with progress 0..1, instead of `from`/`to` |
| `ms` | length, default 1000 |
| `delay` | wait before starting, in ms. **With `loop` the delay repeats on every loop.** |
| `loop` | `true` = forever, or a number **above 1** = that many times. `-1`, `0` and `1` all play once. |
| `ease` | `linear`, `ease_in`, `ease_out`, `ease_in_out` (default), `ease_in_square`, `ease_out_square`, `ease_in_sine`, `ease_out_sine`, `ease_in_out_sine`, `bounce`, `sin`, `ease_out_back`, `reverse`. Underscores, dashes and case don't matter. Unknown names fall back to `ease_in_out`. |

- `x`/`y` are relative to the parent. To slide something in, put its final position in `x` and animate from an offset.
- `scale` takes a number. It scales around the center.
- There is no rotation. For spinning, use a `gif` or `lottie`.
- Don't animate `x`/`y` of children inside a `layout`, or `scale` of a button that has a `focus.scale`.

Examples:

```json
{"type": "box", "x": 40, "y": 40, "w": 24, "h": 24, "radius": 12, "bg": "#ff3b30",
 "animate": [{"prop": "opacity", "keyframes": [[0, 1], [0.5, 0.2], [1, 1]], "ms": 1000, "loop": true}]}
```

```json
{"type": "text", "x": 40, "y": 30, "w": 820, "text": "Arriving", "size": 34, "color": "#ffffff",
 "animate": [{"prop": "x", "from": 120, "to": 40, "ms": 500, "ease": "ease_out_back"},
             {"prop": "opacity", "from": 0, "to": 1, "ms": 400}]}
```

---

## 9. `/update`: change a message on screen

```json
{"id": "door", "nodes": {"status": {"text": "Unlocked", "color": "#30d158"}, "dot": {"visible": false}}, "duration": 5}
```

- `nodes` maps a node's `name` to properties to set. Any property from sections 4..6 works, including `animate`
  (adds new animations) and `call`.
- **Include `"type"` when changing `src`**: `{"cam": {"type": "image", "src": "..."}}`. Without it the change is
  ignored with `src ignored on`. Same for a `progress` bar's `color`: `{"bar": {"type": "progress", "color": "#ff0000"}}`.
- Not changeable in place: `children`, `layout`, `action`, `focus`. If a button's `focus` sets a key (e.g. `bg`),
  updating that key on the button is undone the next time the selection moves.
- **Structure, without redrawing the rest** (app 1.2.0+): swap, add or remove named subtrees. Only the swapped or
  removed parts stop their players, streams, timers and animations; live video elsewhere in the message keeps playing.
  ```json
  {"id": "dash:main",
   "replace": {"d.s0.c1": {"type": "box", "name": "d.s0.c1", "x": 0, "y": 0, "w": 400, "h": 120, "children": [...]}},
   "add": [{"parent": "d", "index": 3, "node": {"type": "box", "name": "d.c4", "...": "..."}}],
   "remove": ["d.c2"]}
  ```
  A replaced subtree takes the old one's place among its siblings. New subtrees go through the same media handling and
  video limits as `/notify`. To change everything, send `/notify` again with the same `id`.
- A `radius` image: its `name` refers to the clip box. `src` updates still reach the picture. Don't update `src` of a
  `refresh_ms` image.
- `duration` restarts the timer from now (`0` = no timer).
- 404 if the message is not on screen. Unknown names are warnings.

---

## 10. Sound

```json
"sound": {"src": "data:audio/wav;base64,...", "volume": 0.05}
```

| Key | Default | Meaning |
|---|---|---|
| `src` | | `data:` URI or allow-listed URL |
| `volume` | **1.0** | linear sample scale, clamped 0..1. **Always set it.** For full-scale sounds (chimes, tones), 0.05 is moderate and 0.3 is already loud. For a 0..1 loudness value, send `loudness³`. **Program audio** (speech and music normalized to -16 LUFS) needs **1.0** to match a video playing in another app; 0.35 was far too quiet. |
| `engine` | `"raw"` | `raw`: decoded in the app and **mixed over the playing video without interrupting it** (tested). `player`: Tizen media player, **stops the playing app's video**. Don't use it. |
| `stream` | `"notification"` | `player` engine only: `notification`, `media`, `alarm`, `system` |

- Formats (raw): WAV PCM 8- or 16-bit, or MP3. Mono or stereo, 8000..48000 Hz. Not ADPCM or float WAV.
- It plays as soon as the request arrives. Closing the message doesn't stop it.
- Only one sound plays at a time. Further sounds queue behind it.
- Keep sounds short.

---

## 11. Raw NUI access (`props`, `call`, `nui:`)

For features not listed here. These are applied by name, case-insensitive.

```json
{"type": "box", "props": {"ClippingMode": "ClipChildren"}}
{"type": "text", "text": "A long scrolling ticker line", "color": "#ffffff", "w": 600, "props": {"EnableAutoScroll": true, "AutoScrollLoopCount": 0}}
{"type": "nui:TextLabel", "props": {"Text": "raw", "PointSize": 30, "TextColor": "#ffffff"}}
{"type": "gif", "src": "data:image/gif;base64,...", "call": [{"method": "Stop", "args": []}]}
```

- `props`: any public writable property of the view. Value conversion: numbers, bools, strings, enums (by name,
  underscores ignored, or by number), colors (as in section 4), `Extents` (number or 4-list), `Size`/`Position`/`Vector2/3/4`/
  `Rectangle` (lists), `PropertyMap` (object).
- `call`: `{"method": "<name>", "args": [...]}`, matched by name and argument count.
- Errors are warnings with the reason.

---


---

## 12. Video relay and `/play`

The relay (`relay/` in the repository) runs in Docker on a machine with the media and, ideally, a GPU. It turns a source
into the synced stream the TV plays without its hardware decoder. The TV gets its address and token at build time
(`RELAY_URL`, `RELAY_TOKEN_FILE`); its host is then allowed automatically.

```
POST /play
{"src": "shows/Some Show/Season 1/episode.mkv", "start": 480, "volume": 1.0}
```

- Full screen by default. A window: `w` (and `h`, default 16:9), with `x`/`y` or `anchor`/`margin`.
- `opacity` (0..1); the video box is named `vid`, so `/update {"id": "play", "nodes": {"vid": {"opacity": 0.5}}}`
  changes it while it plays.
- `id` (default `play`), `volume`, `start`, `quality`, `bg` (behind the letterbox), `duration` (0 = until the end),
  `level`, `callback`, `data`. The message closes when the source ends (callback action `ended`).
- 503 when no relay is configured or it doesn't answer.

**Limits.** The TV caps relay video so it doesn't stutter:

| | Tizen 6 | Tizen 5 |
|---|---|---|
| streams at once | 6 | 4 |
| combined size of several streams | 1.25 Mpx | 0.75 Mpx |
| one stream alone | full screen | full screen |

- Video nodes over the stream limit use their `fallback`, or are left out, with a warning.
- When several streams would exceed the size budget, the new ones are requested smaller from the relay and scaled up into
  their boxes, with a warning.
- Streams of other messages count; a message's own earlier streams don't (it is being replaced). Dashboard camera cards
  count too.
- `GET /video` shows what is playing (`streams`, `mpx`) and the limits. `POST /config {"video": {"max_streams": 4,
  "budget_mpx": 0.75}}` changes them for this TV, and the change is kept.

Measured (2026-11-15; relay on an Intel N-series GPU): full screen 1920×800 at about 20–22 fps on all three test TVs
(two 2021 Frames on Tizen 6.0 and a 2019 TV on Tizen 5.0), with decoding near its limit; 1280 wide and smaller at 24 fps.
Six 560-wide streams with sound at about 24 fps on the Frames; on the Tizen 5 TV four ran cleanly and six stuttered.
Earlier on a Frame: one 1080p episode full screen at 21–24 fps;
six episodes at 560 px wide at 24 fps each, all with sound; a 15 fps camera in a dashboard card. The relay used about 7%
of a CPU core per stream; its GPU transcodes 1080p at about 13× real time.

---

## 13. Dashboards

`POST /dashboard {"dashboard": "<url_path>", "id": "main"}` shows a Home Assistant dashboard as one transparent message
and keeps it live. Built with `HA_URL` and `HA_TOKEN_FILE` (a **non-admin** user's token). The full description is in
`docs/superpowers/specs/2026-11-15-lovelace-overlay-design.md`. In short:

- Sections, masonry and panel views; view badges; `visibility` and `conditional` rules.
- Built-in cards: tile, entity, entities, glance, button, sensor (with graph), gauge, history-graph, statistics-graph,
  statistic, markdown (bold and italic), heading, todo-list, calendar (month grid, day and list views), weather-forecast,
  clock, picture, picture-entity, picture-glance, camera, media-control, thermostat, light, alarm-panel, area, logbook,
  horizontal/vertical stack, grid, conditional, entity-filter.
- Custom cards: layout-card (grid with named areas, masonry, horizontal, vertical; also as `custom:*-layout` views),
  auto-entities, week-planner-card, atomic-calendar-revive, calendar-card-pro, stack-in-card variants,
  weather-chart-card, mini-graph-card, clock-weather-card, Mushroom (template, entity, title, chips, person, light,
  lock, cover, climate, ...), better-moment-card, advanced-camera-card.
- Layout follows the frontend: sections have 12 grid columns per view column they span, content-sized cards take one
  row that grows to fit, and card default sizes match the frontend's. `tools/ha_screenshot.py` captures how Home
  Assistant itself draws a dashboard, for side-by-side checks with `tools/preview.py`.
- Camera cards play live through the relay (go2rtc's RTSP stream when the card names one, else Home Assistant's own
  stream for the entity) and fall back to snapshots.
- Read-only: taps do nothing and action-only cards are dropped. Unsupported cards are listed in `GET /dashboard`.
- Request options: `view` (index or path), `persist` (default true: comes back after an app restart), plus any of the
  settings below. A request only overrides the settings it actually contains.

### 13.1 Settings in the dashboard: `tv_overlay`

Everything about how a dashboard looks on the TV can live in the dashboard itself, so editing it in Home Assistant
changes the TV live, with no rebuild. Put a `tv_overlay` key at the top level of the dashboard's config (raw
configuration editor); a view may carry its own `tv_overlay` to override it; a card may carry
`tv_overlay: {conditional: <mode>}`.

```yaml
tv_overlay:
  theme: ambient            # default | lovelace | ambient
  scale: 2.33               # x, y, w, h, scale, opacity (whole overlay, 0..1), as_user, conditionals
  conditionals: reserve     # instant | reserve | crossfade | slide
  ambient:                  # parameters of a theme, under its name
    origin: bottom-left
views:
  - title: Evening
    tv_overlay: {ambient: {origin: top-right}}
    sections: [...]
```

- **Precedence**, lowest first: the theme's defaults, the dashboard's `tv_overlay`, the shown view's `tv_overlay`, the
  `/dashboard` request. Nested objects merge key by key. The request's `theme` object still works:
  `{"theme": {"name": "lovelace", "card_opacity": 0.8}}` picks the theme and sets its parameters.
- Bad values are listed under `warnings` in `GET /dashboard` and ignored, never fatal.
- Home Assistant keeps the key through raw-editor saves (checked on all three levels). The visual editor was not
  checked; if a visual edit drops a key, put it back in the raw editor.
- Palette parameters work in every theme: `card_bg`, `text`, `secondary_text`, `accent`, `radius`, `card_opacity`.

### 13.2 Themes

- **`default`**: translucent cards, the TV's system font, the frontend's layout.
- **`lovelace`**: Home Assistant's own dark theme: opaque `#1c1c1c` cards, `#e1e1e1` / `#9b9b9b` text, Roboto, HA's
  current calendar colors (ended events at half strength), idle thermostat rings dimmed. Same layout as `default`.
  Parameter `background` (default `false`): `true` draws HA's `#111111` view background behind the cards.
- **`ambient`**: a quiet theme for sitting over art. The view's structure is ignored: every card, in config order
  (badges first, then section cards by section, then view cards), flows in fixed-width columns from a corner. Each
  column fills from the corner's edge toward the opposite one, then the next column steps sideways away from the
  corner. Strictly in order: a card that doesn't fit the rest of a column starts the next one, and earlier columns are
  never backfilled. A card taller than the screen gets its own column and is clipped; cards past the last column are
  listed as offscreen. Every color becomes one foreground color at the original color's lightness (CIE L*); all text is
  drawn at one opacity, at least `min_weight`, with an outline and a shadow; text on a filled shape (event bars, the
  today circle) takes `knockout_color`; card-colored surfaces become tiles. Photos, camera views and video are never
  recolored. Put conditional cards **last** in the view: a hidden card at the end gives its space back, one in the
  middle leaves a gap (`reserve`).

The defaults were set on a Frame over Art Mode in the evening, when the panel is dim: the smallest card text comes out
at 28 px on the 1080 layer (`scale` 2.33), on a light frosted tile, in white with a thin outline and a soft shadow.

| ambient parameter | default | meaning |
|---|---|---|
| `origin` | `bottom-left` | `top-left`, `top-right`, `bottom-left`, `bottom-right` |
| `color` | `#ffffff` | the one foreground color |
| `tile_color` | `#ffffff` | tile background color |
| `tile_opacity` | `0.18` | tile background opacity |
| `column_width` | `340` | column width (CSS px, before `scale`) |
| `gap` | `8` | between tiles and between columns |
| `margin` | `24` | from the screen edges |
| `radius` | `14` | tile corners |
| `font` | `Inter` | family for every text |
| `min_value` | `0.7` | how faint the darkest color gets (0..1) |
| `recolor` | `alpha` | `alpha` (foreground at a share of opacity) or `ramp` (mixed from the tile color) |
| `text_opacity` | `1` | opacity of all text |
| `min_weight` | `medium` | `light`, `regular`, `medium`, `bold`: lighter text is drawn at this weight |
| `outline` | `1` | text outline width in TV px (0 = none) |
| `outline_color` | `#00000099` | |
| `shadow` | `4` | text shadow blur in TV px (0 = none) |
| `shadow_color` | `#000000b3` | |
| `shadow_x`, `shadow_y` | `2`, `2` | shadow offset in TV px |
| `knockout_color` | `#000000` | text on filled shapes |
| `fill_opacity` | `0.45` | translucent fills (a graph's shaded area) are at least this opaque |

ambient's default `scale` is 2.33 (default and lovelace: 1.0).

Text nodes accept `outline` (`{"color": "#00000099", "width": 1}`, TV px; app 1.2.0+) as well as `text_shadow`.

Fonts: Inter and Roboto ship with the app (static Light/Regular/Medium/Bold cuts, SIL Open Font License 1.1, in
`src/res/fonts`). Any text node can use them with `"font": "Inter"` or `"font": "Roboto"`.

### 13.3 Conditional cards

How a card with `visibility` rules, or a `conditional` card, comes and goes. The mode is set by `conditionals`
(dashboard, view, request) or per card with `tv_overlay: {conditional: <mode>}`. Default: `instant` for `default` and
`lovelace`, `reserve` for `ambient`.

- `instant`: hidden cards take no space; the layout redraws when one changes.
- `reserve`: a hidden card keeps its place as an empty slot, so nothing else moves. It is measured without contacting
  anything (a hidden camera card asks for no stream). A hidden card after the last shown one gives its space back.
  Cards fade out (400 ms) before they go and fade in when they come back.
- `crossfade`: hidden cards take no space; when one changes, the whole overlay fades out (300 ms) and back in.
- `slide`: hidden cards take no space; a card fades out, the others glide to their new places (500 ms), and a card
  that comes back fades in once they have moved.

Only the cards that changed are redrawn (`/update` `replace`), so live cameras in other cards keep playing.

## 14. Tizen 5 differences

The same request works on both builds; unsupported parts become warnings.

| Feature | Tizen 5 |
|---|---|
| `radius` (boxes, images, progress) | square corners |
| `shadow`, `text_shadow` | not drawn |
| `lottie` | empty box |
| `layout` row/column | manual placement; every child needs `w` and `h` |
| `gif` | works (plain image view) |
| `level` | ignored |
| windows | one shared full-screen host; content outside a message's `w`×`h` is not clipped |

---

## 15. Full showcase (every safe feature in one message)

Gradient card, rounded live camera view, GIF, markup, a pulsing dot, buttons, a timeout bar, sound and a callback.
Replace the `data:` placeholders with real base64 and the hosts with allow-listed ones.

```json
{
  "id": "showcase", "duration": 0, "timeout": 45, "w": 1000, "h": 330, "anchor": "top-right", "margin": 50,
  "enter": {"type": "slide", "from": "right", "ms": 450}, "exit": {"type": "fade", "ms": 300},
  "sound": {"src": "data:audio/wav;base64,REPLACE", "volume": 0.05},
  "callback": "http://homeassistant.local:8123/api/webhook/tv_showcase", "data": {"source": "showcase"},
  "root": {"type": "box", "x": 10, "y": 10, "w": 980, "h": 310, "radius": 28,
    "gradient": {"colors": ["#1b3a6bf5", "#0d0d12f5"], "angle": 0},
    "children": [
      {"type": "image", "name": "cam", "src": "http://camera.local/snapshot.jpg", "refresh_ms": 500,
       "x": 20, "y": 20, "w": 340, "h": 190, "radius": 16, "fit": "cover"},
      {"type": "box", "x": 36, "y": 36, "w": 16, "h": 16, "radius": 8, "bg": "#ff3b30",
       "animate": [{"prop": "opacity", "keyframes": [[0, 1], [0.5, 0.2], [1, 1]], "ms": 1000, "loop": true}]},
      {"type": "gif", "src": "data:image/gif;base64,REPLACE", "x": 390, "y": 24, "w": 64, "h": 64},
      {"type": "text", "x": 474, "y": 30, "w": 480, "text": "Front door", "size": 34, "bold": true, "color": "#ffffff",
       "animate": [{"prop": "opacity", "from": 0, "to": 1, "ms": 500, "delay": 150}]},
      {"type": "text", "name": "status", "x": 390, "y": 104, "w": 564, "h": 90, "multiline": true, "ellipsis": true,
       "markup": true, "size": 28, "color": "#cccccc",
       "text": "Motion at <color value='#73bfff'>the porch</color>. Someone is <color value='#ffcc66'>waiting</color>."},
      {"type": "box", "action": "unlock", "x": 40, "y": 220, "w": 280, "h": 60, "radius": 30, "bg": "#2a2a33",
       "focus": {"bg": "#2f7fd6", "scale": 1.06},
       "children": [{"type": "text", "x": 0, "y": 0, "w": 280, "h": 60, "text": "Unlock", "size": 26, "color": "#ffffff", "align": "center", "valign": "center"}]},
      {"type": "box", "action": "talk", "x": 350, "y": 220, "w": 280, "h": 60, "radius": 30, "bg": "#2a2a33",
       "focus": {"bg": "#2f7fd6", "scale": 1.06},
       "children": [{"type": "text", "x": 0, "y": 0, "w": 280, "h": 60, "text": "Talk", "size": 26, "color": "#ffffff", "align": "center", "valign": "center"}]},
      {"type": "box", "action": "ignore", "x": 660, "y": 220, "w": 280, "h": 60, "radius": 30, "bg": "#2a2a33",
       "focus": {"bg": "#2f7fd6", "scale": 1.06},
       "children": [{"type": "text", "x": 0, "y": 0, "w": 280, "h": 60, "text": "Ignore", "size": 26, "color": "#ffffff", "align": "center", "valign": "center"}]},
      {"type": "progress", "x": 40, "y": 292, "w": 900, "h": 6, "radius": 3, "bg": "#ffffff22", "color": "#73bfff", "ms": 45000}
    ]}
}
```

Notes on this layout: the card is inset 10 px inside a 1000×330 window so its edges aren't clipped. `duration: 0` +
`timeout: 45` keeps it up for 45 s unless a button or Back closes it. The bar's `ms` matches the timeout. Then:

```json
{"id": "showcase", "nodes": {"status": {"text": "Door <color value='#30d158'>unlocked</color>"}}}
```

---

## 16. Checklist before sending

- [ ] `root` has `bg` or `gradient`; every `text` has `color`.
- [ ] All numbers and booleans are unquoted.
- [ ] Every node that holds content fits inside its parent; the card fits inside the window with room for scaling.
- [ ] Every centered label has `w`/`h` equal to its box and `align`/`valign` center.
- [ ] Every button has `bg` and a `focus` with a different `bg` (or is a text node with `focus.color`).
- [ ] Every URL (media and `callback`) is on the allow list; Lottie data is `application/json`.
- [ ] `sound.volume` is set (0.02..0.2).
- [ ] No `video` unless you accept the app underneath going black.
- [ ] After sending: response `ok` is `true` and `warnings` is empty or understood.
