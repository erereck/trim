using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace TrimApp
{
    sealed class MediaInfo
    {
        public bool Ok, HasVideo, HasAudio, HasCover;
        public double Duration, Start, Fps;
        public int W, H, Rotation, VIndex = -1, AIndex = -1, VBitrate, ABitrate, Bitrate;
        public string VCodec = "", ACodec = "", PixFmt = "", Format = "", Error;

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "ok={0} dur={1} start={2} video={3}#{4} {5} {6} {7}x{8} fps={9} rot={10} vbr={11} | audio={12}#{13} {14} abr={15} cover={16} fmt={17} err={18}",
                Ok, Duration, Start, HasVideo, VIndex, VCodec, PixFmt, W, H, Fps, Rotation, VBitrate, HasAudio, AIndex, ACodec, ABitrate, HasCover, Format, Error);
        }
    }

    static class Ff
    {
        public static string Exe;
        static readonly List<Process> live = new List<Process>();

        public static string Find()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            var cands = new List<string> { Path.Combine(dir, "ffmpeg.exe"), Path.Combine(dir, "bin", "ffmpeg.exe") };
            foreach (var p in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                string q = p.Trim().Trim('"');
                if (q.Length == 0) continue;
                try { cands.Add(Path.Combine(q, "ffmpeg.exe")); } catch { }
            }
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string la = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            cands.Add(Path.Combine(pf, "ShareX", "ffmpeg.exe"));
            cands.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ShareX", "Tools", "ffmpeg.exe"));
            cands.Add(Path.Combine(la, "Microsoft", "WinGet", "Links", "ffmpeg.exe"));
            cands.Add(@"C:\ffmpeg\bin\ffmpeg.exe");
            foreach (var c in cands)
            {
                try { if (File.Exists(c)) return c; } catch { }
            }
            return null;
        }

        public static Process Run(string args, bool redirectOut, bool redirectErr)
        {
            var psi = new ProcessStartInfo(Exe, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = redirectOut;
            psi.RedirectStandardError = redirectErr;
            if (redirectErr) psi.StandardErrorEncoding = Encoding.UTF8;
            if (redirectOut) psi.StandardOutputEncoding = Encoding.UTF8;
            var p = Process.Start(psi);
            lock (live) live.Add(p);
            return p;
        }

        public static void Done(Process p)
        {
            lock (live) live.Remove(p);
            try { p.Dispose(); } catch { }
        }

        public static void KillAll()
        {
            lock (live)
            {
                foreach (var p in live) { try { if (!p.HasExited) p.Kill(); } catch { } }
                live.Clear();
            }
        }

        public static void LowPrio(Process p)
        {
            try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        }

        public static string Q(string s) { return "\"" + s + "\""; }
        public static string T(double s) { return s.ToString("0.######", CultureInfo.InvariantCulture); }
        static double D(string s)
        {
            double v; return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        static readonly Regex rxDur = new Regex(@"Duration: (\d+):(\d+):(\d+(?:\.\d+)?)");
        static readonly Regex rxStart = new Regex(@"start: (-?\d+(?:\.\d+)?)");
        static readonly Regex rxBr = new Regex(@"bitrate: (\d+) kb/s");
        static readonly Regex rxStream = new Regex(@"^\s*Stream #\d+:(\d+)\S*?: (Video|Audio|Subtitle|Data|Attachment): (.*)$");
        static readonly Regex rxRot = new Regex(@"rotation of (-?\d+(?:\.\d+)?) degrees", RegexOptions.IgnoreCase);
        static readonly Regex rxInput = new Regex(@"^Input #0, (.+?), from");
        static readonly Regex rxSize = new Regex(@"^(\d{1,5})x(\d{1,5})");
        static readonly Regex rxKb = new Regex(@"^(\d+) kb/s");

        static List<string> SplitTop(string s)
        {
            var r = new List<string>();
            int depth = 0, start = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '(' || c == '[') depth++;
                else if (c == ')' || c == ']') depth--;
                else if (c == ',' && depth == 0) { r.Add(s.Substring(start, i - start).Trim()); start = i + 1; }
            }
            r.Add(s.Substring(start).Trim());
            return r;
        }

        static string FirstWord(string s)
        {
            s = s.Trim();
            int i = 0;
            while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
            return s.Substring(0, i).ToLowerInvariant();
        }

        public static MediaInfo Probe(string path)
        {
            var mi = new MediaInfo();
            if (Exe == null) { mi.Error = "ffmpeg não encontrado"; return mi; }
            string text;
            try
            {
                var p = Run("-hide_banner -nostdin -i " + Q(path), false, true);
                text = p.StandardError.ReadToEnd();
                p.WaitForExit();
                Done(p);
            }
            catch (Exception e) { mi.Error = e.Message; return mi; }

            bool inMainVideo = false;
            double tbr = 0;
            string lastLine = "";
            foreach (var raw in text.Split('\n'))
            {
                string l = raw.TrimEnd('\r');
                if (l.Trim().Length > 0) lastLine = l.Trim();
                Match m = rxInput.Match(l);
                if (m.Success) { mi.Format = m.Groups[1].Value; continue; }
                m = rxDur.Match(l);
                if (m.Success)
                {
                    mi.Duration = int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60 + D(m.Groups[3].Value);
                    var ms = rxStart.Match(l); if (ms.Success) mi.Start = D(ms.Groups[1].Value);
                    var mb = rxBr.Match(l); if (mb.Success) mi.Bitrate = int.Parse(mb.Groups[1].Value);
                    continue;
                }
                m = rxStream.Match(l);
                if (m.Success)
                {
                    inMainVideo = false;
                    int idx = int.Parse(m.Groups[1].Value);
                    string kind = m.Groups[2].Value, rest = m.Groups[3].Value;
                    var parts = SplitTop(rest);
                    if (kind == "Video")
                    {
                        if (rest.Contains("(attached pic)")) { mi.HasCover = true; continue; }
                        if (mi.HasVideo) continue;
                        mi.HasVideo = true; mi.VIndex = idx; inMainVideo = true;
                        mi.VCodec = FirstWord(parts[0]);
                        if (parts.Count > 1)
                        {
                            string pf = parts[1]; int k = pf.IndexOf('(');
                            mi.PixFmt = (k >= 0 ? pf.Substring(0, k) : pf).Trim();
                        }
                        foreach (var part in parts)
                        {
                            var sm = rxSize.Match(part);
                            if (sm.Success && mi.W == 0) { mi.W = int.Parse(sm.Groups[1].Value); mi.H = int.Parse(sm.Groups[2].Value); }
                            string pt = part.Replace(" (default)", "").Trim();
                            if (pt.EndsWith(" fps")) mi.Fps = D(pt.Substring(0, pt.Length - 4));
                            else if (pt.EndsWith(" tbr"))
                            {
                                string v = pt.Substring(0, pt.Length - 4);
                                if (!v.EndsWith("k")) tbr = D(v);
                            }
                            var bm = rxKb.Match(part); if (bm.Success) mi.VBitrate = int.Parse(bm.Groups[1].Value);
                        }
                    }
                    else if (kind == "Audio" && !mi.HasAudio)
                    {
                        mi.HasAudio = true; mi.AIndex = idx;
                        mi.ACodec = FirstWord(parts[0]);
                        foreach (var part in parts)
                        {
                            var bm = rxKb.Match(part); if (bm.Success) mi.ABitrate = int.Parse(bm.Groups[1].Value);
                        }
                    }
                    continue;
                }
                if (inMainVideo)
                {
                    m = rxRot.Match(l);
                    if (m.Success) mi.Rotation = (int)Math.Round(D(m.Groups[1].Value));
                }
            }
            if (mi.Fps <= 0 && tbr > 0 && tbr <= 240) mi.Fps = tbr;
            if (mi.Fps > 240) mi.Fps = 0;
            mi.Fps = NormFps(mi.Fps);
            int r = ((mi.Rotation % 360) + 360) % 360;
            if (r > 180) r -= 360;
            mi.Rotation = r;
            mi.Ok = mi.HasVideo || mi.HasAudio;
            if (!mi.Ok)
            {
                string e = lastLine;
                int c = e.LastIndexOf(": ");
                if (c >= 0 && c + 2 < e.Length) e = e.Substring(c + 2);
                mi.Error = e.Length > 0 ? e : "formato não reconhecido";
            }
            return mi;
        }

        static double NormFps(double f)
        {
            if (f <= 0) return 0;
            double[] ntsc = { 24000.0 / 1001, 30000.0 / 1001, 48000.0 / 1001, 60000.0 / 1001, 120000.0 / 1001 };
            foreach (var n in ntsc) if (Math.Abs(f - n) < 0.006) return n;
            return f;
        }
    }

    static class Settings
    {
        public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Trim");
        static readonly string file = Path.Combine(Dir, "settings.ini");
        static Dictionary<string, string> d;
        static readonly object gate = new object();

        static void Load()
        {
            d = new Dictionary<string, string>();
            try
            {
                if (!File.Exists(file)) return;
                foreach (var l in File.ReadAllLines(file))
                {
                    int i = l.IndexOf('=');
                    if (i > 0) d[l.Substring(0, i)] = l.Substring(i + 1);
                }
            }
            catch { }
        }

        public static string Get(string k, string def)
        {
            lock (gate)
            {
                if (d == null) Load();
                string v;
                return d.TryGetValue(k, out v) ? v : def;
            }
        }

        public static void Set(string k, string v)
        {
            lock (gate)
            {
                if (d == null) Load();
                string old;
                if (d.TryGetValue(k, out old) && old == v) return;
                d[k] = v;
                try
                {
                    Directory.CreateDirectory(Dir);
                    File.WriteAllLines(file, d.Select(kv => kv.Key + "=" + kv.Value).ToArray());
                }
                catch { }
            }
        }
    }

    sealed class ProbeTask
    {
        MediaInfo result;
        Action<MediaInfo> then;
        readonly ManualResetEvent done = new ManualResetEvent(false);
        readonly object gate = new object();

        public static ProbeTask Start(string path)
        {
            var t = new ProbeTask();
            var th = new Thread(() =>
            {
                MediaInfo r;
                try { r = Ff.Probe(path); } catch (Exception e) { r = new MediaInfo(); r.Error = e.Message; }
                Action<MediaInfo> cb;
                lock (t.gate) { t.result = r; t.done.Set(); cb = t.then; }
                if (cb != null) cb(r);
            });
            th.IsBackground = true;
            th.Start();
            return t;
        }

        public MediaInfo Wait(int ms) { return done.WaitOne(ms) ? result : null; }

        public void Then(Action<MediaInfo> cb)
        {
            MediaInfo r = null; bool ready;
            lock (gate)
            {
                ready = done.WaitOne(0);
                if (ready) r = result; else then = cb;
            }
            if (ready) cb(r);
        }
    }
}
