using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace TrimApp
{
    static class Naming
    {
        // video.mp4 -> video_trim.mp4 -> video_trim (1).mp4 -> video_trim (2).mp4 ...
        public static string Unique(string src)
        {
            string dir = Path.GetDirectoryName(src);
            string name = Path.GetFileNameWithoutExtension(src);
            string ext = Path.GetExtension(src);
            string p = Path.Combine(dir, name + "_trim" + ext);
            for (int i = 1; File.Exists(p); i++) p = Path.Combine(dir, name + "_trim (" + i + ")" + ext);
            return p;
        }
    }

    sealed class Exporter
    {
        public event Action<double> Progress;
        public event Action<bool, string, string, double> Done; // ok, error, label, seconds
        public readonly string Src, Dst;
        public string LastArgs;
        readonly double inT, outT, dur, fps;
        readonly MediaInfo mi;
        readonly bool precise;
        volatile bool cancel;
        Process proc;
        Thread thread;
        readonly object gate = new object();
        double lastReport = -1;
        bool triedNv;

        public Exporter(string src, string dst, double inT, double outT, double dur, double fps, MediaInfo mi, bool precise)
        {
            Src = src; Dst = dst; this.inT = inT; this.outT = outT; this.dur = dur; this.fps = fps; this.mi = mi; this.precise = precise;
        }

        public void Start()
        {
            thread = new Thread(Run);
            thread.IsBackground = true;
            thread.Start();
        }

        public void Cancel()
        {
            cancel = true;
            lock (gate) { try { if (proc != null && !proc.HasExited) proc.Kill(); } catch { } }
        }

        public void CancelAndWait()
        {
            Cancel();
            if (thread != null) thread.Join(3000);
        }

        sealed class Attempt
        {
            public string Args, Label, EncKey;
            public Attempt(string a, string l, string k) { Args = a; Label = l; EncKey = k; }
        }

        bool FromStart { get { return inT <= 0.0005; } }
        bool ToEnd { get { return dur > 0 && outT >= dur - 0.0005; } }
        double Half { get { return (mi.HasVideo && precise && fps > 0) ? 0.5 / fps : 0; } }
        double SeekPos { get { return FromStart ? 0 : Math.Max(0, inT - Half); } }
        double Length { get { return ToEnd ? Math.Max(0.001, dur - SeekPos) : Math.Max(0.001, outT - inT); } }

        void Run()
        {
            var sw = Stopwatch.StartNew();
            string err = "";
            foreach (var a in BuildAttempts())
            {
                if (cancel) break;
                LastArgs = a.Args;
                int code = Exec(a.Args, out err);
                if (cancel) break;
                if (code == 0 && File.Exists(Dst) && new FileInfo(Dst).Length > 0)
                {
                    if (a.EncKey != null)
                    {
                        Settings.Set("enc", a.EncKey);
                        if (a.EncKey == "sw" && triedNv) Settings.Set("enc_at", DateTime.UtcNow.Ticks.ToString());
                    }
                    Fire(true, null, a.Label, sw.Elapsed.TotalSeconds);
                    return;
                }
                Delete();
            }
            Delete();
            Fire(false, cancel ? "cancel" : Clean(err), null, 0);
        }

        void Fire(bool ok, string msg, string label, double secs)
        {
            var d = Done;
            if (d != null) d(ok, msg, label, secs);
        }

        void Delete()
        {
            for (int i = 0; i < 5; i++)
            {
                try { if (File.Exists(Dst)) File.Delete(Dst); return; } catch { Thread.Sleep(100); }
            }
        }

        static string Clean(string err)
        {
            var lines = (err ?? "").Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            if (lines.Count == 0) return "erro desconhecido do ffmpeg";
            string l = lines.Last();
            if (l.Length > 140) l = l.Substring(0, 140) + "…";
            return l;
        }

        int Exec(string args, out string err)
        {
            var psi = new ProcessStartInfo(Ff.Exe, args);
            psi.UseShellExecute = false; psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8; psi.StandardErrorEncoding = Encoding.UTF8;
            var sb = new StringBuilder();
            var p = new Process();
            p.StartInfo = psi;
            p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
            lock (gate)
            {
                if (cancel) { err = ""; return -1; }
                p.Start();
                proc = p;
            }
            p.BeginErrorReadLine();
            double total = Length;
            string line;
            while ((line = p.StandardOutput.ReadLine()) != null)
            {
                if (line.StartsWith("out_time_us=") || line.StartsWith("out_time_ms="))
                {
                    long us;
                    if (long.TryParse(line.Substring(line.IndexOf('=') + 1), out us) && us >= 0) Report(Math.Min(0.99, us / 1e6 / total));
                }
                else if (line == "progress=end") Report(1);
            }
            p.WaitForExit();
            int code = p.ExitCode;
            lock (sb) err = sb.ToString();
            lock (gate) proc = null;
            p.Dispose();
            return code;
        }

        void Report(double v)
        {
            if (Math.Abs(v - lastReport) < 0.004 && v < 1) return;
            lastReport = v;
            var h = Progress;
            if (h != null) h(v);
        }

        static bool IsMov(string ext)
        {
            return ext == ".mp4" || ext == ".m4v" || ext == ".mov" || ext == ".m4a" || ext == ".3gp";
        }

        List<Attempt> BuildAttempts()
        {
            var L = new List<Attempt>();
            string ext = Path.GetExtension(Dst).ToLowerInvariant();
            string pre = "-hide_banner -nostdin -v error -nostats -stats_period 0.1 -progress pipe:1 -n ";
            string input = (FromStart ? "" : "-ss " + Ff.T(SeekPos) + " ") + "-i " + Ff.Q(Src) + " " +
                           (ToEnd ? "" : "-t " + Ff.T(outT - inT) + " ");
            string mov = IsMov(ext) ? "-movflags +faststart " : "";
            string o = Ff.Q(Dst);

            if (!precise)
            {
                L.Add(new Attempt(pre + input + "-map 0:v? -map 0:a? -map 0:s? -c copy " + mov + o, "sem recodificar", null));
                L.Add(new Attempt(pre + input + "-map 0:v? -map 0:a? -c copy " + mov + o, "sem recodificar", null));
                return L;
            }

            if (!mi.HasVideo)
            {
                string ac = AudioArgs(ext, false);
                if (mi.HasCover) L.Add(new Attempt(pre + input + "-map 0:a -map 0:v -c:v copy " + ac + " " + mov + o, "áudio exato", null));
                L.Add(new Attempt(pre + input + "-map 0:a -vn " + ac + " " + mov + o, "áudio exato", null));
                if (ac.Length > 0) L.Add(new Attempt(pre + input + "-map 0:a -vn " + mov + o, "áudio exato", null));
                return L;
            }

            // keep the source timebase so frame timestamps aren't rounded to a 1/fps grid (that would drop the last frame)
            string map = "-map 0:" + mi.VIndex + " -map 0:a? -sn -dn -enc_time_base:v demux ";
            string a = AudioArgs(ext, true);
            foreach (var enc in VideoEncoders(ext))
                L.Add(new Attempt(pre + input + map + enc.Args + " " + a + " " + mov + o, enc.Label, enc.Key));
            return L;
        }

        sealed class Enc
        {
            public string Args, Label, Key;
            public Enc(string a, string l, string k) { Args = a; Label = l; Key = k; }
        }

        List<Enc> VideoEncoders(string ext)
        {
            var r = new List<Enc>();
            switch (ext)
            {
                case ".webm":
                    r.Add(new Enc("-c:v libvpx-vp9 -deadline realtime -cpu-used 8 -row-mt 1 -crf 24 -b:v 0", "VP9", null));
                    return r;
                case ".wmv":
                case ".asf":
                    r.Add(new Enc("-c:v wmv2 -b:v " + Br(mi.VBitrate * 3 / 2, 6000, 1500, 20000) + "k", "WMV", null));
                    return r;
                case ".mpg":
                case ".mpeg":
                case ".vob":
                case ".m2v":
                    r.Add(new Enc("-c:v mpeg2video -q:v 2", "MPEG-2", null));
                    return r;
                case ".ogv":
                    r.Add(new Enc("-c:v libtheora -q:v 8", "Theora", null));
                    return r;
            }
            bool hevc = mi.VCodec == "hevc" && ext != ".avi" && ext != ".flv" && ext != ".3gp";
            string tag = hevc && IsMov(ext) ? " -tag:v hvc1" : "";
            var nv = new Enc(hevc ? "-c:v hevc_nvenc -preset p5 -tune hq -rc vbr -cq 21 -b:v 0" + tag
                                  : "-c:v h264_nvenc -preset p5 -tune hq -rc vbr -cq 19 -b:v 0 -pix_fmt yuv420p", "NVENC", "nvenc");
            var sw = new Enc(hevc ? "-c:v libx265 -preset fast -crf 20 -x265-params log-level=error" + tag
                                  : "-c:v libx264 -preset veryfast -crf 18 -pix_fmt yuv420p", hevc ? "x265" : "x264", "sw");
            // NVENC first unless it already failed here; re-check every 2 weeks (a driver update may enable it)
            bool tryNv = Settings.Get("enc", "") != "sw";
            long at;
            if (!tryNv && long.TryParse(Settings.Get("enc_at", "0"), out at) && DateTime.UtcNow.Ticks - at > TimeSpan.FromDays(14).Ticks) tryNv = true;
            if (tryNv) { r.Add(nv); triedNv = true; }
            r.Add(sw);
            return r;
        }

        static int Br(int v, int def, int lo, int hi)
        {
            if (v <= 0) v = def;
            return Math.Max(lo, Math.Min(hi, v));
        }

        static int StdMp3(int kb)
        {
            int[] s = { 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320 };
            foreach (int x in s) if (x >= kb) return x;
            return 320;
        }

        string AudioArgs(string ext, bool forVideo)
        {
            if (!mi.HasAudio) return "";
            string c = mi.ACodec;
            if (forVideo)
            {
                switch (ext)
                {
                    case ".webm": return "-c:a libopus -b:a 160k";
                    case ".avi": return "-c:a libmp3lame -q:a 2";
                    case ".wmv": case ".asf": return "-c:a wmav2 -b:a 192k";
                    case ".mpg": case ".mpeg": case ".vob": case ".m2v": return "-c:a mp2 -b:a 256k";
                    case ".ogv": return "-c:a libvorbis -q:a 6";
                }
                return "-c:a aac -b:a " + Br(mi.ABitrate, 192, 128, 320) + "k";
            }
            if (c.StartsWith("pcm_")) return "-c:a " + c;
            switch (c)
            {
                case "flac": return "-c:a flac";
                case "alac": return "-c:a alac";
                case "mp3": return "-c:a libmp3lame " + (mi.ABitrate > 0 ? "-b:a " + StdMp3(mi.ABitrate) + "k" : "-q:a 0");
                case "aac": return "-c:a aac -b:a " + Br(mi.ABitrate, 256, 128, 320) + "k";
                case "vorbis": return "-c:a libvorbis -q:a 6";
                case "opus": return "-c:a libopus -b:a " + Br(mi.ABitrate, 160, 64, 256) + "k";
                case "wmav1": case "wmav2": return "-c:a wmav2 -b:a " + Br(mi.ABitrate, 192, 96, 320) + "k";
                case "ac3": return "-c:a ac3 -b:a " + Br(mi.ABitrate, 384, 192, 640) + "k";
            }
            return "";
        }
    }
}
