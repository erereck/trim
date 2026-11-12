using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace TrimApp
{
    sealed class MainWindow : Window
    {
        readonly TrimView view;
        readonly Canvas layer;
        readonly TextBox editor;
        bool editIn;

        public bool Editing { get { return editor.Visibility == Visibility.Visible; } }

        public MainWindow(string path, ProbeTask probe)
        {
            Title = Path.GetFileName(path);
            Background = Theme.Bg;
            UseLayoutRounding = true;
            // the probe has been running since Main(); it is usually done by now
            MediaInfo mi = probe != null ? probe.Wait(900) : null;
            bool gpu = mi == null || !mi.Ok || mi.HasVideo;   // audio-only files don't need a D3D11 device
            // audio-only: render the UI on the CPU too, so the app loads no GPU driver at all (~70 MB instead of ~130 MB)
            string forced = Environment.GetEnvironmentVariable("TRIM_SW");
            if (forced == "1" || (forced != "0" && !gpu)) RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            view = new TrimView(this, path, gpu);
            view.InitialSize = ComputeSize(mi);
            if (mi != null) view.SetInfo(mi);
            else if (probe != null) probe.Then(r => Dispatcher.BeginInvoke(new Action(() => view.SetInfo(r))));

            editor = MakeEditor();
            editor.Visibility = Visibility.Collapsed;
            layer = new Canvas();
            layer.Children.Add(editor);
            var g = new Grid();
            g.Children.Add(view);
            g.Children.Add(layer);
            Content = g;

            // explicit sizing: content size + the real non-client frame, centered on the work area
            Size init = view.InitialSize.Value;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Width = init.Width + 16; Height = init.Height + 39;
            MinWidth = 640; MinHeight = 280;
            ContentRendered += (s, e) =>
            {
                if (!Shot.Active) view.Focus();
                Bench.Mark();
            };
            SourceInitialized += (s, e) =>
            {
                Native.StyleWindow(this);
                Size nc = Native.NonClient(this);
                Width = init.Width + nc.Width; Height = init.Height + nc.Height;
                Rect wa = SystemParameters.WorkArea;
                Left = wa.Left + Math.Max(0, (wa.Width - Width) / 2);
                Top = wa.Top + Math.Max(0, (wa.Height - Height) / 2);
            };
            PreviewKeyDown += (s, e) => view.OnKey(e);
            Closing += (s, e) => view.Shutdown();
            Shot.Setup(this, view, g);
        }

        static Size ComputeSize(MediaInfo mi)
        {
            Rect wa = SystemParameters.WorkArea;
            if (mi == null || !mi.Ok) return new Size(960, 600);
            if (!mi.HasVideo) return new Size(Math.Round(Math.Min(820, wa.Width * 0.6)), 264);
            double vw = mi.W, vh = mi.H;
            int r = ((mi.Rotation % 360) + 360) % 360;
            if (r == 90 || r == 270) { double t = vw; vw = vh; vh = t; }
            if (vw <= 0 || vh <= 0) { vw = 16; vh = 9; }
            double maxW = wa.Width * 0.72 - 32, maxH = wa.Height * 0.86 - 40 - 166;
            double s = Math.Min(maxW / vw, maxH / vh);
            double cw = Math.Max(720, vw * s + 32), ch = Math.Max(300, vh * s + 166);
            return new Size(Math.Round(cw), Math.Round(ch));
        }

        TextBox MakeEditor()
        {
            var tb = new TextBox();
            tb.FontFamily = Theme.Font;
            tb.FontSize = 13;
            tb.Foreground = Theme.Text;
            tb.CaretBrush = Theme.Text;
            tb.SelectionBrush = Theme.Accent;
            tb.Height = 28;
            tb.VerticalContentAlignment = VerticalAlignment.Center;
            var tpl = new ControlTemplate(typeof(TextBox));
            var bd = new FrameworkElementFactory(typeof(Border));
            bd.SetValue(Border.BackgroundProperty, Theme.Surface);
            bd.SetValue(Border.BorderBrushProperty, Theme.Accent);
            bd.SetValue(Border.BorderThicknessProperty, new Thickness(1.5));
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            var sv = new FrameworkElementFactory(typeof(ScrollViewer));
            sv.Name = "PART_ContentHost";
            sv.SetValue(FrameworkElement.MarginProperty, new Thickness(7, 0, 7, 0));
            sv.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(sv);
            tpl.VisualTree = bd;
            tb.Template = tpl;
            tb.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
                else if (e.Key == Key.Escape) { HideEditor(); e.Handled = true; }
            };
            tb.LostKeyboardFocus += (s, e) => HideEditor();
            return tb;
        }

        public void EditTime(bool isIn, Rect r, double value)
        {
            editIn = isIn;
            editor.Text = Theme.Fmt(value);
            editor.Width = Math.Max(104, r.Width + 18);
            Canvas.SetLeft(editor, r.Left + r.Width / 2 - editor.Width / 2);
            Canvas.SetTop(editor, r.Top + r.Height / 2 - 14);
            editor.Visibility = Visibility.Visible;
            editor.Focus();
            editor.SelectAll();
        }

        void Commit()
        {
            double t;
            if (Theme.TryParseTime(editor.Text, out t)) view.ApplyEdit(editIn, t);
            HideEditor();
        }

        void HideEditor()
        {
            if (editor.Visibility != Visibility.Visible) return;
            editor.Visibility = Visibility.Collapsed;
            view.Focus();
        }
    }

    static class Native
    {
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("shell32.dll")]
        public static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int L, T, R, B; }
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);

        // size of the title bar + borders in DIPs
        public static Size NonClient(Window w)
        {
            try
            {
                IntPtr h = new WindowInteropHelper(w).Handle;
                RECT wr, cr;
                GetWindowRect(h, out wr); GetClientRect(h, out cr);
                double dpi = VisualTreeHelper.GetDpi(w).PixelsPerDip;
                double nw = ((wr.R - wr.L) - (cr.R - cr.L)) / dpi, nh = ((wr.B - wr.T) - (cr.B - cr.T)) / dpi;
                if (nw >= 0 && nw < 60 && nh > 0 && nh < 120) return new Size(nw, nh);
            }
            catch { }
            return new Size(16, 39);
        }

        static int Cref(byte r, byte g, byte b) { return r | (g << 8) | (b << 16); }

        public static void StyleWindow(Window w)
        {
            try
            {
                IntPtr h = new WindowInteropHelper(w).Handle;
                int on = 1;
                DwmSetWindowAttribute(h, 20, ref on, 4);                       // immersive dark mode
                Color c = Theme.BgColor;
                int cap = Cref(c.R, c.G, c.B);
                DwmSetWindowAttribute(h, 35, ref cap, 4);                      // caption color
                int border = Cref(0x2A, 0x2A, 0x30);
                DwmSetWindowAttribute(h, 34, ref border, 4);                   // border color
                int text = Cref(0xA1, 0xA1, 0xAA);
                DwmSetWindowAttribute(h, 36, ref text, 4);                     // caption text color
            }
            catch { }
        }
    }

    // Dev-only: TRIM_SHOT=<png> renders the window off-screen, applies TRIM_STATE, saves a PNG and exits.
    static class Shot
    {
        public static readonly bool Active = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TRIM_SHOT"));
        public static bool Keys;

        public static void Log(string s)
        {
            if (!Active) return;
            try { File.AppendAllText(Environment.GetEnvironmentVariable("TRIM_SHOT") + ".log", DateTime.Now.ToString("HH:mm:ss.fff ") + s + Environment.NewLine); } catch { }
        }

        public static void Setup(Window w, TrimView view, FrameworkElement root)
        {
            string png = Environment.GetEnvironmentVariable("TRIM_SHOT");
            if (string.IsNullOrEmpty(png)) return;
            w.Focusable = false;
            w.SourceInitialized += (s, e) => { w.Left = -30000; w.Top = 0; };
            w.ShowActivated = false;
            w.ShowInTaskbar = false;
            w.ContentRendered += (s, e) =>
            {
                var t1 = new System.Windows.Threading.DispatcherTimer();
                t1.Interval = TimeSpan.FromMilliseconds(2500);
                t1.Tick += delegate
                {
                    t1.Stop();
                    view.Debug(Environment.GetEnvironmentVariable("TRIM_STATE") ?? "");
                    var t2 = new System.Windows.Threading.DispatcherTimer();
                    int wait;
                    if (!int.TryParse(Environment.GetEnvironmentVariable("TRIM_SHOT_WAIT"), out wait)) wait = 900;
                    t2.Interval = TimeSpan.FromMilliseconds(wait);
                    t2.Tick += delegate
                    {
                        t2.Stop();
                        double d = Theme.Dpi;
                        Log(string.Format("win {0}x{1} state={2} root {3}x{4} dpi={5} init={6}", w.ActualWidth, w.ActualHeight, w.WindowState, root.ActualWidth, root.ActualHeight, d, view.InitialSize));
                        view.LogState();
                        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                            (int)Math.Round(root.ActualWidth * d), (int)Math.Round(root.ActualHeight * d), 96 * d, 96 * d, PixelFormats.Pbgra32);
                        rtb.Render(root);
                        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                        using (var f = File.Create(png)) enc.Save(f);
                        w.Close();
                    };
                    t2.Start();
                };
                t1.Start();
            };
        }
    }

    static class Bench
    {
        public static void Mark()
        {
            string f = Environment.GetEnvironmentVariable("TRIM_BENCH");
            if (string.IsNullOrEmpty(f)) return;
            try
            {
                var start = System.Diagnostics.Process.GetCurrentProcess().StartTime;
                double ms = (DateTime.Now - start).TotalMilliseconds;
                File.AppendAllText(f, ((int)ms).ToString() + Environment.NewLine);
            }
            catch { }
        }
    }
}
