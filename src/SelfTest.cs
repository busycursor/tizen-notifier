// Creates and disposes each optional feature on this TV; nothing is shown. Result also appears in /info as "capabilities".
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace TizenNotifier
{
    internal static class SelfTest
    {
        public static Dictionary<string, object> Last = new Dictionary<string, object>();

        public static Dictionary<string, object> Run()
        {
            var r = App.OnMain(() =>
            {
                var d = new Dictionary<string, object>();
                void T(string name, Action a)
                {
                    try { a(); d[name] = "ok"; }
                    catch (Exception e) { d[name] = (e.InnerException ?? e).GetType().Name + ": " + (e.InnerException ?? e).Message; }
                }
                T("text", Text); T("image", Image); T("gif", Gif); T("lottie", Lottie); T("video", Video);
                T("layout", Layout); T("shadow", Shadow); T("gradient", Gradient); T("keyframes", Frames);
                T("notification_window", NoteWindow); T("audio_player", Audio);
                return d;
            }, 30000);
            Last = r;
            Log.W("selftest: " + Json.Write(r));
            return new Dictionary<string, object> { ["ok"] = true, ["capabilities"] = r };
        }

#if TIZEN5
        [MethodImpl(MethodImplOptions.NoInlining)] private static void Text() { using (var t = new TextLabel("x") { EnableMarkup = true, MultiLine = true, Ellipsis = true }) { } }
        [MethodImpl(MethodImplOptions.NoInlining)] private static void Image() { using (var i = new ImageView()) { } }
        [MethodImpl(MethodImplOptions.NoInlining)] private static void Gif() { using (var i = new ImageView()) { } }   // gif via the image visual
        [MethodImpl(MethodImplOptions.NoInlining)] private static void Lottie() { throw new NotSupportedException("not on Tizen 5"); }
        [MethodImpl(MethodImplOptions.NoInlining)] private static void Video() { using (var v = new VideoView()) { } }
        [MethodImpl(MethodImplOptions.NoInlining)] private static void Layout() { throw new NotSupportedException("manual row/column placement on Tizen 5"); }
        [MethodImpl(MethodImplOptions.NoInlining)] private static void Shadow() { throw new NotSupportedException("not on Tizen 5"); }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Gradient()
        {
            using (var v = new View())
                v.Background = new PropertyMap().Add(Visual.Property.Type, new PropertyValue((int)Visual.Type.Gradient));
        }
        [MethodImpl(MethodImplOptions.NoInlining)] private static void Frames() { var k = new KeyFrames(); k.Add(0f, new PropertyValue(1f)); k.Dispose(); }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void NoteWindow()
        {
            if (Window.Instance.Type != WindowType.Notification) throw new InvalidOperationException("host window is not a Notification window");
            if (!Window.Instance.SetNotificationLevel(NotificationLevel.Base)) throw new InvalidOperationException("notification level refused");
        }
#else
        [MethodImpl(MethodImplOptions.NoInlining)] private static void Text() { using (var t = new TextLabel("x") { EnableMarkup = true, MultiLine = true, Ellipsis = true }) { } }
        [MethodImpl(MethodImplOptions.NoInlining)] private static void Image() { using (var i = new ImageView()) { } }
        [MethodImpl(MethodImplOptions.NoInlining)] private static void Gif() { using (var g = new AnimatedImageView()) { } }
        [MethodImpl(MethodImplOptions.NoInlining)] private static void Lottie() { using (var l = new LottieAnimationView()) { } }
        [MethodImpl(MethodImplOptions.NoInlining)] private static void Video() { using (var v = new VideoView()) { } }
        [MethodImpl(MethodImplOptions.NoInlining)] private static void Layout() { using (var v = new View { Layout = new LinearLayout() }) { } }
        [MethodImpl(MethodImplOptions.NoInlining)] private static void Shadow() { using (var v = new View { BoxShadow = new Tizen.NUI.Shadow(10f, new Vector2(0, 4), Color.Black, new Vector2(0, 0)) }) { } }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Gradient()
        {
            using (var v = new View())
                v.Background = new PropertyMap().Add(Visual.Property.Type, new PropertyValue((int)Visual.Type.Gradient));
        }
        [MethodImpl(MethodImplOptions.NoInlining)] private static void Frames() { var k = new KeyFrames(); k.Add(0f, new PropertyValue(1f)); k.Dispose(); }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void NoteWindow()
        {
            var w = new Window("tiznotify-selftest", new Rectangle(0, 0, 10, 10), true);
            try
            {
                w.Type = WindowType.Notification;
                if (!w.SetNotificationLevel(NotificationLevel.Base)) throw new InvalidOperationException("notification level refused");
            }
            finally { w.Dispose(); }
        }
#endif
        // GET /probe: what this TV's media stack offers, found by reflection (nothing is decoded or shown).
        public static Dictionary<string, object> Probe()
        {
            var d = new Dictionary<string, object>();
            void T(string name, Func<object> f)
            {
                try { d[name] = f(); }
                catch (Exception e) { var x = e is System.Reflection.TargetInvocationException && e.InnerException != null ? e.InnerException : e; d[name] = "ERR " + x.GetType().Name + ": " + x.Message; }
            }
            List<object> Members(Type t, string filter = null) => t == null ? null : t.GetMembers(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly)
                .Where(m => m.MemberType == System.Reflection.MemberTypes.Property || m.MemberType == System.Reflection.MemberTypes.Method || m.MemberType == System.Reflection.MemberTypes.Constructor)
                .Select(m => m.MemberType == System.Reflection.MemberTypes.Constructor ? "ctor(" + string.Join(",", ((System.Reflection.ConstructorInfo)m).GetParameters().Select(p => p.ParameterType.Name)) + ")" : m.Name)
                .Where(n => !n.StartsWith("get_") && !n.StartsWith("set_") && !n.StartsWith("add_") && !n.StartsWith("remove_") && (filter == null || n.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0))
                .Distinct().OrderBy(n => n).Cast<object>().ToList();
            Type Find(string asm, string type)
            {
                try { return System.Reflection.Assembly.Load(new System.Reflection.AssemblyName(asm)).GetType(type); } catch (Exception) { return null; }
            }
            T("videoview_members", () => Members(typeof(VideoView)));
            T("videoview_has_underlay", () => typeof(VideoView).GetProperty("Underlay") != null);
            var mc = Find("Tizen.Multimedia.MediaCodec", "Tizen.Multimedia.MediaCodec.MediaCodec");
            T("mediacodec_type", () => mc?.FullName ?? "not found");
            T("mediacodec_members", () => Members(mc));
            T("mediacodec_video_codecs", () =>
            {
                var list = mc.GetProperty("SupportedVideoCodecs", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.GetValue(null) as System.Collections.IEnumerable;
                var outp = new List<object>();
                if (list == null) return "no SupportedVideoCodecs";
                object inst = Activator.CreateInstance(mc);
                try
                {
                    var get = mc.GetMethods().FirstOrDefault(m => m.Name == "GetCodecType" && m.GetParameters().Length == 2 && m.GetParameters()[1].ParameterType.Name.Contains("Video"));
                    foreach (var codec in list)
                    {
                        string dec = "?", enc = "?";
                        try { dec = get?.Invoke(inst, new object[] { false, codec })?.ToString(); } catch (Exception e) { dec = "ERR " + (e.InnerException ?? e).Message; }
                        try { enc = get?.Invoke(inst, new object[] { true, codec })?.ToString(); } catch (Exception e) { enc = "ERR " + (e.InnerException ?? e).Message; }
                        outp.Add(codec + " decoder=" + dec + " encoder=" + enc);
                    }
                }
                finally { (inst as IDisposable)?.Dispose(); }
                return outp;
            });
            var disp = Find("Tizen.Multimedia", "Tizen.Multimedia.Display");
            T("player_display_ctors", () => Members(disp, "ctor"));
            T("player_members_display", () => Members(Find("Tizen.Multimedia", "Tizen.Multimedia.Player"), "Display"));
            T("player_video_frame", () => Members(Find("Tizen.Multimedia", "Tizen.Multimedia.Player"), "Frame"));
            T("nui_native_image_source", () => Members(typeof(View).Assembly.GetType("Tizen.NUI.NativeImageSource")));
            T("nui_pixel_data", () => Members(typeof(View).Assembly.GetType("Tizen.NUI.PixelData")));
            T("nui_encoded_image_buffer", () => Members(typeof(View).Assembly.GetType("Tizen.NUI.EncodedImageBuffer")) ?? (object)"not found");
            T("nui_image_url", () => Members(typeof(View).Assembly.GetType("Tizen.NUI.ImageUrl")) ?? (object)"not found");
            // raw-pixel route: any way (public or not) to show a NativeImageSource / PixelData buffer
            var nuiAsm = typeof(View).Assembly;
            var all = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static;
            T("nui_native_image_source_all", () => nuiAsm.GetType("Tizen.NUI.NativeImageSource")?.GetMembers(all).Select(m => m.Name).Where(x => !x.StartsWith("get_") && !x.StartsWith("set_")).Distinct().OrderBy(x => x).Cast<object>().ToList());
            T("nui_generate_url_like", () =>
            {
                var hits = new List<object>();
                Type[] types;
                try { types = nuiAsm.GetTypes(); } catch (System.Reflection.ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                foreach (var t in types)
                    foreach (var m in t.GetMethods(all | System.Reflection.BindingFlags.DeclaredOnly))
                        if (m.Name.IndexOf("GenerateUrl", StringComparison.OrdinalIgnoreCase) >= 0 || m.Name.IndexOf("NativeImage", StringComparison.OrdinalIgnoreCase) >= 0 && m.Name.IndexOf("Url", StringComparison.OrdinalIgnoreCase) >= 0
                            || (m.Name.IndexOf("PixelData", StringComparison.OrdinalIgnoreCase) >= 0 && (m.Name.IndexOf("Url", StringComparison.OrdinalIgnoreCase) >= 0 || m.Name.IndexOf("Texture", StringComparison.OrdinalIgnoreCase) >= 0)))
                            hits.Add(t.FullName + "." + m.Name + "(" + string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name)) + ")" + (m.IsPublic ? "" : " [non-public]"));
                return hits.Distinct().Take(60).ToList();
            });
            T("nui_texture_types", () =>
            {
                Type[] types;
                try { types = nuiAsm.GetTypes(); } catch (System.Reflection.ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                return types.Where(t => t.Name == "Texture" || t.Name == "TextureSet" || t.Name == "Shader" || t.Name == "Renderer" || t.Name == "Geometry" || t.Name == "NativeImage" || t.Name == "ImageUrl")
                    .Select(t => t.FullName + (t.IsPublic ? "" : " [non-public]") + ": " + string.Join(",", t.GetConstructors(all).Select(c => "ctor(" + string.Join(",", c.GetParameters().Select(p => p.ParameterType.Name)) + ")")) + " | " +
                        string.Join(",", t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly).Select(m => m.Name).Distinct()))
                    .Cast<object>().ToList();
            });
            T("tizen_assemblies", () => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name).Where(n => n.StartsWith("Tizen")).OrderBy(n => n).Cast<object>().ToList());
            return d;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Audio()
        {
            using (var p = new Tizen.Multimedia.Player())
            using (var pol = new Tizen.Multimedia.AudioStreamPolicy(Tizen.Multimedia.AudioStreamType.Notification)) { }
        }
    }
}
