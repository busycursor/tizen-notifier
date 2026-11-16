// Builds NUI views from request nodes and applies friendly + raw (reflection) properties.
// Optional view types live in their own non-inlined methods so a missing assembly only fails that node.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace TizenNotifier
{
    internal static class Build
    {
        public static Size Sz(float w, float h) => new Size(w, h, 0);
        public static Position Ps(float x, float y) => new Position(x, y, 0);
        public static View Node(Note n, Dictionary<string, object> spec, int pw, int ph, bool parentLayout, List<object> warn)
        {
            if (spec == null) return new View();
            string type = (Json.S(spec, "type") ?? "box").ToLowerInvariant();
            if (type == "image" && spec.ContainsKey("radius"))
            {
                // CornerRadius on an ImageView blanks the image on Tizen 6.0: clip it inside a rounded box instead
                var wrap = new Dictionary<string, object>(spec);
                wrap["type"] = "box"; wrap.Remove("src"); wrap.Remove("fit");
                var props = wrap.TryGetValue("props", out var pp) && pp is Dictionary<string, object> pd ? new Dictionary<string, object>(pd) : new Dictionary<string, object>();
                props["ClippingMode"] = "ClipChildren"; wrap["props"] = props;
                var inner = new Dictionary<string, object> { ["type"] = "image", ["x"] = 0.0, ["y"] = 0.0 };
                foreach (var k in new[] { "src", "fit", "w", "h", "refresh_ms", "__refresh_url" }) if (spec.ContainsKey(k)) inner[k] = spec[k];
                wrap.Remove("refresh_ms"); wrap.Remove("__refresh_url");
                wrap["children"] = new List<object> { inner };
                wrap["__image_wrap"] = true;
                return Node(n, wrap, pw, ph, parentLayout, warn);
            }
            if (type == "mjpeg") return Mjpeg.Create(n, spec, pw, ph, parentLayout, warn);
            if (type == "image" && spec.ContainsKey("refresh_ms") && spec.ContainsKey("__refresh_url"))
                return RefreshImage(n, spec, pw, ph, parentLayout, warn);
            View v;
            try
            {
                switch (type)
                {
                    case "box": v = new View(); break;
                    case "text": v = new TextLabel(); break;
                    case "image": v = new ImageView(); break;
                    case "gif": v = MakeGif(); break;
                    case "lottie": v = MakeLottie(); n.AddKeep(v, v); break;   // stopped before the note is torn down (Notes.StopKeep)
                    case "video":
                        // VideoView takes the TV's video plane: the playing app goes black and does not recover (Frame, Tizen 6.0, 2026-11-14)
                        if (!Json.B(n.Req, "takeover", false)) { warn.Add("video node skipped: it blanks the app underneath; send \"takeover\": true to allow it"); v = new View(); spec = new Dictionary<string, object>(spec); spec.Remove("src"); spec["type"] = "box"; type = "box"; break; }
                        v = MakeVideo(); break;
                    case "progress": v = new View(); break;
                    default:
                        if (type.StartsWith("nui:")) v = MakeNui(Json.S(spec, "type").Substring(4));
                        else { warn.Add("unknown node type " + type + "; using box"); v = new View(); }
                        break;
                }
            }
            catch (Exception e)
            {
                warn.Add(type + " unavailable: " + e.GetType().Name + " " + e.Message);
                v = new View();
            }
            v.BackgroundColor = Color.Transparent;
            string name = Json.S(spec, "name");
            if (name != null) n.Named[name] = v;

            // geometry
            float x = (float)Json.N(spec, "x", 0), y = (float)Json.N(spec, "y", 0);
            object ow = Json.Get(spec, "w"), oh = Json.Get(spec, "h");
            int w = ow is double dw ? (int)dw : (parentLayout ? -1 : Math.Max(1, pw - (int)x));
            int h = oh is double dh ? (int)dh : (parentLayout ? -1 : Math.Max(1, ph - (int)y));
#if !TIZEN5
            if (parentLayout)
            {
                try
                {
                    v.WidthSpecification = ow is double ? (int)(double)ow : (ow as string) == "fill" ? LayoutParamPolicies.MatchParent : LayoutParamPolicies.WrapContent;
                    v.HeightSpecification = oh is double ? (int)(double)oh : (oh as string) == "fill" ? LayoutParamPolicies.MatchParent : LayoutParamPolicies.WrapContent;
                }
                catch (Exception e) { warn.Add("layout size: " + e.Message); }
            }
            else
#endif
            {
                v.Position = Ps(x, y);
                if (type == "text" && oh == null)
                {
                    v.Size = Sz(w, 0);
                    try { v.WidthResizePolicy = ResizePolicyType.Fixed; v.HeightResizePolicy = ResizePolicyType.DimensionDependency; } catch (Exception e) { warn.Add("text height: " + e.Message); }
                }
                else v.Size = Sz(w, h);
            }

            Apply(n, v, spec, warn, false);

            // layout + children
            string lay = Json.S(spec, "layout");
            bool hasLayout = false;
#if !TIZEN5
            if (lay == "row" || lay == "column")
            {
                try { SetLayout(v, lay, (int)Json.N(spec, "gap", 0)); hasLayout = true; }
                catch (Exception e) { warn.Add("layout: " + e.Message); }
            }
#endif
            if (type == "progress") Progress(n, v, spec, w, h, warn);
            var kids = Json.A(spec, "children");
            if (kids != null)
            {
#if TIZEN5
                // no LinearLayout in API5: place children one after another (they need explicit w/h)
                float cur = 0; int gap = (int)Json.N(spec, "gap", 0);
                bool manual = lay == "row" || lay == "column";
                if (manual) { var pad = Json.Get(spec, "padding"); if (pad != null) cur = lay == "row" ? Ext(pad).Start : Ext(pad).Top; }
#endif
                foreach (var c in kids)
                    if (c is Dictionary<string, object> cs)
                    {
                        var cv = Node(n, cs, Math.Max(1, w), Math.Max(1, h), hasLayout, warn);
#if TIZEN5
                        if (manual)
                        {
                            if (lay == "row") { cv.PositionX = cur; cur += cv.SizeWidth + gap; }
                            else { cv.PositionY = cur; cur += cv.SizeHeight + gap; }
                        }
#endif
                        v.Add(cv);
                    }
            }

            // buttons
            string action = Json.S(spec, "action");
            if (action != null)
            {
                var focus = Json.O(spec, "focus") ?? new Dictionary<string, object> { ["scale"] = 1.08 };
                var saved = new Dictionary<string, object>();
                foreach (var k in focus.Keys)
                    saved[k] = spec.TryGetValue(k, out var orig) ? orig : k == "scale" || k == "opacity" ? (object)1.0 : k == "bg" ? "transparent" : k == "color" ? "#ffffff" : null;
                try { v.PivotPoint = PivotPoint.Center; v.PositionUsesPivotPoint = false; } catch (Exception) { }
                n.Buttons.Add((v, action, focus, saved));
            }
            return v;
        }

        // Friendly properties. styleOnly = focus/unfocus styling (no animations/calls).
        public static void Apply(Note n, View v, Dictionary<string, object> s, List<object> warn, bool styleOnly)
        {
            foreach (var kv in s)
            {
                try { ApplyOne(n, v, kv.Key, kv.Value, s, warn, styleOnly); }
                catch (Exception e) { warn.Add(kv.Key + ": " + (e.InnerException ?? e).Message); }
            }
        }

        private static void ApplyOne(Note n, View v, string k, object val, Dictionary<string, object> s, List<object> warn, bool styleOnly)
        {
            var t = v as TextLabel;
            switch (k)
            {
                case "type": case "name": case "children": case "action": case "focus": case "layout": case "gap": case "ms": case "reverse": case "__image_wrap": case "refresh_ms": case "__refresh_url": return;
                case "x": if (val is double) v.PositionX = (float)(double)val; return;
                case "y": if (val is double) v.PositionY = (float)(double)val; return;
                case "w": if (val is double) v.SizeWidth = (float)(double)val; return;
                case "h": if (val is double) v.SizeHeight = (float)(double)val; return;
                case "bg": v.BackgroundColor = Col(val); return;
                case "radius":
#if TIZEN5
                    warn.Add("radius not supported on Tizen 5 (square corners)"); return;
#else
                    if (v is ImageView) { warn.Add("radius on an image is applied by a clipping box"); return; } v.CornerRadius = F(val); return;
#endif
                case "opacity": v.Opacity = F(val); return;
                case "visible": if (Bool(val)) v.Show(); else v.Hide(); return;
                case "scale":
                    v.PivotPoint = PivotPoint.Center; v.PositionUsesPivotPoint = false;
                    v.Scale = val is List<object> sl ? new Vector3(F(sl[0]), F(sl[1]), 1f) : new Vector3(F(val), F(val), 1f); return;
                case "padding": v.Padding = Ext(val); return;
                case "gradient": SetGradient(v, val as Dictionary<string, object>, s); return;
                case "shadow": SetShadow(v, val as Dictionary<string, object>); return;
                case "fit": return; // handled with src
                case "src": SetSrc(v, Json.S(s, "type"), val as string, s, warn); return;
                case "loop": case "mute": case "underlay": return; // handled with src
                case "color":
                    if (t != null) t.TextColor = Col(val);
                    else if (Json.S(s, "type") == "progress") { var f = FindChild(v, "progress-fill"); if (f != null) f.BackgroundColor = Col(val); }
                    return;
                case "animate":
                    if (styleOnly) return;
                    if (val is List<object> al) foreach (var a in al) if (a is Dictionary<string, object> ad) Animate(n, v, ad, warn);
                    return;
                case "props":
                    if (val is Dictionary<string, object> pd) foreach (var p in pd) Reflect.Set(v, p.Key, p.Value);
                    return;
                case "call":
                    if (styleOnly) return;
                    if (val is List<object> cl) foreach (var c in cl) if (c is Dictionary<string, object> cd) Reflect.Call(v, Json.S(cd, "method"), Json.A(cd, "args"));
                    return;
            }
            if (t == null) { warn.Add("unknown property " + k); return; }
            switch (k)
            {
                case "text":
                    string tx = val as string ?? Convert.ToString(val, CultureInfo.InvariantCulture);
                    t.Text = Json.B(s, "markup", false) || t.EnableMarkup ? Markup(tx) : tx; return;
                case "size": t.PointSize = F(val); return;
                case "pixel_size": t.PixelSize = F(val); return;
                case "font": t.FontFamily = val as string; return;
                case "bold": if (Bool(val)) t.FontStyle = new PropertyMap().Add("weight", new PropertyValue("bold")); return;
                case "weight": t.FontStyle = new PropertyMap().Add("weight", new PropertyValue(val as string)); return;
                case "align":
                    var a = (val as string ?? "").ToLowerInvariant();
                    t.HorizontalAlignment = a == "center" ? HorizontalAlignment.Center : a == "right" || a == "end" ? HorizontalAlignment.End : HorizontalAlignment.Begin; return;
                case "valign":
                    var va = (val as string ?? "").ToLowerInvariant();
                    t.VerticalAlignment = va == "center" || va == "middle" ? VerticalAlignment.Center : va == "bottom" ? VerticalAlignment.Bottom : VerticalAlignment.Top; return;
                case "multiline": t.MultiLine = Bool(val); return;
                case "ellipsis": t.Ellipsis = Bool(val); return;
                case "markup": t.EnableMarkup = Bool(val); return;
                case "line_spacing": t.LineSpacing = F(val); return;
                case "outline":
                {
                    // {"color": "#000000", "width": 2}: an outline around every glyph (TV px)
                    var ol = val as Dictionary<string, object>;
                    var pm = new PropertyMap();
                    pm.Add("color", new PropertyValue(Col(Json.Get(ol, "color") ?? "#000000")));
                    pm.Add("width", new PropertyValue((float)Json.N(ol, "width", 2)));
                    try { t.Outline = pm; } catch (Exception e) { warn.Add("outline: " + e.Message); }
                    return;
                }
                case "text_shadow":
#if TIZEN5
                    warn.Add("text_shadow not supported on Tizen 5"); return;
#else
                    var ts = val as Dictionary<string, object>;
                    t.TextShadow = new TextShadow(Col(Json.Get(ts, "color") ?? "#000000"), new Vector2((float)Json.N(ts, "x", 2), (float)Json.N(ts, "y", 2)), (float)Json.N(ts, "blur", 2)); return;
#endif
            }
            warn.Add("unknown property " + k);
        }

        // Tizen 6.0 markup needs single-quoted attribute values and reads 8-digit colors as #AARRGGBB.
        private static readonly System.Text.RegularExpressions.Regex AttrDq = new System.Text.RegularExpressions.Regex("(\\w+)\\s*=\\s*\"([^\"]*)\"");
        private static readonly System.Text.RegularExpressions.Regex Hex8 = new System.Text.RegularExpressions.Regex("='#([0-9a-fA-F]{6})([0-9a-fA-F]{2})'");
        public static string Markup(string t)
        {
            if (t == null || t.IndexOf('<') < 0) return t;
            t = AttrDq.Replace(t, "$1='$2'");
            return Hex8.Replace(t, "='#$2$1'");
        }

        private static View FindChild(View v, string name)
        {
            for (uint i = 0; i < v.ChildCount; i++) { var c = v.GetChildAt(i); if (c.Name == name) return c; }
            return null;
        }

        private static void Progress(Note n, View v, Dictionary<string, object> s, int w, int h, List<object> warn)
        {
            bool reverse = Json.B(s, "reverse", false);
            var fill = new View { Name = "progress-fill", Position = Ps(0, 0), Size = Sz(reverse ? 0 : w, h), BackgroundColor = Col(Json.Get(s, "color") ?? "#4aa3ff") };
#if !TIZEN5
            try { fill.CornerRadius = F(Json.Get(s, "radius") ?? 0.0); } catch (Exception) { }
#endif
            v.Add(fill);
            double ms = Json.N(s, "ms", Json.N(n.Req, "duration", 10) * 1000);
            if (ms <= 0) return;
            var anim = new Animation((int)ms);
            anim.AnimateTo(fill, "SizeWidth", reverse ? (float)w : 0f, new AlphaFunction(AlphaFunction.BuiltinFunctions.Linear));
            n.AddKeep(anim, v);
            anim.Play();
        }

        private static readonly Dictionary<string, string> Friendly = new Dictionary<string, string>
        { ["opacity"] = "Opacity", ["x"] = "PositionX", ["y"] = "PositionY", ["w"] = "SizeWidth", ["h"] = "SizeHeight", ["scale"] = "Scale", ["color"] = "TextColor", ["bg"] = "BackgroundColor" };

        private static void Animate(Note n, View v, Dictionary<string, object> a, List<object> warn)
        {
            string p = Json.S(a, "prop") ?? "opacity";
            string prop = Friendly.TryGetValue(p, out var fp) ? fp : p;
            int ms = Math.Max(1, (int)Json.N(a, "ms", 1000)), delay = Math.Max(0, (int)Json.N(a, "delay", 0));
            var pi = v.GetType().GetProperty(prop, BindingFlags.Public | BindingFlags.Instance);
            Type pt = pi?.PropertyType ?? typeof(float);
            if (prop == "Scale") { v.PivotPoint = PivotPoint.Center; v.PositionUsesPivotPoint = false; }
            object Conv(object j) => prop == "Scale" && j is double d ? new Vector3((float)d, (float)d, 1f) : Reflect.Convert(pt, j);
            var anim = new Animation(delay + ms);
            var ease = Ease(Json.S(a, "ease"));
            var kf = Json.A(a, "keyframes");
            if (kf != null)
            {
                var frames = new KeyFrames();
                foreach (var f in kf) if (f is List<object> fl && fl.Count >= 2) frames.Add(F(fl[0]), ToPV(Conv(fl[1])));
                anim.AnimateBetween(v, prop, frames, delay, delay + ms, Animation.Interpolation.Linear, ease);
            }
            else
            {
                if (Json.Has(a, "from") && pi != null && pi.CanWrite) pi.SetValue(v, Conv(Json.Get(a, "from")));
                anim.AnimateTo(v, prop, Conv(Json.Get(a, "to")), delay, delay + ms, ease);
            }
            var loop = Json.Get(a, "loop");
            if (loop is bool lb && lb) anim.Looping = true;
            else if (loop is double ld && ld > 1) anim.LoopCount = (int)ld;
            n.AddKeep(anim, v);
            anim.Play();
        }

        private static PropertyValue ToPV(object o)
        {
            switch (o)
            {
                case float f: return new PropertyValue(f);
                case Vector2 v2: return new PropertyValue(v2);
                case Vector3 v3: return new PropertyValue(v3);
                case Vector4 v4: return new PropertyValue(v4);
                case Color c: return new PropertyValue(new Vector4(c.R, c.G, c.B, c.A));
                case Position p: return new PropertyValue(p);
                case Size s: return new PropertyValue(new Vector2(s.Width, s.Height));
                case int i: return new PropertyValue(i);
                case bool b: return new PropertyValue(b);
            }
            throw new ArgumentException("cannot animate value " + o);
        }

        private static AlphaFunction Ease(string e)
        {
            if (string.IsNullOrEmpty(e)) return new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseInOut);
            string k = e.Replace("_", "").Replace("-", "");
            foreach (AlphaFunction.BuiltinFunctions b in Enum.GetValues(typeof(AlphaFunction.BuiltinFunctions)))
                if (string.Equals(b.ToString(), k, StringComparison.OrdinalIgnoreCase)) return new AlphaFunction(b);
            return new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseInOut);
        }

        private static void SetGradient(View v, Dictionary<string, object> g, Dictionary<string, object> s)
        {
            var cols = Json.A(g, "colors");
            if (cols == null || cols.Count < 2) throw new ArgumentException("gradient needs 2+ colors");
            var stops = Json.A(g, "stops");
            double ang = Json.N(g, "angle", 90) * Math.PI / 180.0;   // 0 = left->right, 90 = top->bottom
            float cx = (float)(Math.Cos(ang) * 0.5), cy = (float)(Math.Sin(ang) * 0.5);
            var off = new PropertyArray(); var col = new PropertyArray();
            for (int i = 0; i < cols.Count; i++)
            {
                off.Add(new PropertyValue(stops != null && i < stops.Count ? F(stops[i]) : (float)i / (cols.Count - 1)));
                var c = Col(cols[i]);
                col.Add(new PropertyValue(new Vector4(c.R, c.G, c.B, c.A)));
            }
            var map = new PropertyMap()
                .Add(Visual.Property.Type, new PropertyValue((int)Visual.Type.Gradient))
                .Add(GradientVisualProperty.StartPosition, new PropertyValue(new Vector2(-cx, -cy)))
                .Add(GradientVisualProperty.EndPosition, new PropertyValue(new Vector2(cx, cy)))
                .Add(GradientVisualProperty.StopOffset, new PropertyValue(off))
                .Add(GradientVisualProperty.StopColor, new PropertyValue(col));
#if !TIZEN5
            if (s.ContainsKey("radius")) map.Add(Visual.Property.CornerRadius, new PropertyValue(F(s["radius"])));
#endif
            v.Background = map;
        }

        private static void SetShadow(View v, Dictionary<string, object> sh)
        {
#if TIZEN5
            throw new NotSupportedException("box shadow not supported on Tizen 5");
#else
            v.BoxShadow = new Shadow((float)Json.N(sh, "blur", 20), new Vector2((float)Json.N(sh, "x", 0), (float)Json.N(sh, "y", 6)),
                Col(Json.Get(sh, "color") ?? "#000000aa"), new Vector2((float)Json.N(sh, "spread", 0), (float)Json.N(sh, "spread", 0)));
#endif
        }

        private static void SetSrc(View v, string type, string src, Dictionary<string, object> s, List<object> warn)
        {
            if (src == null) return;
            if (!(v is ImageView) && v.ChildCount > 0 && v.GetChildAt(0) is ImageView inner && (type == null || type == "image" || type == "box")) { v = inner; type = "image"; }
            switch ((type ?? "").ToLowerInvariant())
            {
                case "image": SetImage((ImageView)v, src, s); return;
            }
            SetSrcOther(v, type, src, s, warn);
        }

        public static void SetImage(ImageView iv, string src, Dictionary<string, object> s)
        {
            {
                {
                    string fit = (Json.S(s, "fit") ?? "contain").ToLowerInvariant();
                    var map = new PropertyMap().Add(Visual.Property.Type, new PropertyValue((int)Visual.Type.Image)).Add(ImageVisualProperty.URL, new PropertyValue(src));
                    if (fit != "fill")
                    {
                        var fm = fit == "cover" ? FittingModeType.ScaleToFill : fit == "width" ? FittingModeType.FitWidth : fit == "height" ? FittingModeType.FitHeight : FittingModeType.ShrinkToFit;
                        map.Add(ImageVisualProperty.FittingMode, new PropertyValue((int)fm));
                    }
                    iv.Image = map;
                }
            }
        }

        private static void SetSrcOther(View v, string type, string src, Dictionary<string, object> s, List<object> warn)
        {
            switch ((type ?? "").ToLowerInvariant())
            {
                case "gif": SetGif(v, src); return;
                case "lottie": SetLottie(v, src, (int)Json.N(s, "loop", -1)); return;
                case "video": SetVideo(v, src, Json.B(s, "loop", true), Json.B(s, "mute", true), Json.B(s, "underlay", true)); return;
            }
            warn.Add("src ignored on " + type);
        }

        // An image that re-downloads its URL every refresh_ms (min 200). Two layers: the next frame loads hidden and is
        // only swapped in once ready, so there is no flicker. No media player involved.
        private static View RefreshImage(Note n, Dictionary<string, object> spec, int pw, int ph, bool parentLayout, List<object> warn)
        {
            var box = new Dictionary<string, object>(spec);
            box["type"] = "box"; box.Remove("src"); box.Remove("fit"); box.Remove("refresh_ms"); box.Remove("__refresh_url");
            var c = Node(n, box, pw, ph, parentLayout, warn);
            int w = (int)c.SizeWidth, h = (int)c.SizeHeight;
            var layers = new[] { new ImageView { Size = Sz(w, h), Position = Ps(0, 0) }, new ImageView { Size = Sz(w, h), Position = Ps(0, 0) } };
            c.Add(layers[0]); c.Add(layers[1]); layers[1].Hide();
            string first = Json.S(spec, "src");
            SetImage(layers[0], first, spec);
            var files = new string[] { first, null };
            int cur = 0, pending = -1;
            for (int i = 0; i < 2; i++)
            {
                int idx = i;
                layers[i].ResourceReady += (s, e) =>
                {
                    if (idx != pending || n.Closing) return;
                    layers[idx].Show(); layers[1 - idx].Hide();
                    string old = files[1 - idx]; files[1 - idx] = null;
                    if (old != null) { n.Files.Remove(old); Media.Delete(new List<string> { old }); }
                    cur = idx; pending = -1;
                };
            }
            string url = Json.S(spec, "__refresh_url");
            int ms = Math.Max(200, (int)Json.N(spec, "refresh_ms", 500));
            int fails = 0;
            var stop = new Stop();
            n.AddKeep(stop, c);   // replaced or removed with its subtree (or the note's content): the loop ends
            System.Threading.Tasks.Task.Run(async () =>
            {
                while (!n.Closing && !stop.Stopped)
                {
                    await System.Threading.Tasks.Task.Delay(ms);
                    if (n.Closing || stop.Stopped) break;
                    string f;
                    try { f = Media.FetchFile(url); fails = 0; }
                    catch (Exception e) { if (fails++ == 0) Log.W("refresh fetch failed: " + e.Message); continue; }
                    var done = new System.Threading.ManualResetEventSlim();
                    App.Post(() =>
                    {
                        try
                        {
                            if (n.Closing) { Media.Delete(new List<string> { f }); return; }
                            int next = 1 - cur;
                            if (pending >= 0 && files[pending] != null) { n.Files.Remove(files[pending]); Media.Delete(new List<string> { files[pending] }); }
                            files[next] = f; n.Files.Add(f); pending = next;
                            SetImage(layers[next], f, spec);
                        }
                        catch (Exception e) { Log.W("refresh swap failed: " + e.Message); }
                        finally { done.Set(); }
                    });
                    done.Wait(5000);
                }
            });
            return c;
        }

#if !TIZEN5
        private static void SetLayout(View v, string lay, int gap)
        {
            v.Layout = new LinearLayout
            {
                LinearOrientation = lay == "row" ? LinearLayout.Orientation.Horizontal : LinearLayout.Orientation.Vertical,
                CellPadding = new Size2D(gap, gap),
            };
        }
#endif

#if TIZEN5
        // API5: no AnimatedImageView; DALi's image visual plays .gif files itself. No Lottie.
        [MethodImpl(MethodImplOptions.NoInlining)] private static View MakeGif() => new ImageView();
        [MethodImpl(MethodImplOptions.NoInlining)] private static View MakeLottie() => throw new NotSupportedException("Lottie not supported on Tizen 5");
        [MethodImpl(MethodImplOptions.NoInlining)] private static void SetGif(View v, string src) { ((ImageView)v).ResourceUrl = src; }
        [MethodImpl(MethodImplOptions.NoInlining)] private static void SetLottie(View v, string src, int loop) { throw new NotSupportedException("Lottie not supported on Tizen 5"); }
#else
        [MethodImpl(MethodImplOptions.NoInlining)] private static View MakeGif() => new AnimatedImageView();
        [MethodImpl(MethodImplOptions.NoInlining)] private static View MakeLottie() => new LottieAnimationView();
        [MethodImpl(MethodImplOptions.NoInlining)] private static void SetGif(View v, string src) { var g = (AnimatedImageView)v; g.ResourceUrl = src; g.Play(); }
        [MethodImpl(MethodImplOptions.NoInlining)] private static void SetLottie(View v, string src, int loop) { var l = (LottieAnimationView)v; l.URL = src; l.LoopCount = loop; l.Play(); }
#endif
        // ends a background loop that belongs to a view (Notes.StopKeep disposes it)
        internal sealed class Stop : IDisposable { public volatile bool Stopped; public void Dispose() => Stopped = true; }

        // A Lottie view still playing when its window is disposed killed the app on Tizen 6.0 (2026-11-15):
        // stop it and take it off the tree first.
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool StopMedia(object k)
        {
#if !TIZEN5
            if (k is LottieAnimationView l)
            {
                try { l.Stop(); } catch (Exception) { }
                try { l.Unparent(); } catch (Exception) { }
                l.Dispose();
                return true;
            }
#endif
            return false;
        }

        [MethodImpl(MethodImplOptions.NoInlining)] private static View MakeVideo() => new VideoView();
        // underlay false = draw the video as a texture in our window instead of the TV's video plane; set before the URL.
        [MethodImpl(MethodImplOptions.NoInlining)] private static void SetVideo(View v, string src, bool loop, bool mute, bool underlay) { var vv = (VideoView)v; if (!underlay) { try { Reflect.Set(vv, "Underlay", false); } catch (Exception) { } } vv.ResourceUrl = src; vv.Looping = loop; try { Reflect.Set(vv, "Mute", mute); } catch (Exception) { try { Reflect.Set(vv, "Muted", mute); } catch (Exception) { } } vv.Play(); }

        private static View MakeNui(string cls)
        {
            var asm = typeof(View).Assembly;
            var t = asm.GetTypes().FirstOrDefault(x => x.Name == cls && typeof(View).IsAssignableFrom(x) && !x.IsAbstract);
            if (t == null) throw new ArgumentException("no NUI view class " + cls);
            return (View)Activator.CreateInstance(t);
        }

        // ---- conversions ----
        public static float F(object o) => o is double d ? (float)d : o is float f ? f : float.Parse(Convert.ToString(o, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        public static bool Bool(object o) => o is bool b ? b : o is double d ? d != 0 : o is string s && (s == "true" || s == "1");

        public static Extents Ext(object o)
        {
            if (o is double d) { var u = (ushort)d; return new Extents(u, u, u, u); }
            var l = (List<object>)o;
            return new Extents((ushort)F(l[0]), (ushort)F(l[1]), (ushort)F(l[2]), (ushort)F(l[3]));
        }

        public static Color Col(object o)
        {
            if (o is List<object> l)
            {
                var f = l.Select(F).ToList();
                bool b255 = f.Take(3).Any(x => x > 1f);
                float r = f[0], g = f[1], bl = f[2], a = f.Count > 3 ? f[3] : 1f;
                if (b255) { r /= 255f; g /= 255f; bl /= 255f; if (a > 1f) a /= 255f; }
                return new Color(r, g, bl, a);
            }
            string s = (o as string ?? "").Trim().ToLowerInvariant();
            switch (s)
            {
                case "transparent": case "": return Color.Transparent;
                case "white": return Color.White;
                case "black": return Color.Black;
                case "red": return Color.Red;
                case "green": return Color.Green;
                case "blue": return Color.Blue;
                case "yellow": return Color.Yellow;
            }
            if (s.StartsWith("#")) s = s.Substring(1);
            if (s.Length == 3 || s.Length == 4) s = string.Concat(s.Select(c => new string(c, 2)));
            if (s.Length != 6 && s.Length != 8) throw new FormatException("bad color " + o);
            float H(int i) => int.Parse(s.Substring(i, 2), NumberStyles.HexNumber) / 255f;
            return new Color(H(0), H(2), H(4), s.Length == 8 ? H(6) : 1f);
        }
    }

    // Raw access to any public NUI property or method on a view, by name (the escape hatch for features not listed).
    internal static class Reflect
    {
        public static void Set(object target, string name, object val)
        {
            var pi = target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(p => p.CanWrite && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (pi == null) throw new ArgumentException("no writable property " + name + " on " + target.GetType().Name);
            pi.SetValue(target, Convert(pi.PropertyType, val));
        }

        public static object Call(object target, string name, List<object> args)
        {
            args = args ?? new List<object>();
            var m = target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase) && x.GetParameters().Length == args.Count);
            if (m == null) throw new ArgumentException("no method " + name + "/" + args.Count + " on " + target.GetType().Name);
            var ps = m.GetParameters();
            var conv = new object[args.Count];
            for (int i = 0; i < ps.Length; i++) conv[i] = Convert(ps[i].ParameterType, args[i]);
            return m.Invoke(target, conv);
        }

        public static object Convert(Type t, object j)
        {
            if (j == null) return null;
            var u = Nullable.GetUnderlyingType(t) ?? t;
            if (u == typeof(string)) return j as string ?? System.Convert.ToString(j, CultureInfo.InvariantCulture);
            if (u == typeof(float)) return Build.F(j);
            if (u == typeof(double)) return (double)Build.F(j);
            if (u == typeof(int)) return (int)Build.F(j);
            if (u == typeof(uint)) return (uint)Build.F(j);
            if (u == typeof(ushort)) return (ushort)Build.F(j);
            if (u == typeof(bool)) return Build.Bool(j);
            if (u.IsEnum) return j is string es ? Enum.Parse(u, es.Replace("_", ""), true) : Enum.ToObject(u, (int)Build.F(j));
            if (u == typeof(Color)) return Build.Col(j);
            if (u == typeof(Extents)) return Build.Ext(j);
            var l = j as List<object>;
            float A(int i) => l != null && i < l.Count ? Build.F(l[i]) : 0f;
            if (u == typeof(Size)) return Build.Sz(A(0), A(1));
            if (u == typeof(Size2D)) return new Size2D((int)A(0), (int)A(1));
            if (u == typeof(Position)) return new Position(A(0), A(1), A(2));
            if (u == typeof(Position2D)) return new Position2D((int)A(0), (int)A(1));
            if (u == typeof(Vector2)) return new Vector2(A(0), A(1));
            if (u == typeof(Vector3)) return new Vector3(A(0), A(1), A(2));
            if (u == typeof(Vector4)) return new Vector4(A(0), A(1), A(2), A(3));
            if (u == typeof(Rectangle)) return new Rectangle((int)A(0), (int)A(1), (int)A(2), (int)A(3));
            if (u == typeof(PropertyMap) && j is Dictionary<string, object> d)
            {
                var m = new PropertyMap();
                foreach (var kv in d)
                {
                    object v = kv.Value;
                    PropertyValue pv = v is string s ? new PropertyValue(s) : v is bool b ? new PropertyValue(b) : v is double x ? new PropertyValue((float)x) : new PropertyValue(System.Convert.ToString(v, CultureInfo.InvariantCulture));
                    if (int.TryParse(kv.Key, out var ik)) m.Add(ik, pv); else m.Add(kv.Key, pv);
                }
                return m;
            }
            return System.Convert.ChangeType(j, u, CultureInfo.InvariantCulture);
        }
    }
}
