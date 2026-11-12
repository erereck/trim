using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace TrimApp
{
    sealed class TrimView : FrameworkElement
    {
        const double Pad = 16, HW = 14, BarH = 52, VideoGap = 26;

        readonly VisualCollection kids;
        readonly DrawingVisual vHead = new DrawingVisual(), vTime = new DrawingVisual();
        readonly TranslateTransform headX = new TranslateTransform();
        DateTime miniDrawnAt;
        readonly DrawingVisual vVideo = new DrawingVisual(), vStrip = new DrawingVisual(), vTrack = new DrawingVisual(),
                               vBar = new DrawingVisual(), vOver = new DrawingVisual();
        readonly MainWindow win;
        readonly string src;
        readonly MfPlayer player;
        MediaInfo info;
        Thumbs thumbs;
        Wave wave;

        bool audioOnly, opened, playing, previewing, ticking, proxyStarted, usingProxy, failedEarly, showHelp, closed;
        string proxyPath;
        string status = "abrindo…";
        bool statusErr;
        double dur, fps, inT, outT, pos, vs, ve;
        double mfOff, mfDur, volume = 1;   // Media Foundation timeline offset vs ffmpeg (MP4 edit lists), MF duration
        bool precise;
        int rotAngle;
        public Size? InitialSize;

        Rect rVideo, rTrack, rMini, rBar, rPlay, rModeP, rModeF, rCut, rInLbl = Rect.Empty, rOutLbl = Rect.Empty, rToast = Rect.Empty;
        enum Drag { None, In, Out, Scrub, Mini }
        Drag drag;
        Point mouse = new Point(-1000, -1000);
        bool mouseIn;

        Exporter job;
        double jobProg;
        string toastText, toastSub, toastPath;
        bool toastErr;
        readonly DispatcherTimer toastTimer, seekTimer;
        double pendingSeek = -1;
        DateTime lastSeek = DateTime.MinValue;
        bool stripQueued;

        public TrimView(MainWindow w, string path, bool gpu)
        {
            win = w; src = path;
            kids = new VisualCollection(this);
            kids.Add(vVideo); kids.Add(vStrip); kids.Add(vTrack); kids.Add(vHead); kids.Add(vBar); kids.Add(vTime); kids.Add(vOver);
            vHead.Transform = headX;
            Focusable = true;
            FocusVisualStyle = null;
            precise = Settings.Get("mode", "precise") != "fast";

            toastTimer = new DispatcherTimer();
            toastTimer.Interval = TimeSpan.FromSeconds(4.5);
            toastTimer.Tick += delegate { toastTimer.Stop(); toastText = null; toastPath = null; DrawOver(); };
            seekTimer = new DispatcherTimer(DispatcherPriority.Render);
            seekTimer.Interval = TimeSpan.FromMilliseconds(30);
            seekTimer.Tick += delegate { seekTimer.Stop(); ApplySeek(); };

            double vol;
            if (!double.TryParse(Settings.Get("vol", "1"), NumberStyles.Float, CultureInfo.InvariantCulture, out vol)) vol = 1;
            player = new MfPlayer(Dispatcher, gpu && Environment.GetEnvironmentVariable("TRIM_MFSW") != "1");
            volume = Theme.Clamp(vol, 0, 1); player.Volume = volume;
            player.FrameWanted += delegate { if (!playing) { player.Pump(true); RequestFrame(); } };
            player.MediaOpened += OnOpened;
            player.MediaFailed += OnFailed;
            player.MediaEnded += OnEnded;
            if (Environment.GetEnvironmentVariable("TRIM_NOPLAYER") == "1") return;
            try { player.Open(path); }
            catch (Exception e) { status = e.Message; statusErr = true; }
        }

        // ------------------------------------------------------------------ setup

        public void SetInfo(MediaInfo mi)
        {
            if (closed || mi == null) return;
            info = mi;
            if (!mi.Ok)
            {
                if (!opened) { status = mi.Error ?? "não consegui ler este arquivo"; statusErr = true; }
                RedrawAll();
                return;
            }
            audioOnly = !mi.HasVideo;
            fps = mi.HasVideo ? mi.Fps : 0;
            if (mi.Duration > 0) SetDuration(mi.Duration);
            if (Ff.Exe != null && Environment.GetEnvironmentVariable("TRIM_NOTHUMBS") != "1")
            {
                if (mi.HasVideo)
                {
                    double dpi = 1;
                    try { dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip; } catch { }
                    thumbs = new Thumbs(src, mi.VIndex, (int)Math.Ceiling(56 * Math.Max(1, dpi)), Dispatcher, QueueStrip);
                }
                if (mi.HasAudio)
                {
                    wave = new Wave();
                    wave.Start(src, mi.AIndex, Math.Max(dur, mi.Duration), Dispatcher, QueueStrip);
                }
            }
            // ffmpeg seeks relative to the file's start time with edit lists applied; MF uses raw sample times
            if (mi.HasVideo && !usingProxy && Mp4.IsIsoBmff(src)) mfOff = mi.Start + Mp4.VideoEditOffset(src);
            if (opened && mfOff != 0) GoTo(pos);
            if (failedEarly) { failedEarly = false; StartProxy(); }
            else { UpdateRotation(); CheckProxy(); }
            Relayout();
            int tips;
            int.TryParse(Settings.Get("tips", "0"), out tips);
            if (tips < 3 && toastText == null)
            {
                Settings.Set("tips", (tips + 1).ToString());
                Toast("dica: F1 mostra os atalhos", "I / O marcam início e fim · ← → andam " + (audioOnly ? "0.05s" : "1 quadro"), false, null);
            }
        }

        void InitRange() { inT = 0; outT = dur; vs = 0; ve = dur; }

        void SetDuration(double d)
        {
            double old = dur;
            dur = d;
            if (old <= 0) { InitRange(); return; }
            if (outT >= old - 0.002 || outT > dur) outT = dur;
            if (ve >= old - 0.002 || ve > dur) ve = dur;
            if (inT >= outT) inT = 0;
            if (vs >= ve) vs = 0;
            if (pos > dur) pos = dur;
        }

        double MinLen { get { return fps > 0 ? 1 / fps : 0.01; } }
        bool Zoomed { get { return dur > 0 && (ve - vs) < dur - 1e-9; } }

        void OnOpened(object s, EventArgs e)
        {
            if (closed) return;
            opened = true;
            mfDur = player.Duration;
            // ffmpeg's duration is the ruler the cut uses; MF's is only a fallback
            if (mfDur > 0 && (info == null || !info.Ok || info.Duration <= 0)) SetDuration(mfDur);
            if (info == null) audioOnly = !player.HasVideo;
            player.Volume = volume;
            player.Pause();
            UpdateRotation();
            if (CheckProxy()) return;
            status = null; statusErr = false;
            GoTo(pos);
            Relayout();
        }

        void OnFailed(object s, EventArgs e)
        {
            if (closed) return;
            if (usingProxy || Ff.Exe == null)
            {
                status = "não consegui abrir este arquivo"; statusErr = true; RedrawAll(); return;
            }
            if (info == null) { failedEarly = true; status = "analisando…"; RedrawAll(); return; }
            if (!info.Ok) { status = info.Error ?? "formato não suportado"; statusErr = true; RedrawAll(); return; }
            StartProxy();
        }

        void OnEnded(object s, EventArgs e)
        {
            if (closed) return;
            StopTicking();
            playing = false; previewing = false;
            player.Pause();
            GoTo(dur);
            RedrawAll();
        }

        void UpdateRotation()
        {
            rotAngle = 0;
            if (info == null || usingProxy || !opened || !player.HasVideo) return;
            int r = ((-info.Rotation % 360) + 360) % 360;
            if (r == 0) return;
            int nw = player.NaturalVideoWidth, nh = player.NaturalVideoHeight;
            bool swapped = info.W != info.H && nw == info.H && nh == info.W;
            if (r == 90 || r == 270) { if (!swapped) rotAngle = r; }
            else rotAngle = r;
        }

        bool CheckProxy()
        {
            if (usingProxy || proxyStarted || info == null || !info.Ok || !opened) return false;
            if (info.HasVideo && !player.HasVideo) { StartProxy(); return true; }
            return false;
        }

        // When Windows can't decode the file (webm, some HEVC, etc.) build a light preview copy with ffmpeg.
        void StartProxy()
        {
            if (proxyStarted || Ff.Exe == null || info == null) return;
            proxyStarted = true; opened = false;
            status = "preparando prévia…"; statusErr = false;
            RedrawAll();
            string dir = Path.Combine(Path.GetTempPath(), "Trim");
            try { Directory.CreateDirectory(dir); } catch { }
            bool av = info.HasVideo;
            proxyPath = Path.Combine(dir, Guid.NewGuid().ToString("N") + (av ? ".mp4" : ".wav"));
            string args = "-hide_banner -nostdin -v error -nostats -stats_period 0.25 -progress pipe:1 -y -i " + Ff.Q(src) + " " +
                (av ? "-map 0:" + info.VIndex + " -map 0:a:0? -vf \"scale=-2:'min(720,ih)'\" -c:v libx264 -preset ultrafast -tune fastdecode -crf 24 -g 12 -pix_fmt yuv420p -c:a aac -b:a 160k "
                    : "-map 0:a:0 -c:a pcm_s16le ") + Ff.Q(proxyPath);
            double total = Math.Max(info.Duration, 0.1);
            string outPath = proxyPath;
            var th = new Thread(() =>
            {
                int code = -1;
                try
                {
                    var p = Ff.Run(args, true, false);
                    string line;
                    while ((line = p.StandardOutput.ReadLine()) != null)
                    {
                        if (!line.StartsWith("out_time_us=")) continue;
                        long us;
                        if (long.TryParse(line.Substring(12), out us) && us > 0)
                        {
                            int pct = (int)Math.Min(99, us / 1e6 / total * 100);
                            Dispatcher.BeginInvoke(new Action(() => { if (!opened) { status = "preparando prévia… " + pct + "%"; DrawVideo(); } }));
                        }
                    }
                    p.WaitForExit(); code = p.ExitCode; Ff.Done(p);
                }
                catch { }
                int c = code;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (closed) return;
                    if (c == 0 && File.Exists(outPath))
                    {
                        usingProxy = true; rotAngle = 0; status = "abrindo prévia…";
                        mfOff = Mp4.VideoEditOffset(outPath);
                        player.Open(outPath);
                    }
                    else { status = "não consegui abrir este arquivo"; statusErr = true; }
                    RedrawAll();
                }));
            });
            th.IsBackground = true;
            th.Start();
        }

        // ------------------------------------------------------------------ playback

        void StartTicking() { if (!ticking) { CompositionTarget.Rendering += OnTick; ticking = true; } }
        void StopTicking() { if (ticking) { CompositionTarget.Rendering -= OnTick; ticking = false; } }

        void TogglePlay() { if (playing) Pause(); else Play(false); }

        void Play(bool preview)
        {
            if (!opened || dur <= 0) return;
            previewing = preview;
            double end = fps > 0 ? dur - 1.5 / fps : dur - 0.02;
            if (preview) GoTo(inT);
            else if (pos >= end) GoTo(inT < end ? inT : 0);
            ApplySeek();
            player.Play();
            playing = true;
            StartTicking();
            DrawBar();
        }

        void Pause()
        {
            if (!playing) return;
            player.Pause();
            playing = false; previewing = false;
            StopTicking();
            GoTo(player.Time - mfOff);
            RedrawAll();
        }

        void OnTick(object s, EventArgs e)
        {
            if (!playing)
            {
                if (frameWanted)
                {
                    // keep polling until the frame on screen is the one we seeked to (or give up after a while)
                    player.Pump(false);
                    double tol = fps > 0 ? 0.75 / fps : 0.1;
                    bool match = !player.HasVideo || (seekTargetMf >= 0 && Math.Abs(player.FramePts - seekTargetMf) <= tol);
                    if (match || (DateTime.UtcNow - frameWantedAt).TotalMilliseconds > 1500) frameWanted = false;
                }
                if (!frameWanted) StopTicking();
                return;
            }
            player.Pump(false);
            double p = player.Time - mfOff;
            if (previewing && p >= outT - 1e-4)
            {
                player.Pause(); playing = false; previewing = false; StopTicking();
                ShowOutFrame();
                RedrawAll();
                return;
            }
            pos = Math.Min(p, dur);
            var now = DateTime.UtcNow;
            if (Zoomed && (pos > ve || pos < vs))
            {
                double span = ve - vs; vs = pos - span * 0.05; ve = vs + span; ClampView();
                DrawStrip(); DrawTrack(); miniDrawnAt = now;
            }
            else if (Zoomed && (now - miniDrawnAt).TotalMilliseconds >= 200) { DrawTrack(); miniDrawnAt = now; }
            UpdateHead();                                                     // just moves a transform
            if ((now - timeDrawnAt).TotalMilliseconds >= 50) DrawTime();      // clock at most 20x/s
        }

        int FrameAt(double t) { return (int)Math.Floor(t * fps + 1e-6); }
        int LastFrame { get { return Math.Max(0, (int)Math.Ceiling(dur * fps - 1e-6) - 1); } }

        void ShowFrame(int k)
        {
            int lf = LastFrame;
            if (k > lf) k = lf;
            if (k < 0) k = 0;
            pos = k / fps;
            Seek(Math.Max(0, Math.Min((k + 0.5) / fps, dur - 0.001)));
        }

        void ShowTime(double t)
        {
            pos = Theme.Clamp(t, 0, dur);
            Seek(pos);
        }

        void GoTo(double t)
        {
            t = Theme.Clamp(t, 0, Math.Max(0, dur));
            if (fps > 0) ShowFrame(FrameAt(t)); else ShowTime(t);
            EnsureVisible();
        }

        void ShowOutFrame()
        {
            if (fps > 0) ShowFrame((int)Math.Ceiling(outT * fps - 1e-6) - 1); else ShowTime(outT);
            EnsureVisible();
        }

        void Seek(double t)
        {
            pendingSeek = t;
            if (playing || (DateTime.UtcNow - lastSeek).TotalMilliseconds >= 30) ApplySeek();
            else if (!seekTimer.IsEnabled) seekTimer.Start();
        }

        void ApplySeek()
        {
            if (pendingSeek < 0 || !opened) return;
            double t = pendingSeek + mfOff;
            if (mfDur > 0) t = Math.Min(t, mfDur - 0.001);
            seekTargetMf = Math.Max(0, t);
            player.Time = seekTargetMf;
            RequestFrame();
            pendingSeek = -1;
            lastSeek = DateTime.UtcNow;
        }

        void Step(int dir, ModifierKeys mods)
        {
            if (dur <= 0) return;
            if (playing) Pause();
            if ((mods & ModifierKeys.Control) != 0) GoTo(pos + dir * 5);
            else if ((mods & ModifierKeys.Shift) != 0) GoTo(pos + dir * 1);
            else if (fps > 0) { ShowFrame(FrameAt(pos) + dir); EnsureVisible(); }
            else GoTo(pos + dir * 0.05);
            RedrawAll();
        }

        void SetIn()
        {
            if (dur <= 0) return;
            double t = fps > 0 ? FrameAt(pos) / fps : pos;
            if (t >= outT) outT = dur;
            inT = Math.Max(0, Math.Min(t, outT - MinLen));
            RedrawAll();
        }

        void SetOut()
        {
            if (dur <= 0) return;
            double t = fps > 0 ? (FrameAt(pos) + 1) / fps : pos;
            if (fps > 0 && dur - t < 0.5 / fps) t = dur;
            t = Math.Min(t, dur);
            if (t <= inT) inT = 0;
            outT = Math.Min(dur, Math.Max(t, inT + MinLen));
            RedrawAll();
        }

        double Snap(double t) { return fps > 0 ? Math.Round(t * fps) / fps : Math.Round(t * 1000) / 1000; }

        public void ApplyEdit(bool isIn, double t)
        {
            if (dur <= 0) return;
            t = Snap(Theme.Clamp(t, 0, dur));
            if (isIn)
            {
                inT = Theme.Clamp(t, 0, Math.Max(0, outT - MinLen));
                GoTo(inT);
            }
            else
            {
                if (fps > 0 && dur - t < 0.5 / fps) t = dur;
                outT = Theme.Clamp(t, inT + MinLen, dur);
                ShowOutFrame();
            }
            RedrawAll();
        }

        void SetMode(bool p)
        {
            if (job != null) return;
            precise = p;
            Settings.Set("mode", p ? "precise" : "fast");
            DrawBar(); DrawOver();
        }

        // ------------------------------------------------------------------ view / zoom

        double TToX(double t) { return rTrack.Left + (t - vs) / Math.Max(1e-9, ve - vs) * rTrack.Width; }
        double XToT(double x) { return vs + (x - rTrack.Left) / Math.Max(1e-9, rTrack.Width) * (ve - vs); }

        void ClampView()
        {
            double span = ve - vs;
            if (span > dur) { vs = 0; ve = dur; return; }
            if (vs < 0) { vs = 0; ve = span; }
            if (ve > dur) { ve = dur; vs = dur - span; }
        }

        void Zoom(double factor, double anchor)
        {
            if (dur <= 0) return;
            double span = ve - vs;
            double minSpan = fps > 0 ? Math.Max(24 / fps, 0.4) : 0.25;
            double ns = Theme.Clamp(span * factor, Math.Min(minSpan, dur), dur);
            double frac = (anchor - vs) / Math.Max(1e-9, span);
            vs = anchor - frac * ns; ve = vs + ns;
            ClampView();
            RedrawAll();
        }

        void EnsureVisible()
        {
            if (!Zoomed) return;
            if (pos < vs || pos > ve)
            {
                double span = ve - vs; vs = pos - span / 2; ve = vs + span; ClampView(); DrawStrip();
            }
        }

        // ------------------------------------------------------------------ export

        void StartExport()
        {
            if (job != null || dur <= 0) return;
            if (Ff.Exe == null) { Toast("ffmpeg não encontrado", "instale o ffmpeg (ou o ShareX) para poder cortar", true, null); return; }
            if (info == null) { Toast("ainda analisando o arquivo…", null, true, null); return; }
            if (!info.Ok) { Toast("não consigo cortar este arquivo", info.Error, true, null); return; }
            if (playing) Pause();
            string dst;
            try { dst = Naming.Unique(src); }
            catch (Exception ex) { Toast("não deu pra criar o arquivo", ex.Message, true, null); return; }
            var j = new Exporter(src, dst, inT, outT, dur, fps, info, precise);
            job = j; jobProg = 0;
            j.Progress += p => Dispatcher.BeginInvoke(new Action(() => { if (job == j) { jobProg = p; DrawBar(); } }));
            j.Done += (ok, msg, label, secs) => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (job != j) return;
                job = null; jobProg = 0;
                if (ok) Toast(Path.GetFileName(j.Dst), string.Format(CultureInfo.InvariantCulture, "{0:0.0}s · {1}", secs, label), false, j.Dst);
                else if (msg == "cancel") Toast("corte cancelado", null, false, null);
                else Toast("não deu pra cortar", msg, true, null);
                DrawBar(); DrawOver();
            }));
            j.Start();
            DrawBar();
        }

        void Toast(string text, string sub, bool err, string path)
        {
            toastText = text; toastSub = sub; toastErr = err; toastPath = path;
            toastTimer.Stop(); toastTimer.Start();
            DrawOver();
        }

        void Reveal(string path)
        {
            try { Process.Start("explorer.exe", "/select,\"" + path + "\""); } catch { }
        }

        public void Shutdown()
        {
            if (closed) return;
            closed = true;
            StopTicking();
            if (job != null) job.CancelAndWait();
            if (thumbs != null) thumbs.Stop();
            if (wave != null) wave.Stop();
            Ff.KillAll();
            try { player.Close(); } catch { }
            if (proxyPath != null) { try { File.Delete(proxyPath); } catch { } }
            if (!Shot.Active) Settings.Set("vol", volume.ToString("0.##", CultureInfo.InvariantCulture));
        }

        // ------------------------------------------------------------------ input

        public void OnKey(KeyEventArgs e)
        {
            if (win.Editing || (Shot.Active && !Shot.Keys)) return;
            var mods = Keyboard.Modifiers;
            Key k = e.Key == Key.System ? e.SystemKey : e.Key;
            switch (k)
            {
                case Key.Space: TogglePlay(); break;
                case Key.Left: Step(-1, mods); break;
                case Key.Right: Step(1, mods); break;
                case Key.I: case Key.OemOpenBrackets: SetIn(); break;
                case Key.O: case Key.OemCloseBrackets: SetOut(); break;
                case Key.Home:
                    if (playing) Pause();
                    if ((mods & ModifierKeys.Control) != 0) GoTo(0); else GoTo(inT);
                    RedrawAll(); break;
                case Key.End:
                    if (playing) Pause();
                    if ((mods & ModifierKeys.Control) != 0) GoTo(dur); else ShowOutFrame();
                    RedrawAll(); break;
                case Key.P: Play(true); break;
                case Key.M:
                    player.IsMuted = !player.IsMuted;
                    Toast(player.IsMuted ? "som desligado" : "som ligado", null, false, null); break;
                case Key.Up: case Key.Down:
                    volume = Theme.Clamp(Math.Round((volume + (k == Key.Up ? 0.1 : -0.1)) * 10) / 10, 0, 1); player.Volume = volume;
                    player.IsMuted = false;
                    Toast("volume " + (int)Math.Round(volume * 100) + "%", null, false, null); break;
                case Key.Tab: SetMode(!precise); break;
                case Key.Enter: StartExport(); break;
                case Key.S:
                    if ((mods & ModifierKeys.Control) != 0) StartExport(); else return;
                    break;
                case Key.Escape:
                    if (showHelp) { showHelp = false; DrawOver(); }
                    else if (job != null) job.Cancel();
                    else win.Close();
                    break;
                case Key.OemPlus: case Key.Add: Zoom(0.6, pos); break;
                case Key.OemMinus: case Key.Subtract: Zoom(1 / 0.6, pos); break;
                case Key.D0: case Key.NumPad0: vs = 0; ve = dur; RedrawAll(); break;
                case Key.F1: case Key.H: case Key.OemQuestion: showHelp = !showHelp; DrawOver(); break;
                default: return;
            }
            e.Handled = true;
        }

        Rect TrackBand
        {
            get { return new Rect(rTrack.Left - HW - 4, rTrack.Top - 10, rTrack.Width + 2 * HW + 8, rTrack.Height + 20); }
        }

        Drag HitTrack(Point p)
        {
            if (dur <= 0 || rTrack.Width < 4) return Drag.None;
            if (Zoomed)
            {
                var m = rMini; m.Inflate(0, 6);
                if (m.Contains(p)) return Drag.Mini;
            }
            if (!TrackBand.Contains(p)) return Drag.None;
            double xi = TToX(inT), xo = TToX(outT);
            bool onIn = p.X >= xi - HW - 4 && p.X <= xi + 4;
            bool onOut = p.X >= xo - 4 && p.X <= xo + HW + 4;
            if (onIn && onOut)
            {
                if (p.X <= xi) return Drag.In;
                if (p.X >= xo) return Drag.Out;
                return (p.X - xi < xo - p.X) ? Drag.In : Drag.Out;
            }
            if (onIn) return Drag.In;
            if (onOut) return Drag.Out;
            if (p.X >= rTrack.Left - 2 && p.X <= rTrack.Right + 2) return Drag.Scrub;
            return Drag.None;
        }

        void DragTo(double x)
        {
            double t = XToT(x);
            switch (drag)
            {
                case Drag.In:
                    inT = Theme.Clamp(Snap(t), 0, Math.Max(0, outT - MinLen));
                    if (fps > 0) ShowFrame((int)Math.Round(inT * fps)); else ShowTime(inT);
                    break;
                case Drag.Out:
                    {
                        double v = Snap(t);
                        if (fps > 0 && dur - v < 0.5 / fps) v = dur;
                        outT = Theme.Clamp(v, inT + MinLen, dur);
                        if (fps > 0) ShowFrame((int)Math.Ceiling(outT * fps - 1e-6) - 1); else ShowTime(outT);
                        break;
                    }
                case Drag.Scrub:
                    t = Theme.Clamp(t, 0, dur);
                    if (fps > 0) ShowFrame(FrameAt(t)); else ShowTime(t);
                    break;
                case Drag.Mini:
                    {
                        double mt = (x - rMini.Left) / Math.Max(1, rMini.Width) * dur;
                        double span = ve - vs; vs = mt - span / 2; ve = vs + span; ClampView();
                        DrawStrip();
                        break;
                    }
            }
            DrawTrack(); UpdateHead(); DrawBar(); DrawTime(); DrawOver();
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            Focus();
            Point p = e.GetPosition(this);
            e.Handled = true;
            if (showHelp) { showHelp = false; DrawOver(); return; }
            if (toastText != null && toastPath != null && rToast.Contains(p)) { Reveal(toastPath); return; }
            if (rPlay.Contains(p)) { TogglePlay(); return; }
            if (rModeP.Contains(p)) { SetMode(true); return; }
            if (rModeF.Contains(p)) { SetMode(false); return; }
            if (rCut.Contains(p)) { if (job != null) job.Cancel(); else StartExport(); return; }
            if (rInLbl.Contains(p)) { if (playing) Pause(); win.EditTime(true, rInLbl, inT); return; }
            if (rOutLbl.Contains(p)) { if (playing) Pause(); win.EditTime(false, rOutLbl, outT); return; }
            var hit = HitTrack(p);
            if (hit != Drag.None)
            {
                if (playing) Pause();
                drag = hit;
                CaptureMouse();
                DragTo(p.X);
                return;
            }
            if (rVideo.Contains(p) && !audioOnly) TogglePlay();
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            if (drag != Drag.None)
            {
                drag = Drag.None;
                ReleaseMouseCapture();
                ApplySeek();
                RedrawAll();
            }
        }

        protected override void OnLostMouseCapture(MouseEventArgs e)
        {
            if (drag != Drag.None) { drag = Drag.None; RedrawAll(); }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            mouse = e.GetPosition(this);
            mouseIn = true;
            if (drag != Drag.None) { DragTo(mouse.X); return; }
            var h = HitTrack(mouse);
            Cursor c = Cursors.Arrow;
            if (h == Drag.In || h == Drag.Out) c = Cursors.SizeWE;
            else if (rPlay.Contains(mouse) || rModeP.Contains(mouse) || rModeF.Contains(mouse) || rCut.Contains(mouse) ||
                     rInLbl.Contains(mouse) || rOutLbl.Contains(mouse) || (toastPath != null && rToast.Contains(mouse)) || h == Drag.Mini)
                c = Cursors.Hand;
            if (Cursor != c) Cursor = c;
            DrawOver(); DrawBar();
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            mouseIn = false;
            mouse = new Point(-1000, -1000);
            DrawOver(); DrawBar();
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            Point p = e.GetPosition(this);
            int notches = e.Delta / 120;
            if (notches == 0) notches = Math.Sign(e.Delta);
            var band = TrackBand; band.Union(rMini);
            if (band.Contains(p))
            {
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
                {
                    double span = ve - vs; vs -= notches * span * 0.15; ve = vs + span; ClampView(); RedrawAll();
                }
                else Zoom(Math.Pow(0.8, notches), Theme.Clamp(XToT(p.X), 0, dur));
            }
            else if (rVideo.Contains(p) || audioOnly)
            {
                Step(-Math.Sign(e.Delta), Keyboard.Modifiers);
            }
            e.Handled = true;
        }

        // Dev-only state injection for off-screen screenshots ("in=1.5;out=3.5;pos=2;zoom=0.4;hover=500,600;help;toast;fast;job=0.4")
        public void Debug(string spec)
        {
            foreach (var part in spec.Split(';'))
            {
                string[] kv = part.Split('=');
                string k = kv[0].Trim(), v = kv.Length > 1 ? kv[1].Trim() : "";
                double d; double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d);
                switch (k)
                {
                    case "in": inT = d; break;
                    case "out": outT = d; break;
                    case "pos": GoTo(d); break;
                    case "zoom": Zoom(d, pos); break;
                    case "help": showHelp = true; break;
                    case "play": Play(false); break;
                    case "fast": precise = false; break;
                    case "toast": Toast("video_trim.mp4", "0.4s · x264", false, "x"); break;
                    case "err": Toast("não deu pra cortar", "Invalid data found when processing input", true, null); break;
                    case "job": jobProg = d; job = new Exporter(src, "x", 0, 1, 1, 0, info, true); break;
                    case "dragin": drag = Drag.In; DragTo(d); drag = Drag.None; break;
                    case "dragout": drag = Drag.Out; DragTo(d); drag = Drag.None; break;
                    case "key":
                        {
                            Key key;
                            if (Enum.TryParse(v, out key)) { Shot.Keys = true; OnKey(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this), 0, key) { RoutedEvent = Keyboard.KeyDownEvent }); Shot.Keys = false; }
                            break;
                        }
                    case "hover":
                        {
                            var xy = v.Split(',');
                            mouse = new Point(double.Parse(xy[0], CultureInfo.InvariantCulture), double.Parse(xy[1], CultureInfo.InvariantCulture));
                            mouseIn = true; break;
                        }
                }
            }
            RedrawAll();
        }

        public void LogState()
        {
            Shot.Log(string.Format(CultureInfo.InvariantCulture,
                "dur={0} fps={1} pos={2} playerPos={3} natural={4}x{5} rot={6} hasVideo={7} opened={8} proxy={9} audioOnly={10}",
                dur, fps, pos, player.Time, player.NaturalVideoWidth, player.NaturalVideoHeight, rotAngle,
                player.HasVideo, opened, usingProxy, audioOnly));
        }

        bool frameWanted;
        DateTime frameWantedAt;
        double seekTargetMf = -1;

        void RequestFrame()
        {
            frameWanted = true;
            frameWantedAt = DateTime.UtcNow;
            StartTicking();
        }

        void DrawFrame(DrawingContext dc, Rect r)
        {
            int pw = (int)Math.Round(r.Width * Theme.Dpi), ph = (int)Math.Round(r.Height * Theme.Dpi);
            if (player.EnsureTarget(pw, ph)) player.Pump(true);
            var img = player.Frame;
            if (img != null) dc.DrawImage(img, r);
        }

        // ------------------------------------------------------------------ layout & drawing

        protected override int VisualChildrenCount { get { return kids.Count; } }
        protected override Visual GetVisualChild(int index) { return kids[index]; }

        protected override Size MeasureOverride(Size avail)
        {
            bool inf = double.IsInfinity(avail.Width) || double.IsInfinity(avail.Height);
            if (inf && InitialSize.HasValue) return InitialSize.Value;
            return new Size(double.IsInfinity(avail.Width) ? 960 : avail.Width, double.IsInfinity(avail.Height) ? 600 : avail.Height);
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            Relayout();
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            Relayout();
        }

        protected override void OnRender(DrawingContext dc)
        {
            dc.DrawRectangle(Theme.Bg, null, new Rect(RenderSize));
        }

        void Relayout()
        {
            double W = ActualWidth, H = ActualHeight;
            if (W <= 0 || H <= 0) return;
            try { Theme.Dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip; } catch { }
            rBar = new Rect(Pad, H - BarH - 8, W - 2 * Pad, BarH);
            double th = audioOnly ? 84 : 56;
            rTrack = new Rect(Pad + HW, rBar.Top - 8 - th, Math.Max(10, W - 2 * Pad - 2 * HW), th);
            rMini = new Rect(rTrack.Left, rTrack.Top - 15, rTrack.Width, 4);
            rVideo = new Rect(Pad, Pad, W - 2 * Pad, Math.Max(0, rTrack.Top - VideoGap - Pad));
            double cy = rBar.Top + BarH / 2;
            rPlay = new Rect(rBar.Left, cy - 18, 36, 36);
            rCut = new Rect(rBar.Right - 104, cy - 18, 104, 36);
            rModeF = new Rect(rCut.Left - 12 - 84, cy - 18, 84, 36);
            rModeP = new Rect(rModeF.Left - 84, cy - 18, 84, 36);
            RedrawAll();
        }

        void RedrawAll()
        {
            if (ActualWidth <= 0) return;
            timeDirty = true;
            DrawVideo(); DrawStrip(); DrawTrack(); DrawHead(); DrawBar(); DrawTime(); DrawOver();
        }

        void QueueStrip()
        {
            if (stripQueued) return;
            stripQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => { stripQueued = false; DrawStrip(); }));
        }

        void DrawVideo()
        {
            using (var dc = vVideo.RenderOpen())
            {
                Rect r = rVideo;
                if (r.Width < 4 || r.Height < 4) return;
                if (audioOnly && info != null) { DrawAudioCard(dc, r); return; }
                if (opened && player.HasVideo)
                {
                    double nw = player.NaturalVideoWidth, nh = player.NaturalVideoHeight;
                    if ((nw <= 0 || nh <= 0) && info != null) { nw = info.W; nh = info.H; }
                    if (nw <= 0 || nh <= 0) { nw = 16; nh = 9; }
                    bool quarter = rotAngle == 90 || rotAngle == 270;
                    double dw = quarter ? nh : nw, dh = quarter ? nw : nh;
                    double s = Math.Min(r.Width / dw, r.Height / dh);
                    double w = dw * s, h = dh * s;
                    // snap to device pixels so the frame (already scaled by the GPU) is blitted 1:1
                    double k = Theme.Dpi;
                    double x0 = Math.Round((r.Left + (r.Width - w) / 2) * k) / k, y0 = Math.Round((r.Top + (r.Height - h) / 2) * k) / k;
                    var R = new Rect(x0, y0, Math.Round(w * k) / k, Math.Round(h * k) / k);
                    w = R.Width; h = R.Height;
                    dc.DrawRectangle(Brushes.Black, null, R);
                    if (rotAngle != 0)
                    {
                        double cx = R.Left + w / 2, cy = R.Top + h / 2;
                        dc.PushTransform(new RotateTransform(rotAngle, cx, cy));
                        DrawFrame(dc, quarter ? new Rect(cx - h / 2, cy - w / 2, h, w) : R);
                        dc.Pop();
                    }
                    else DrawFrame(dc, R);
                    Corners(dc, R, 10);   // cheap rounded corners (no per-pixel clip on the video)
                    return;
                }
                string msg = status ?? "";
                if (msg.Length > 0)
                {
                    var ft = Theme.Txt(msg, 13, statusErr ? Theme.Danger : Theme.Text2, false);
                    ft.MaxTextWidth = Math.Max(50, r.Width - 40);
                    ft.TextAlignment = TextAlignment.Center;
                    dc.DrawText(ft, new Point(r.Left + 20, r.Top + r.Height / 2 - ft.Height / 2));
                }
            }
        }

        void DrawAudioCard(DrawingContext dc, Rect r)
        {
            string name = Path.GetFileName(src);
            var ft = Theme.Txt(name, 15, Theme.Text, true);
            ft.MaxTextWidth = Math.Max(50, r.Width - 40);
            ft.MaxLineCount = 1;
            ft.Trimming = TextTrimming.CharacterEllipsis;
            ft.TextAlignment = TextAlignment.Center;
            string meta = (info.ACodec ?? "").ToUpperInvariant();
            if (info.ABitrate > 0) meta += " · " + info.ABitrate + " kb/s";
            if (dur > 0) meta += " · " + Theme.Fmt(dur);
            if (statusErr && status != null) meta = status;
            var fm = Theme.Txt(meta, 12, statusErr ? Theme.Danger : Theme.Text3, false);
            fm.MaxTextWidth = ft.MaxTextWidth;
            fm.TextAlignment = TextAlignment.Center;
            double total = ft.Height + 4 + fm.Height;
            double y = r.Top + Math.Max(0, (r.Height - total) / 2);
            dc.DrawText(ft, new Point(r.Left + 20, y));
            dc.DrawText(fm, new Point(r.Left + 20, y + ft.Height + 4));
        }

        void DrawStrip()
        {
            using (var dc = vStrip.RenderOpen())
            {
                if (dur <= 0 || rTrack.Width < 4) return;
                Rect tr = rTrack;
                dc.DrawRoundedRectangle(Theme.Surface, null, tr, 6, 6);
                dc.PushClip(new RectangleGeometry(tr, 6, 6));
                if (!audioOnly && thumbs != null)
                {
                    double tile = tr.Height;
                    double tileDur = (ve - vs) * tile / tr.Width;
                    double step = dur; int guard = 0;
                    while (step > tileDur && guard++ < 40) step /= 2;
                    int n = (int)Math.Ceiling(tr.Width / tile);
                    for (int i = 0; i < n; i++)
                    {
                        double x = tr.Left + i * tile;
                        double tc = Theme.Clamp(XToT(x + tile / 2), 0, Math.Max(0, dur - 0.001));
                        long k = (long)Math.Floor(tc / step);
                        double tk = (k + 0.5) * step;
                        if (tk >= dur) tk = Math.Max(0, dur - step / 2);
                        bool exact;
                        var bmp = thumbs.Get(tk, out exact);
                        if (bmp != null) dc.DrawImage(bmp, new Rect(x, tr.Top, tile, tile));
                        if (!exact) thumbs.Want(tk);
                    }
                    thumbs.Flush();
                    if (wave != null && wave.Count > 0)
                    {
                        var wr = new Rect(tr.Left, tr.Bottom - 18, tr.Width, 18);
                        dc.DrawRectangle(Theme.WaveBand, null, wr);
                        DrawWave(dc, new Rect(wr.Left, wr.Top + 2, wr.Width, wr.Height - 4), Theme.WaveVid);
                    }
                }
                else if (wave != null)
                {
                    var wr = tr; wr.Inflate(0, -8);
                    DrawWave(dc, wr, Theme.WaveAud);
                }
                dc.Pop();
            }
        }

        void DrawWave(DrawingContext dc, Rect r, Brush b)
        {
            if (wave == null || wave.Count == 0) return;
            var geo = new StreamGeometry();
            float norm = wave.Max;
            using (var ctx = geo.Open())
            {
                for (double x = r.Left; x < r.Right; x += 3)
                {
                    float m = wave.PeakIn(XToT(x), XToT(x + 3));
                    if (m < 0) continue;
                    double a = Math.Pow(Math.Min(1, m / norm), 0.65);
                    double h = Math.Max(1, a * r.Height);
                    double y = r.Top + (r.Height - h) / 2;
                    double w = Math.Min(2, r.Right - x);
                    ctx.BeginFigure(new Point(x, y), true, true);
                    ctx.LineTo(new Point(x + w, y), false, false);
                    ctx.LineTo(new Point(x + w, y + h), false, false);
                    ctx.LineTo(new Point(x, y + h), false, false);
                }
            }
            geo.Freeze();
            dc.DrawGeometry(b, null, geo);
        }

        void DrawTrack()
        {
            using (var dc = vTrack.RenderOpen())
            {
                if (dur <= 0 || rTrack.Width < 4) return;
                Rect tr = rTrack;
                double xi = TToX(inT), xo = TToX(outT);
                dc.PushClip(new RectangleGeometry(tr, 6, 6));
                if (xi > tr.Left) dc.DrawRectangle(Theme.Dim, null, new Rect(tr.Left, tr.Top, Math.Min(xi, tr.Right) - tr.Left, tr.Height));
                if (xo < tr.Right) { double a = Math.Max(xo, tr.Left); dc.DrawRectangle(Theme.Dim, null, new Rect(a, tr.Top, tr.Right - a, tr.Height)); }
                dc.Pop();

                dc.PushClip(new RectangleGeometry(new Rect(tr.Left - HW, tr.Top - 6, tr.Width + 2 * HW, tr.Height + 12)));
                if (xo >= tr.Left - HW && xi <= tr.Right + HW)
                {
                    var outer = new Rect(xi - HW, tr.Top - 2.5, Math.Max(0, xo - xi) + 2 * HW, tr.Height + 5);
                    var inner = new Rect(xi, tr.Top + 0.5, Math.Max(0, xo - xi), tr.Height - 1);
                    var g = new GeometryGroup();
                    g.FillRule = FillRule.EvenOdd;
                    g.Children.Add(new RectangleGeometry(outer, 6, 6));
                    g.Children.Add(new RectangleGeometry(inner, 1.5, 1.5));
                    bool hot = drag == Drag.In || drag == Drag.Out;
                    dc.DrawGeometry(hot ? Theme.AccentHi : Theme.Accent, null, g);
                    double cy = tr.Top + tr.Height / 2;
                    Chevron(dc, xi - HW / 2, cy, true);
                    Chevron(dc, xo + HW / 2, cy, false);
                }
                dc.Pop();

                if (Zoomed)
                {
                    dc.DrawRoundedRectangle(Theme.Surface2, null, rMini, 2, 2);
                    double ms = rMini.Width / dur;
                    dc.DrawRectangle(Theme.AccentSoft, null, new Rect(rMini.Left + inT * ms, rMini.Top, Math.Max(1, (outT - inT) * ms), rMini.Height));
                    double a = rMini.Left + vs * ms, b = rMini.Left + ve * ms;
                    var vr = new Rect(a, rMini.Top - 1, Math.Max(4, b - a), rMini.Height + 2);
                    dc.DrawRoundedRectangle(null, Theme.MiniPen, vr, 2, 2);
                    dc.DrawRectangle(Theme.Text, null, new Rect(rMini.Left + pos * ms - 0.75, rMini.Top - 2, 1.5, rMini.Height + 4));
                }
            }
        }

        // playhead: drawn once at x=0, moved with a transform (no redraw while playing)
        void DrawHead()
        {
            using (var dc = vHead.RenderOpen())
            {
                if (dur <= 0 || rTrack.Width < 4) return;
                Rect tr = rTrack;
                dc.DrawRoundedRectangle(Theme.Shadow, null, new Rect(-2.5, tr.Top - 8, 5, tr.Height + 16), 2.5, 2.5);
                dc.DrawRoundedRectangle(Theme.Text, null, new Rect(-1.5, tr.Top - 7, 3, tr.Height + 14), 1.5, 1.5);
            }
            UpdateHead();
        }

        void UpdateHead()
        {
            double xp = TToX(pos);
            bool vis = dur > 0 && xp >= rTrack.Left - 2 && xp <= rTrack.Right + 2;
            headX.X = vis ? xp : -10000;
        }

        string timeShown;
        DateTime timeDrawnAt;

        void DrawTime()
        {
            string s = Theme.Fmt(pos);
            if (s == timeShown && !timeDirty) return;
            timeShown = s; timeDirty = false; timeDrawnAt = DateTime.UtcNow;
            using (var dc = vTime.RenderOpen())
            {
                if (rBar.Width < 10) return;
                double cy = rBar.Top + BarH / 2;
                double x = rPlay.Right + 12;
                var t1 = Theme.Txt(s, 14, Theme.Text, true);
                dc.DrawText(t1, new Point(x, cy - t1.Height / 2));
                var t2 = Theme.Txt("  /  " + Theme.Fmt(dur), 13, Theme.Text3, false);
                dc.DrawText(t2, new Point(x + t1.Width, cy - t2.Height / 2 + 0.5));
            }
        }
        bool timeDirty = true;

        static void Corners(DrawingContext dc, Rect R, double r)
        {
            var g = new StreamGeometry();
            var sz = new Size(r, r);
            const SweepDirection ccw = SweepDirection.Counterclockwise;
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(R.Left, R.Top), true, true);
                c.LineTo(new Point(R.Left + r, R.Top), false, false);
                c.ArcTo(new Point(R.Left, R.Top + r), sz, 0, false, ccw, false, false);
                c.BeginFigure(new Point(R.Right, R.Top), true, true);
                c.LineTo(new Point(R.Right, R.Top + r), false, false);
                c.ArcTo(new Point(R.Right - r, R.Top), sz, 0, false, ccw, false, false);
                c.BeginFigure(new Point(R.Right, R.Bottom), true, true);
                c.LineTo(new Point(R.Right - r, R.Bottom), false, false);
                c.ArcTo(new Point(R.Right, R.Bottom - r), sz, 0, false, ccw, false, false);
                c.BeginFigure(new Point(R.Left, R.Bottom), true, true);
                c.LineTo(new Point(R.Left, R.Bottom - r), false, false);
                c.ArcTo(new Point(R.Left + r, R.Bottom), sz, 0, false, ccw, false, false);
            }
            g.Freeze();
            dc.DrawGeometry(Theme.Bg, null, g);
        }

        static void Chevron(DrawingContext dc, double cx, double cy, bool left)
        {
            double d = left ? 1 : -1;
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(cx + 1.6 * d, cy - 4.5), false, false);
                c.LineTo(new Point(cx - 1.6 * d, cy), true, true);
                c.LineTo(new Point(cx + 1.6 * d, cy + 4.5), true, true);
            }
            g.Freeze();
            dc.DrawGeometry(null, Theme.InkPen, g);
        }

        void DrawBar()
        {
            using (var dc = vBar.RenderOpen())
            {
                if (rBar.Width < 10) return;
                double cy = rBar.Top + BarH / 2;
                Point m = mouse;

                // play / pause
                var pc = new Point(rPlay.Left + 18, cy);
                dc.DrawEllipse(rPlay.Contains(m) ? Theme.Surface3 : Theme.Surface2, null, pc, 18, 18);
                if (playing)
                {
                    dc.DrawRoundedRectangle(Theme.Text, null, new Rect(pc.X - 5.5, cy - 6.5, 4, 13), 1.3, 1.3);
                    dc.DrawRoundedRectangle(Theme.Text, null, new Rect(pc.X + 1.5, cy - 6.5, 4, 13), 1.3, 1.3);
                }
                else
                {
                    var g = new StreamGeometry();
                    using (var c = g.Open())
                    {
                        c.BeginFigure(new Point(pc.X - 3.5, cy - 6), true, true);
                        c.LineTo(new Point(pc.X + 6.5, cy), true, true);
                        c.LineTo(new Point(pc.X - 3.5, cy + 6), true, true);
                    }
                    g.Freeze();
                    dc.DrawGeometry(Theme.Text, Theme.TextPen, g);
                }

                // time text lives in its own visual (vTime); reserve its width here
                double x = rPlay.Right + 12;
                x += Theme.Txt(Theme.Fmt(dur), 14, Theme.Text, true).Width;
                x += Theme.Txt("  /  " + Theme.Fmt(dur), 13, Theme.Text3, false).Width;
                double leftEnd = x + 18;

                // mode toggle
                var pill = Rect.Union(rModeP, rModeF);
                dc.DrawRoundedRectangle(Theme.Surface, null, pill, 18, 18);
                var sel = precise ? rModeP : rModeF; sel.Inflate(-3, -3);
                dc.DrawRoundedRectangle(Theme.Surface2, null, sel, 15, 15);
                Center(dc, Theme.Txt("Preciso", 13, precise ? Theme.Text : (rModeP.Contains(m) ? Theme.Text2 : Theme.Text3), precise), rModeP);
                Center(dc, Theme.Txt("Rápido", 13, !precise ? Theme.Text : (rModeF.Contains(m) ? Theme.Text2 : Theme.Text3), !precise), rModeF);

                // cut button
                bool hc = rCut.Contains(m);
                if (job == null)
                {
                    dc.DrawRoundedRectangle(hc ? Theme.AccentHi : Theme.Accent, null, rCut, 18, 18);
                    Center(dc, Theme.Txt("Cortar", 14, Theme.AccentInk, true), rCut);
                }
                else
                {
                    dc.DrawRoundedRectangle(Theme.Surface2, null, rCut, 18, 18);
                    double pw = rCut.Width * Theme.Clamp(jobProg, 0, 1);
                    dc.PushClip(new RectangleGeometry(rCut, 18, 18));
                    dc.DrawRectangle(Theme.Accent, null, new Rect(rCut.Left, rCut.Top, pw, rCut.Height));
                    dc.Pop();
                    string label = hc ? "Cancelar" : ((int)Math.Floor(jobProg * 100)).ToString() + "%";
                    var dark = Theme.Txt(label, 14, Theme.AccentInk, true);
                    var light = Theme.Txt(label, 14, Theme.Text, true);
                    dc.PushClip(new RectangleGeometry(new Rect(rCut.Left, rCut.Top, pw, rCut.Height)));
                    Center(dc, dark, rCut);
                    dc.Pop();
                    dc.PushClip(new RectangleGeometry(new Rect(rCut.Left + pw, rCut.Top, Math.Max(0, rCut.Width - pw), rCut.Height)));
                    Center(dc, light, rCut);
                    dc.Pop();
                }

                // selection info in the free middle space
                rInLbl = Rect.Empty; rOutLbl = Rect.Empty;
                double rightEnd = pill.Left - 18;
                if (dur > 0 && rightEnd - leftEnd > 60)
                {
                    var fa = Theme.Txt(Theme.Fmt(inT), 13, Theme.Text2, false);
                    var fArrow = Theme.Txt("  →  ", 13, Theme.Text3, false);
                    var fb = Theme.Txt(Theme.Fmt(outT), 13, Theme.Text2, false);
                    var fd = Theme.Txt(Theme.FmtDur(outT - inT), 13, Theme.Text, true);
                    double gap = 14;
                    double full = fa.Width + fArrow.Width + fb.Width + gap + fd.Width + 16;
                    double avail = rightEnd - leftEnd;
                    if (full <= avail)
                    {
                        double sx = leftEnd + (avail - full) / 2;
                        double center = rBar.Left + rBar.Width / 2;
                        if (center - full / 2 >= leftEnd && center + full / 2 <= rightEnd) sx = center - full / 2;
                        double ly = cy - fa.Height / 2;
                        rInLbl = new Rect(sx - 6, cy - 14, fa.Width + 12, 28);
                        rOutLbl = new Rect(sx + fa.Width + fArrow.Width - 6, cy - 14, fb.Width + 12, 28);
                        if (rInLbl.Contains(m)) dc.DrawRoundedRectangle(Theme.Surface, null, rInLbl, 8, 8);
                        if (rOutLbl.Contains(m)) dc.DrawRoundedRectangle(Theme.Surface, null, rOutLbl, 8, 8);
                        dc.DrawText(fa, new Point(sx, ly));
                        dc.DrawText(fArrow, new Point(sx + fa.Width, ly));
                        dc.DrawText(fb, new Point(sx + fa.Width + fArrow.Width, ly));
                        var dr = new Rect(sx + fa.Width + fArrow.Width + fb.Width + gap, cy - 12, fd.Width + 16, 24);
                        dc.DrawRoundedRectangle(Theme.Surface, null, dr, 12, 12);
                        dc.DrawText(fd, new Point(dr.Left + 8, cy - fd.Height / 2));
                    }
                    else if (fd.Width + 16 <= avail)
                    {
                        var dr = new Rect(leftEnd + (avail - fd.Width - 16) / 2, cy - 12, fd.Width + 16, 24);
                        dc.DrawRoundedRectangle(Theme.Surface, null, dr, 12, 12);
                        dc.DrawText(fd, new Point(dr.Left + 8, cy - fd.Height / 2));
                    }
                }
            }
        }

        static void Center(DrawingContext dc, FormattedText ft, Rect r)
        {
            dc.DrawText(ft, new Point(r.Left + (r.Width - ft.Width) / 2, r.Top + (r.Height - ft.Height) / 2));
        }

        static Rect Pill(DrawingContext dc, FormattedText ft, double cx, double y, double minX, double maxX)
        {
            double w = ft.Width + 18, h = 24;
            double x = Theme.Clamp(cx - w / 2, minX, Math.Max(minX, maxX - w));
            var r = new Rect(x, y, w, h);
            dc.DrawRoundedRectangle(Theme.Card, Theme.CardPen, r, 12, 12);
            dc.DrawText(ft, new Point(x + 9, y + (h - ft.Height) / 2));
            return r;
        }

        void DrawOver()
        {
            using (var dc = vOver.RenderOpen())
            {
                double W = ActualWidth, H = ActualHeight;
                if (W <= 0) return;
                Point m = mouse;

                // time pill over the track
                if (dur > 0 && !showHelp && (drag != Drag.None || (mouseIn && TrackBand.Contains(m) && HitTrack(m) != Drag.Mini)))
                {
                    double t, x;
                    if (drag == Drag.In) { t = inT; x = TToX(inT); }
                    else if (drag == Drag.Out) { t = outT; x = TToX(outT); }
                    else if (drag == Drag.Scrub) { t = pos; x = TToX(pos); }
                    else if (drag == Drag.Mini) { t = -1; x = 0; }
                    else
                    {
                        var h = HitTrack(m);
                        if (h == Drag.In) { t = inT; x = TToX(inT); }
                        else if (h == Drag.Out) { t = outT; x = TToX(outT); }
                        else
                        {
                            t = Theme.Clamp(XToT(m.X), 0, dur);
                            if (fps > 0) t = FrameAt(t) / fps;
                            x = Theme.Clamp(m.X, rTrack.Left, rTrack.Right);
                            dc.DrawRectangle(Theme.HoverLine, null, new Rect(x - 0.5, rTrack.Top, 1, rTrack.Height));
                        }
                    }
                    if (t >= 0)
                    {
                        string s = Theme.Fmt(t);
                        if (fps > 0 && (drag == Drag.Out || (drag == Drag.None && HitTrack(m) == Drag.Out))) s = "fim " + s;
                        else if (fps > 0 && (drag == Drag.In || (drag == Drag.None && HitTrack(m) == Drag.In))) s = "início " + s;
                        var ft = Theme.Txt(s, 12, Theme.Text, true);
                        double y = rTrack.Top - (Zoomed ? 46 : 36);
                        Pill(dc, ft, x, y, 8, W - 8);
                    }
                }

                // mode hints
                if (!showHelp && drag == Drag.None && (rModeP.Contains(m) || rModeF.Contains(m)))
                {
                    bool p = rModeP.Contains(m);
                    string s = p ? (audioOnly ? "corte exato · recodifica o áudio" : "corte exato no quadro · recodifica o vídeo")
                                 : (audioOnly ? "instantâneo · sem recodificar" : "instantâneo e sem perda · começa no keyframe anterior");
                    var ft = Theme.Txt(s, 12, Theme.Text2, false);
                    var pill = Rect.Union(rModeP, rModeF);
                    Pill(dc, ft, pill.Left + pill.Width / 2, rTrack.Top - (Zoomed ? 46 : 36), 8, W - 8);
                }

                // toast
                rToast = Rect.Empty;
                if (toastText != null)
                {
                    var ft = Theme.Txt(toastText, 13, toastErr ? Theme.Danger : Theme.Text, true);
                    ft.MaxTextWidth = Math.Max(60, W - 140); ft.MaxLineCount = 1; ft.Trimming = TextTrimming.CharacterEllipsis;
                    FormattedText fs = null, fl = null;
                    if (toastSub != null) { fs = Theme.Txt(toastSub, 12, Theme.Text3, false); fs.MaxTextWidth = Math.Max(60, W - 140); fs.MaxLineCount = 1; fs.Trimming = TextTrimming.CharacterEllipsis; }
                    if (toastPath != null) fl = Theme.Txt("mostrar na pasta", 12, Theme.Accent, true);
                    bool check = !toastErr && toastPath != null;
                    double w = Math.Max(ft.Width, fs != null ? fs.Width : 0);
                    if (fl != null) w = Math.Max(w, ft.Width + 16 + fl.Width);
                    w += 32 + (check ? 26 : 0);
                    w = Math.Min(w, W - 24);
                    double h = fs != null ? 52 : 36;
                    var r = new Rect((W - w) / 2, 12, w, h);
                    dc.DrawRoundedRectangle(Theme.Card, Theme.CardPen, r, 12, 12);
                    double tx = r.Left + 16;
                    if (check)
                    {
                        var g = new StreamGeometry();
                        double cx = tx + 6, cyy = r.Top + h / 2;
                        using (var c = g.Open())
                        {
                            c.BeginFigure(new Point(cx - 5, cyy), false, false);
                            c.LineTo(new Point(cx - 1.5, cyy + 3.5), true, true);
                            c.LineTo(new Point(cx + 5.5, cyy - 4), true, true);
                        }
                        g.Freeze();
                        dc.DrawGeometry(null, Theme.GoodPen, g);
                        tx += 26;
                    }
                    double ty = fs != null ? r.Top + 9 : r.Top + (h - ft.Height) / 2;
                    dc.DrawText(ft, new Point(tx, ty));
                    if (fl != null) dc.DrawText(fl, new Point(r.Right - 16 - fl.Width, ty + (ft.Height - fl.Height) / 2));
                    if (fs != null) dc.DrawText(fs, new Point(tx, ty + ft.Height + 1));
                    rToast = r;
                }

                if (showHelp) DrawHelp(dc, W, H);
            }
        }

        void DrawHelp(DrawingContext dc, double W, double H)
        {
            string[,] rows = {
                { "Espaço", "play / pausa" },
                { "P", "tocar só o trecho" },
                { "I   O", "marcar início / fim aqui" },
                { "←  →", "1 quadro  (Shift 1s · Ctrl 5s)" },
                { "Home  End", "ir ao início / fim do trecho" },
                { "roda", "zoom na timeline · quadro a quadro no vídeo" },
                { "+  −  0", "zoom · tirar zoom" },
                { "Tab", "Preciso ↔ Rápido" },
                { "↑  ↓   M", "volume · mudo" },
                { "Enter", "cortar" },
                { "Esc", "cancelar / fechar" },
            };
            int n = rows.GetLength(0);
            double rowH = 24, w = 400, h = n * rowH + 56;
            var r = new Rect((W - w) / 2, Math.Max(12, (H - h) / 2 - 30), w, h);
            dc.DrawRoundedRectangle(Theme.Card, Theme.CardPen, r, 14, 14);
            var title = Theme.Txt("Atalhos", 14, Theme.Text, true);
            dc.DrawText(title, new Point(r.Left + 22, r.Top + 16));
            for (int i = 0; i < n; i++)
            {
                double y = r.Top + 46 + i * rowH;
                dc.DrawText(Theme.Txt(rows[i, 0], 13, Theme.Accent, true), new Point(r.Left + 22, y));
                dc.DrawText(Theme.Txt(rows[i, 1], 13, Theme.Text2, false), new Point(r.Left + 130, y));
            }
        }
    }
}
