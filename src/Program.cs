using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using System.Threading;
using System.Windows;
using Microsoft.Win32;

[assembly: TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]
[assembly: AssemblyTitle("Trim")]
[assembly: AssemblyProduct("Trim")]
[assembly: AssemblyDescription("Cortador minimalista de vídeo e áudio")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace TrimApp
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            string a0 = args.Length > 0 ? args[0] : "";
            if (a0 == "--install") { Shell.Install(args.Length > 1 && args[1] == "--quiet"); return 0; }
            if (a0 == "--uninstall") { Shell.Uninstall(); return 0; }

            Ff.Exe = Ff.Find();
            if (a0 == "--probe" && args.Length > 1) { Console.WriteLine(Ff.Probe(args[1])); return 0; }
            if (a0 == "--cut" && args.Length > 4) return Cli.Cut(args);

            string path = a0.Length > 0 ? a0 : null;
            ProbeTask probe = (path != null && Ff.Exe != null) ? ProbeTask.Start(path) : null;

            var app = new Application();
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            app.DispatcherUnhandledException += (s, e) =>
            {
                e.Handled = true;
                MessageBox.Show(e.Exception.Message, "Trim", MessageBoxButton.OK, MessageBoxImage.Warning);
            };

            if (path == null)
            {
                var dlg = new OpenFileDialog();
                dlg.Title = "Escolha um vídeo ou áudio";
                dlg.Filter = "Vídeo e áudio|" + string.Join(";", Shell.AllExts.Select(e => "*" + e).ToArray()) + "|Todos os arquivos|*.*";
                if (dlg.ShowDialog() != true) return 0;
                path = dlg.FileName;
                if (Ff.Exe != null) probe = ProbeTask.Start(path);
            }
            if (!File.Exists(path))
            {
                MessageBox.Show("Arquivo não encontrado:\n" + path, "Trim", MessageBoxButton.OK, MessageBoxImage.Warning);
                return 1;
            }
            MainWindow w;
            try { w = new MainWindow(path, probe); }
            catch (Exception e)
            {
                MessageBox.Show("Não consegui iniciar o player do Windows (Media Foundation).\n\n" + e.Message, "Trim", MessageBoxButton.OK, MessageBoxImage.Warning);
                return 1;
            }
            app.Run(w);
            return 0;
        }
    }

    static class Cli
    {
        // Trim.exe --cut <file> <in> <out|-1> <precise|fast>  (used for testing; prints the result)
        public static int Cut(string[] a)
        {
            var mi = Ff.Probe(a[1]);
            double i = double.Parse(a[2], CultureInfo.InvariantCulture), o = double.Parse(a[3], CultureInfo.InvariantCulture);
            double dur = mi.Duration;
            if (o < 0 || o > dur) o = dur;
            bool pr = a[4] != "fast";
            string dst = Naming.Unique(a[1]);
            var ex = new Exporter(a[1], dst, i, o, dur, mi.HasVideo ? mi.Fps : 0, mi, pr);
            string res = null;
            var done = new ManualResetEvent(false);
            ex.Done += (ok, msg, label, secs) =>
            {
                res = ok ? "OK|" + dst + "|" + label + "|" + secs.ToString("0.00", CultureInfo.InvariantCulture) : "FAIL|" + msg;
                done.Set();
            };
            ex.Start();
            done.WaitOne();
            Console.WriteLine(res);
            Console.WriteLine("ARGS|" + ex.LastArgs);
            return res.StartsWith("OK") ? 0 : 1;
        }
    }

    static class Shell
    {
        public static readonly string[] VideoExts = { ".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi", ".wmv", ".asf", ".flv", ".ts", ".mts", ".m2ts", ".3gp", ".mpg", ".mpeg", ".vob", ".ogv" };
        public static readonly string[] AudioExts = { ".mp3", ".wav", ".m4a", ".aac", ".flac", ".ogg", ".oga", ".opus", ".wma", ".aif", ".aiff", ".ac3", ".amr", ".mka" };
        public static IEnumerable<string> AllExts { get { return VideoExts.Concat(AudioExts); } }
        const string Verb = "Trim", Label = "Cortar com Trim";
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Trim";

        public static void Install(bool quiet)
        {
            string exe = Assembly.GetExecutingAssembly().Location;
            foreach (var ext in AllExts)
            {
                using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\SystemFileAssociations\" + ext + @"\shell\" + Verb))
                {
                    k.SetValue("MUIVerb", Label);
                    k.SetValue("Icon", "\"" + exe + "\",0");
                    using (var c = k.CreateSubKey("command")) c.SetValue("", "\"" + exe + "\" \"%1\"");
                }
            }
            using (var u = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                u.SetValue("DisplayName", "Trim");
                u.SetValue("DisplayIcon", "\"" + exe + "\",0");
                u.SetValue("DisplayVersion", "1.0");
                u.SetValue("Publisher", "Trim");
                u.SetValue("InstallLocation", Path.GetDirectoryName(exe));
                u.SetValue("UninstallString", "\"" + exe + "\" --uninstall");
                u.SetValue("NoModify", 1, RegistryValueKind.DWord);
                u.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                u.SetValue("EstimatedSize", (int)(new FileInfo(exe).Length / 1024) + 1, RegistryValueKind.DWord);
            }
            Native.SHChangeNotify(0x08000000, 0x1000, IntPtr.Zero, IntPtr.Zero);
            if (!quiet) MessageBox.Show("Pronto! Clique com o botão direito num vídeo ou áudio → \"Cortar com Trim\".", "Trim");
        }

        public static void Uninstall()
        {
            foreach (var ext in AllExts)
            {
                try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\SystemFileAssociations\" + ext + @"\shell\" + Verb, false); } catch { }
            }
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
            Native.SHChangeNotify(0x08000000, 0x1000, IntPtr.Zero, IntPtr.Zero);
            try { if (Directory.Exists(Settings.Dir)) Directory.Delete(Settings.Dir, true); } catch { }
            string exe = Assembly.GetExecutingAssembly().Location;
            string dir = Path.GetDirectoryName(exe).TrimEnd('\\');
            string std = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Trim");
            if (string.Equals(dir, std, StringComparison.OrdinalIgnoreCase))
            {
                var psi = new ProcessStartInfo("cmd.exe", "/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"" + dir + "\"");
                psi.CreateNoWindow = true; psi.UseShellExecute = false; psi.WorkingDirectory = Path.GetTempPath();
                try { Process.Start(psi); } catch { }
            }
            MessageBox.Show("Trim removido.", "Trim");
        }
    }
}
