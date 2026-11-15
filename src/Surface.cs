// Where a note is drawn.
//   Tizen 6+ (default build): one transparent, non-focusable Notification-type Window per note (proven on the Frames).
//   Tizen 5 (TIZEN5 build): NUI API5 cannot create extra windows, so every note is a View inside the
//   app's own window, which is made a Notification window and grows to full screen only while a note is showing.
using System;
using System.Collections.Generic;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using Timer = Tizen.NUI.Timer;

namespace TizenNotifier
{
    internal sealed class Surface
    {
        private readonly Action<Window.KeyEventArgs> onKey;
#if TIZEN5
        private static readonly List<Surface> Shown = new List<Surface>();
        private static readonly List<Surface> Focused = new List<Surface>();
        private static bool hooked;
        private readonly View box;
        private bool shown;

        public Surface(string name, int w, int h, Action<Window.KeyEventArgs> onKey, List<object> warn)
        {
            this.onKey = onKey;
            box = new View { Name = name, Size = new Size(w, h, 0), Position = new Position(0, 0, 0), BackgroundColor = Color.Transparent };
            box.Hide();
            Window.Instance.GetDefaultLayer().Add(box);
            if (!hooked) { hooked = true; Window.Instance.KeyEvent += (s, e) => { if (Focused.Count > 0) Focused[Focused.Count - 1].onKey(e); }; }
        }

        // Host window: transparent Notification window, 1x1 while idle, full screen while any note is up.
        public static void SetupHost()
        {
            var w = Window.Instance;
            try { w.BackgroundColor = Color.Transparent; w.SetTransparency(true); } catch (Exception e) { Log.W("host transparency: " + e.Message); }
            try { w.SetAcceptFocus(false); } catch (Exception e) { Log.W("host focus: " + e.Message); }
            try { w.Type = WindowType.Notification; } catch (Exception e) { Log.W("host type: " + e.Message); }
            try { if (!w.SetNotificationLevel(NotificationLevel.Base)) Log.W("host notification level refused"); } catch (Exception e) { Log.W("host level: " + e.Message); }
            // Called again on every resume, and showing the host window itself triggers one. Measured 2026-11-14 on Tizen 5.0:
            // a size set BEFORE that resume, or a same-size set after it, is ignored by the compositor (window stays invisible);
            // only a real change after the resume shows it. So with a note waiting: shrink, then grow.
            if (Shown.Count > 0) { Idle(); Full(); } else Idle();
        }

        private static void Idle()
        {
            try { Window.Instance.WindowSize = new Size2D(1, 1); Window.Instance.WindowPosition = new Position2D(0, 0); } catch (Exception e) { Log.W("host idle size: " + e.Message); }
        }

        private static void Full()
        {
            try { Window.Instance.WindowPosition = new Position2D(0, 0); Window.Instance.WindowSize = new Size2D(1920, 1080); } catch (Exception e) { Log.W("host full size: " + e.Message); }
        }

        // Diagnostics: report the host window, and run actions on it (POST /host {"actions": [...]}).
        public static Dictionary<string, object> Host(List<object> actions)
        {
            var w = Window.Instance;
            var done = new List<object>();
            if (actions != null)
                foreach (var a in actions)
                {
                    string act = a as string ?? "";
                    try
                    {
                        if (act == "show") w.Show();
                        else if (act == "hide") w.Hide();
                        else if (act == "raise") w.Raise();
                        else if (act == "lower") w.Lower();
                        else if (act == "activate") w.Activate();
                        else if (act == "full") Full();
                        else if (act == "idle") Idle();
                        else if (act == "type:notification") w.Type = WindowType.Notification;
                        else if (act == "type:normal") w.Type = WindowType.Normal;
                        else if (act.StartsWith("level:"))
                        {
                            var lv = act.Substring(6); var nl = lv == "top" ? NotificationLevel.Top : lv == "high" ? NotificationLevel.High : lv == "medium" ? NotificationLevel.Medium : lv == "none" ? NotificationLevel.None : NotificationLevel.Base;
                            done.Add(act + " -> " + w.SetNotificationLevel(nl)); continue;
                        }
                        else if (act == "focus:true") w.SetAcceptFocus(true);
                        else if (act == "focus:false") w.SetAcceptFocus(false);
                        else if (act == "opaque") { w.SetTransparency(false); w.BackgroundColor = new Color(0.1f, 0.4f, 0.9f, 1f); }
                        else if (act == "transparent") { w.BackgroundColor = Color.Transparent; w.SetTransparency(true); }
                        else if (act.StartsWith("size:")) { var p = act.Substring(5).Split('x'); w.WindowSize = new Size2D(int.Parse(p[0]), int.Parse(p[1])); }
                        else if (act.StartsWith("pos:")) { var p = act.Substring(4).Split(','); w.WindowPosition = new Position2D(int.Parse(p[0]), int.Parse(p[1])); }
                        else { done.Add(act + " -> unknown"); continue; }
                        done.Add(act + " -> ok");
                    }
                    catch (Exception e) { done.Add(act + " -> " + e.GetType().Name + " " + e.Message); }
                }
            var st = new Dictionary<string, object> { ["actions"] = done, ["shown_notes"] = Shown.Count, ["focused_notes"] = Focused.Count };
            void R(string k, Func<object> f) { try { st[k] = f()?.ToString(); } catch (Exception e) { st[k] = "ERR " + e.Message; } }
            R("size", () => w.WindowSize.Width + "x" + w.WindowSize.Height);
            R("position", () => w.WindowPosition.X + "," + w.WindowPosition.Y);
            R("type", () => w.Type);
            R("level", () => w.GetNotificationLevel());
            R("root_children", () => w.GetDefaultLayer().ChildCount);
            return st;
        }

        public void Add(View v) => box.Add(v);
        public void Remove(View v) => box.Remove(v);
        public void Show()
        {
            if (shown) return;
            shown = true; Shown.Add(this);
            box.Show(); box.RaiseToTop();
            if (Shown.Count == 1)
            {
                Full();
                // The compositor ignores a resize made before the window is really up (measured 2026-11-14): shrink and
                // regrow once it is, which is what made every test message appear.
                var t = new Timer(500);
                t.Tick += (s, e) => { if (Shown.Count > 0) { Idle(); Full(); } return false; };
                Bounces.Add(t); t.Start();
                if (Bounces.Count > 4) { var o = Bounces[0]; Bounces.RemoveAt(0); try { o.Dispose(); } catch (Exception) { } }
            }
        }
        private static readonly List<Timer> Bounces = new List<Timer>();
        public void Hide() { if (!shown) return; shown = false; box.Hide(); Shown.Remove(this); if (Shown.Count == 0) Idle(); }
        public void Resize(int w, int h) => box.Size = new Size(w, h, 0);
        public void Move(int x, int y) => box.Position = new Position(x, y, 0);
        public bool SetLevel(NotificationLevel l) => true;   // the host window holds the level
        public void Focus(bool take)
        {
            Focused.Remove(this);
            if (take) Focused.Add(this);
            try { Window.Instance.SetAcceptFocus(Focused.Count > 0); if (take) Window.Instance.Raise(); } catch (Exception e) { Log.W("host focus: " + e.Message); }
        }
        public void Dispose()
        {
            Hide(); Focus(false);
            try { Window.Instance.GetDefaultLayer().Remove(box); box.Dispose(); } catch (Exception) { }
        }
        public static Size2D Screen() => new Size2D(1920, 1080);
#else
        private readonly Window win;

        public Surface(string name, int w, int h, Action<Window.KeyEventArgs> onKey, List<object> warn)
        {
            this.onKey = onKey;
            win = new Window(name, new Rectangle(0, 0, w, h), true);
            try { win.Type = WindowType.Notification; } catch (Exception e) { warn.Add("window type: " + e.Message); }
            try { win.BackgroundColor = Color.Transparent; win.SetTransparency(true); } catch (Exception e) { warn.Add("transparency: " + e.Message); }
            try { win.SetOpaqueState(false); } catch (Exception e) { warn.Add("opaque: " + e.Message); }
            try { win.SetAcceptFocus(false); } catch (Exception e) { warn.Add("focus: " + e.Message); }
            win.KeyEvent += (s, e) => onKey(e);
        }

        public static void SetupHost() { }
        public static Dictionary<string, object> Host(List<object> actions) => new Dictionary<string, object> { ["ok"] = false, ["error"] = "only on the Tizen 5 build" };
        public void Add(View v) => win.Add(v);
        public void Remove(View v) => win.Remove(v);
        public void Show() => win.Show();
        public void Hide() => win.Hide();
        public void Resize(int w, int h) => win.WindowSize = new Size2D(w, h);
        public void Move(int x, int y) => win.WindowPosition = new Position2D(x, y);
        public bool SetLevel(NotificationLevel l) => win.SetNotificationLevel(l);
        public void Focus(bool take) { win.SetAcceptFocus(take); if (take) win.Raise(); }
        public void Dispose() { try { win.Hide(); } catch (Exception) { } win.Dispose(); }
        public static Size2D Screen() => App.MainWin.WindowSize;
#endif
    }
}
