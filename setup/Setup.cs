// TrimSetup.exe — tiny per-user installer for Trim (no admin needed).
// Embeds Trim.exe, copies it to %LOCALAPPDATA%\Programs\Trim and registers the Explorer menu entry.
//   TrimSetup.exe        -> installer window
//   TrimSetup.exe /S     -> silent install (exit code 0 = ok)
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

[assembly: TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]
[assembly: AssemblyTitle("Instalar Trim")]
[assembly: AssemblyProduct("Trim")]
[assembly: AssemblyDescription("Instalador do Trim")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace TrimSetup
{
    static class Program
    {
        public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Trim");
        public static readonly string Exe = Path.Combine(Dir, "Trim.exe");

        [STAThread]
        static int Main(string[] args)
        {
            foreach (var a in args)
            {
                string s = a.ToLowerInvariant();
                if (s == "/s" || s == "-s" || s == "--silent")
                {
                    string err = Install();
                    if (err != null) { Console.Error.WriteLine(err); return 1; }
                    return 0;
                }
            }
            var app = new Application();
            app.Run(new SetupWindow());
            return 0;
        }

        public static bool Installed { get { return File.Exists(Exe); } }

        // returns null on success, otherwise an error message
        public static string Install()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                foreach (var p in Process.GetProcessesByName("Trim"))
                {
                    try
                    {
                        if (string.Equals(p.MainModule.FileName, Exe, StringComparison.OrdinalIgnoreCase)) { p.Kill(); p.WaitForExit(3000); }
                    }
                    catch { }
                }
                using (var src = Assembly.GetExecutingAssembly().GetManifestResourceStream("Trim.exe"))
                {
                    if (src == null) return "instalador corrompido (Trim.exe não embutido)";
                    string tmp = Exe + ".new";
                    using (var dst = File.Create(tmp)) src.CopyTo(dst);
                    if (File.Exists(Exe)) File.Delete(Exe);
                    File.Move(tmp, Exe);
                }
                var psi = new ProcessStartInfo(Exe, "--install --quiet");
                psi.UseShellExecute = false; psi.CreateNoWindow = true;
                using (var p = Process.Start(psi))
                {
                    if (!p.WaitForExit(15000)) return "o registro do menu demorou demais";
                    if (p.ExitCode != 0) return "falha ao registrar o menu (código " + p.ExitCode + ")";
                }
                return null;
            }
            catch (Exception e) { return e.Message; }
        }

        public static string FindFfmpeg()
        {
            var cands = new System.Collections.Generic.List<string>();
            foreach (var p in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                string q = p.Trim().Trim('"');
                if (q.Length == 0) continue;
                try { cands.Add(Path.Combine(q, "ffmpeg.exe")); } catch { }
            }
            cands.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ShareX", "ffmpeg.exe"));
            cands.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ShareX", "Tools", "ffmpeg.exe"));
            cands.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links", "ffmpeg.exe"));
            cands.Add(@"C:\ffmpeg\bin\ffmpeg.exe");
            foreach (var c in cands) { try { if (File.Exists(c)) return c; } catch { } }
            return null;
        }
    }

    sealed class SetupWindow : Window
    {
        static readonly Color BgC = Color.FromRgb(0x11, 0x11, 0x13);
        static readonly Brush Bg = B(0x11, 0x11, 0x13), Surface = B(0x1B, 0x1B, 0x1F), Surface2 = B(0x2A, 0x2A, 0x30), Surface3 = B(0x38, 0x38, 0x40),
                              Text = B(0xF4, 0xF4, 0xF6), Text2 = B(0xA1, 0xA1, 0xAA), Text3 = B(0x66, 0x66, 0x70),
                              Accent = B(0xFF, 0xC5, 0x3D), AccentHi = B(0xFF, 0xD4, 0x6E), Ink = B(0x26, 0x1B, 0x00),
                              Good = B(0x5E, 0xE0, 0x8F), Warn = B(0xFF, 0xB0, 0x6B), Danger = B(0xFF, 0x80, 0x80);
        static readonly FontFamily Font = new FontFamily("Segoe UI");

        static Brush B(byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }

        readonly TextBlock status, ffStatus;
        readonly Button main, ffButton, uninstall;
        readonly StackPanel ffRow;
        bool done;

        public SetupWindow()
        {
            Title = "Instalar Trim";
            Width = 440;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Bg;
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

            var root = new StackPanel { Margin = new Thickness(32, 26, 32, 22), HorizontalAlignment = HorizontalAlignment.Stretch };

            var icon = new Image { Width = 76, Height = 76, Margin = new Thickness(0, 0, 0, 12) };
            try
            {
                var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("icon.png");
                if (s != null) icon.Source = BitmapFrame.Create(s, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            }
            catch { }
            RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
            root.Children.Add(icon);

            root.Children.Add(Label("Trim", 24, Text, true, 0));
            root.Children.Add(Label("Cortador minimalista de vídeo e áudio", 13, Text2, false, 2));

            status = Label(Program.Installed ? "Já está instalado. Instalar de novo atualiza a versão." : "Adiciona \"Cortar com Trim\" ao clique direito do Explorer.\nSó para o seu usuário, sem precisar de administrador.", 12, Text3, false, 16);
            root.Children.Add(status);

            main = PillButton(Program.Installed ? "Atualizar" : "Instalar", Accent, AccentHi, Ink, 132);
            main.Margin = new Thickness(0, 18, 0, 0);
            main.Click += delegate { if (done) Close(); else DoInstall(); };
            root.Children.Add(main);

            ffStatus = Label("", 12, Text3, false, 18);
            ffButton = PillButton("Instalar ffmpeg", Surface2, Surface3, Text, 0);
            ffButton.Height = 30; ffButton.FontSize = 12; ffButton.Padding = new Thickness(14, 0, 14, 0);
            ffButton.Margin = new Thickness(0, 8, 0, 0);
            ffButton.Click += delegate { InstallFfmpeg(); };
            ffRow = new StackPanel();
            ffRow.Children.Add(ffStatus);
            ffRow.Children.Add(ffButton);
            root.Children.Add(ffRow);
            UpdateFfmpeg();

            uninstall = LinkButton("desinstalar");
            uninstall.Margin = new Thickness(0, 14, 0, 0);
            uninstall.Visibility = Program.Installed ? Visibility.Visible : Visibility.Collapsed;
            uninstall.Click += delegate { DoUninstall(); };
            root.Children.Add(uninstall);

            Content = root;
            SourceInitialized += (s, e) => DarkTitle();
            Activated += delegate { if (!done) UpdateFfmpeg(); };   // re-check after the user installs ffmpeg
            KeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };
            DevShot();
        }

        static TextBlock Label(string text, double size, Brush color, bool semi, double top)
        {
            return new TextBlock
            {
                Text = text, FontSize = size, Foreground = color, FontFamily = Font,
                FontWeight = semi ? FontWeights.SemiBold : FontWeights.Normal,
                TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, top, 0, 0)
            };
        }

        static Button PillButton(string text, Brush bg, Brush hover, Brush fg, double width)
        {
            var t = new ControlTemplate(typeof(Button));
            var bd = new FrameworkElementFactory(typeof(Border), "bd");
            bd.SetValue(Border.BackgroundProperty, bg);
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(18));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            cp.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
            bd.AppendChild(cp);
            t.VisualTree = bd;
            var tr = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            tr.Setters.Add(new Setter(Border.BackgroundProperty, hover, "bd"));
            t.Triggers.Add(tr);
            var dis = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            dis.Setters.Add(new Setter(UIElement.OpacityProperty, 0.55, "bd"));
            t.Triggers.Add(dis);
            var b = new Button
            {
                Content = text, Template = t, Foreground = fg, FontFamily = Font, FontSize = 14, FontWeight = FontWeights.SemiBold,
                Height = 36, Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Center, FocusVisualStyle = null
            };
            if (width > 0) b.Width = width;
            return b;
        }

        static Button LinkButton(string text)
        {
            var t = new ControlTemplate(typeof(Button));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            t.VisualTree = cp;
            return new Button
            {
                Content = new TextBlock { Text = text, FontSize = 12, Foreground = Text3, FontFamily = Font, TextDecorations = TextDecorations.Underline },
                Template = t, Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Center, FocusVisualStyle = null
            };
        }

        void UpdateFfmpeg()
        {
            string ff = Program.FindFfmpeg();
            if (ff != null)
            {
                ffStatus.Text = "✓ ffmpeg encontrado";
                ffStatus.Foreground = Good;
                ffButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                ffStatus.Text = "O Trim usa o ffmpeg para cortar, e ele não foi encontrado.";
                ffStatus.Foreground = Warn;
                ffButton.Visibility = Visibility.Visible;
            }
        }

        void InstallFfmpeg()
        {
            try
            {
                // visible console so the user sees winget and accepts its terms
                Process.Start(new ProcessStartInfo("cmd.exe", "/k winget install --id Gyan.FFmpeg.Essentials -e && echo. && echo Pronto! Pode fechar esta janela.") { UseShellExecute = true });
                ffStatus.Text = "Instalando pelo winget… quando terminar, volte aqui.";
                ffStatus.Foreground = Text2;
            }
            catch (Exception e)
            {
                ffStatus.Text = "Não deu para abrir o winget: " + e.Message;
                ffStatus.Foreground = Danger;
            }
        }

        void DoInstall()
        {
            main.IsEnabled = false;
            main.Content = "Instalando…";
            status.Text = "";
            Dispatcher.BeginInvoke(new Action(() =>
            {
                string err = Program.Install();
                main.IsEnabled = true;
                if (err == null)
                {
                    done = true;
                    main.Content = "Fechar";
                    status.Text = "Pronto! Clique com o botão direito num vídeo ou áudio\ne escolha \"Cortar com Trim\".";
                    status.Foreground = Good;
                    uninstall.Visibility = Visibility.Collapsed;
                }
                else
                {
                    main.Content = "Tentar de novo";
                    status.Text = "Não deu para instalar: " + err;
                    status.Foreground = Danger;
                }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        void DoUninstall()
        {
            if (MessageBox.Show(this, "Remover o Trim e o item do menu do Explorer?", "Trim", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            try
            {
                var p = Process.Start(new ProcessStartInfo(Program.Exe, "--uninstall") { UseShellExecute = false });
                p.WaitForExit(15000);
            }
            catch { }
            Close();
        }

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        void DarkTitle()
        {
            try
            {
                IntPtr h = new WindowInteropHelper(this).Handle;
                int on = 1; DwmSetWindowAttribute(h, 20, ref on, 4);
                int cap = BgC.R | (BgC.G << 8) | (BgC.B << 16); DwmSetWindowAttribute(h, 35, ref cap, 4);
                int border = 0x2A | (0x2A << 8) | (0x30 << 16); DwmSetWindowAttribute(h, 34, ref border, 4);
                int text = 0xA1 | (0xA1 << 8) | (0xAA << 16); DwmSetWindowAttribute(h, 36, ref text, 4);
            }
            catch { }
        }

        // Dev-only: SETUP_SHOT=<png> renders the window off-screen to a PNG and exits (SETUP_STATE=done|noff for other states)
        void DevShot()
        {
            string png = Environment.GetEnvironmentVariable("SETUP_SHOT");
            if (string.IsNullOrEmpty(png)) return;
            ShowActivated = false; ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.Manual; Left = -30000; Top = 0;
            string st = Environment.GetEnvironmentVariable("SETUP_STATE") ?? "";
            ContentRendered += delegate
            {
                if (st.Contains("noff")) { ffStatus.Text = "O Trim usa o ffmpeg para cortar, e ele não foi encontrado."; ffStatus.Foreground = Warn; ffButton.Visibility = Visibility.Visible; }
                if (st.Contains("done"))
                {
                    done = true; main.Content = "Fechar"; uninstall.Visibility = Visibility.Collapsed;
                    status.Text = "Pronto! Clique com o botão direito num vídeo ou áudio\ne escolha \"Cortar com Trim\"."; status.Foreground = Good;
                }
                var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                t.Tick += delegate
                {
                    t.Stop();
                    var el = (FrameworkElement)Content;
                    double d = VisualTreeHelper.GetDpi(this).PixelsPerDip;
                    var rtb = new RenderTargetBitmap((int)(el.ActualWidth * d), (int)(el.ActualHeight * d), 96 * d, 96 * d, PixelFormats.Pbgra32);
                    var bg = new DrawingVisual();
                    using (var dc = bg.RenderOpen()) dc.DrawRectangle(Bg, null, new Rect(0, 0, el.ActualWidth, el.ActualHeight));
                    rtb.Render(bg); rtb.Render(el);
                    var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(rtb));
                    using (var f = File.Create(png)) enc.Save(f);
                    Close();
                };
                t.Start();
            };
        }
    }
}
