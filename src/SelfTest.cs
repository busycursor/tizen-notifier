// Creates and disposes each optional feature on this TV; nothing is shown. Result also appears in /info as "capabilities".
using System;
using System.Collections.Generic;
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
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Audio()
        {
            using (var p = new Tizen.Multimedia.Player())
            using (var pol = new Tizen.Multimedia.AudioStreamPolicy(Tizen.Multimedia.AudioStreamType.Notification)) { }
        }
    }
}
