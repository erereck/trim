using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace TrimApp
{
    // Filmstrip thumbnails: one tiny ffmpeg per tile (keyframe seek, no accurate decode), a few in parallel.
    sealed class Thumbs
    {
        readonly string src;
        readonly int vidx, px;
        readonly Dispatcher ui;
        readonly Action changed;
        readonly SortedList<long, BitmapSource> cache = new SortedList<long, BitmapSource>();
        readonly object gate = new object();
        readonly HashSet<long> busy = new HashSet<long>();
        readonly List<long> queue = new List<long>();
        readonly List<long> want = new List<long>();
        int running;
        volatile bool stopped;
        const int MaxPar = 4;

        public Thumbs(string src, int vidx, int px, Dispatcher ui, Action changed)
        {
            this.src = src; this.vidx = vidx; this.px = px; this.ui = ui; this.changed = changed;
        }

        public BitmapSource Get(double t, out bool exact)
        {
            long key = (long)Math.Round(t * 1000);
            BitmapSource b;
            if (cache.TryGetValue(key, out b)) { exact = true; return b; }
            exact = false;
            int n = cache.Count;
            if (n == 0) return null;
            IList<long> keys = cache.Keys;
            int lo = 0, hi = n - 1;
            while (lo < hi) { int mid = (lo + hi) / 2; if (keys[mid] < key) lo = mid + 1; else hi = mid; }
            int best = lo;
            if (lo > 0 && Math.Abs(keys[lo - 1] - key) <= Math.Abs(keys[lo] - key)) best = lo - 1;
            return cache.Values[best];
        }

        public void Want(double t) { want.Add((long)Math.Round(t * 1000)); }

        public void Flush()
        {
            lock (gate)
            {
                queue.Clear();
                foreach (var k in want) if (!busy.Contains(k) && !cache.ContainsKey(k) && !queue.Contains(k)) queue.Add(k);
            }
            want.Clear();
            Pump();
        }

        void Pump()
        {
            lock (gate)
            {
                while (!stopped && running < MaxPar && queue.Count > 0)
                {
                    long k = queue[0]; queue.RemoveAt(0);
                    busy.Add(k); running++;
                    ThreadPool.QueueUserWorkItem(Work, k);
                }
            }
        }

        void Work(object o)
        {
            long k = (long)o;
            BitmapSource bmp = null;
            if (!stopped) { try { bmp = Grab(k / 1000.0); } catch { } }
            lock (gate) running--;
            if (bmp != null && !stopped)
            {
                ui.BeginInvoke(new Action(() =>
                {
                    cache[k] = bmp;
                    lock (gate) busy.Remove(k);
                    changed();
                }));
            }
            Pump();
        }

        public void Stop() { stopped = true; }

        BitmapSource Grab(double t)
        {
            string args = "-hide_banner -nostdin -v error -threads 1 -noaccurate_seek -ss " + Ff.T(t) + " -i " + Ff.Q(src) +
                " -map 0:" + vidx + " -frames:v 1 -an -sn -dn -vf \"scale=" + px + ":" + px +
                ":force_original_aspect_ratio=increase:flags=bilinear,crop=" + px + ":" + px + "\" -f image2pipe -c:v bmp -";
            var p = Ff.Run(args, true, false);
            Ff.LowPrio(p);
            var ms = new MemoryStream();
            p.StandardOutput.BaseStream.CopyTo(ms);
            p.WaitForExit();
            Ff.Done(p);
            if (ms.Length < 64) return null;
            ms.Position = 0;
            var dec = new BmpBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            BitmapSource f = dec.Frames[0];
            f.Freeze();
            return f;
        }
    }

    // Waveform peaks at 100/s, decoded at 8 kHz mono, published progressively.
    sealed class Wave
    {
        public const int Rate = 100;
        public float[] Peaks = new float[0];
        public int Count;
        public float Max = 0.0001f;
        volatile bool stopped;

        public void Start(string src, int aidx, double dur, Dispatcher ui, Action changed)
        {
            var th = new Thread(() => Run(src, aidx, dur, ui, changed));
            th.IsBackground = true;
            th.Priority = ThreadPriority.BelowNormal;
            th.Start();
        }

        public void Stop() { stopped = true; }

        void Run(string src, int aidx, double dur, Dispatcher ui, Action changed)
        {
            try
            {
                var p = Ff.Run("-hide_banner -nostdin -v error -i " + Ff.Q(src) + " -map 0:" + aidx +
                    " -vn -sn -dn -ac 1 -ar 8000 -f s16le -", true, false);
                Ff.LowPrio(p);
                var s = p.StandardOutput.BaseStream;
                int cap = (int)(dur * Rate) + 256; if (cap < 1024) cap = 1024;
                float[] arr = new float[cap];
                int n = 0, cnt = 0, mx = 0, gmx = 1;
                const int per = 8000 / Rate;
                byte[] buf = new byte[65536];
                byte pend = 0; bool hasPend = false;
                var sw = Stopwatch.StartNew(); long lastPub = 0;
                int read;
                while (!stopped && (read = s.Read(buf, 0, buf.Length)) > 0)
                {
                    int i = 0;
                    if (hasPend)
                    {
                        int v = (short)(pend | (buf[0] << 8)); if (v < 0) v = -v; if (v > mx) mx = v;
                        if (++cnt == per) { if (n == arr.Length) Array.Resize(ref arr, arr.Length * 2); arr[n++] = mx / 32768f; if (mx > gmx) gmx = mx; cnt = 0; mx = 0; }
                        i = 1; hasPend = false;
                    }
                    for (; i + 1 < read; i += 2)
                    {
                        int v = (short)(buf[i] | (buf[i + 1] << 8)); if (v < 0) v = -v; if (v > mx) mx = v;
                        if (++cnt == per) { if (n == arr.Length) Array.Resize(ref arr, arr.Length * 2); arr[n++] = mx / 32768f; if (mx > gmx) gmx = mx; cnt = 0; mx = 0; }
                    }
                    if (i < read) { pend = buf[i]; hasPend = true; }
                    if (sw.ElapsedMilliseconds - lastPub > 150)
                    {
                        lastPub = sw.ElapsedMilliseconds;
                        Publish(arr, n, gmx, ui, changed);
                    }
                }
                if (cnt > 0) { if (n == arr.Length) Array.Resize(ref arr, arr.Length + 1); arr[n++] = mx / 32768f; if (mx > gmx) gmx = mx; }
                Publish(arr, n, gmx, ui, changed);
                try { if (stopped && !p.HasExited) p.Kill(); } catch { }
                p.WaitForExit();
                Ff.Done(p);
            }
            catch { }
        }

        void Publish(float[] a, int c, int gmx, Dispatcher ui, Action changed)
        {
            if (stopped) return;
            float m = gmx / 32768f;
            ui.BeginInvoke(new Action(() => { Peaks = a; Count = c; Max = Math.Max(0.0001f, m); changed(); }));
        }

        public float PeakIn(double t0, double t1)
        {
            int c = Count;
            if (c == 0) return -1;
            int i0 = (int)(t0 * Rate), i1 = (int)Math.Ceiling(t1 * Rate);
            if (i0 < 0) i0 = 0;
            if (i0 >= c) return -1;
            if (i1 <= i0) i1 = i0 + 1;
            if (i1 > c) i1 = c;
            float m = 0; var a = Peaks;
            for (int i = i0; i < i1; i++) if (a[i] > m) m = a[i];
            return m;
        }
    }
}
