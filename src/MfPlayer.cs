using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace TrimApp
{
    // Thin wrapper over the Media Foundation Media Engine in frame-server mode.
    // With a D3D11 device the GPU decodes and scales each frame to the exact on-screen size; we read back
    // that small frame into a WriteableBitmap. Without D3D11 it falls back to MF's software path (WIC).
    // Memory stays small regardless of the video resolution.
    public sealed class MfPlayer
    {
        public event EventHandler MediaOpened, MediaFailed, MediaEnded, FrameWanted;
        public bool HasVideo, HasAudio, Hardware;
        public int NaturalVideoWidth, NaturalVideoHeight;
        public double Duration;
        public string Error;
        public double FramePts = -1;   // timestamp (s) of the last frame copied to the bitmap

        IMFMediaEngine eng;
        readonly Notify notify;
        readonly Dispatcher ui;
        WriteableBitmap wb;
        int fw, fh;
        double volume = 1;
        bool muted, closed;

        // software path
        IWICImagingFactory wic;
        IWICBitmap wicBmp;
        IntPtr wicPtr;
        // hardware path
        IntPtr dev, ctx, mgr, texRT, texStage;

        static readonly Guid CLSID_Factory = new Guid("B44392DA-499B-446b-A4CB-005FEAD0E6D5");
        static readonly Guid CLSID_WIC = new Guid("cacaf262-9370-4615-a13b-9f5539da4c0a");
        static readonly Guid MF_MEDIA_ENGINE_CALLBACK = new Guid("c60381b8-83a4-41f8-a3d0-de05076849a9");
        static readonly Guid MF_MEDIA_ENGINE_VIDEO_OUTPUT_FORMAT = new Guid("5066893c-8cf9-42bc-8b8a-472212e52726");
        static readonly Guid MF_MEDIA_ENGINE_DXGI_MANAGER = new Guid("065702da-1094-486d-8617-ee7cc4ee4648");
        static readonly Guid IID_ID3D10Multithread = new Guid("9B7E4E00-342C-4106-A19F-4F2704F689F0");
        static readonly Guid WIC_32bppBGRA = new Guid("6fddc324-4e03-4bfe-b185-3d77768dc90f");

        [DllImport("mfplat.dll")] static extern int MFStartup(int version, int flags);
        [DllImport("mfplat.dll")] static extern int MFShutdown();
        [DllImport("mfplat.dll")] static extern int MFCreateAttributes(out IMFAttributes attr, int size);
        [DllImport("mfplat.dll")] static extern int MFCreateDXGIDeviceManager(out uint token, out IntPtr manager);
        [DllImport("d3d11.dll")]
        static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr levels, uint numLevels,
                                            uint sdkVersion, out IntPtr device, out int level, out IntPtr context);
        [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")] static extern void CopyMemory(IntPtr dst, IntPtr src, IntPtr len);

        [StructLayout(LayoutKind.Sequential)]
        struct TexDesc
        {
            public uint Width, Height, MipLevels, ArraySize;
            public int Format;
            public uint SampleCount, SampleQuality;
            public int Usage;
            public uint BindFlags, CPUAccessFlags, MiscFlags;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct Mapped { public IntPtr Data; public uint RowPitch, DepthPitch; }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnSetMt(IntPtr self, int on);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnResetDevice(IntPtr self, IntPtr device, uint token);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnCreateTex(IntPtr self, ref TexDesc d, IntPtr init, out IntPtr tex);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void FnCopyResource(IntPtr self, IntPtr dst, IntPtr src);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnMap(IntPtr self, IntPtr res, uint sub, int type, uint flags, out Mapped m);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void FnUnmap(IntPtr self, IntPtr res, uint sub);

        FnCreateTex createTex;
        FnCopyResource copyResource;
        FnMap map;
        FnUnmap unmap;

        static T Fn<T>(IntPtr obj, int slot) where T : class
        {
            IntPtr vt = Marshal.ReadIntPtr(obj);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(vt, slot * IntPtr.Size), typeof(T));
        }

        static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }
        static void Rel(ref IntPtr p) { if (p != IntPtr.Zero) { Marshal.Release(p); p = IntPtr.Zero; } }

        public MfPlayer(Dispatcher ui, bool allowHardware)
        {
            this.ui = ui;
            notify = new Notify(OnEvent);
            Check(MFStartup(0x20070, 0));
            IMFAttributes a;
            Check(MFCreateAttributes(out a, 3));
            Guid cb = MF_MEDIA_ENGINE_CALLBACK, fmt = MF_MEDIA_ENGINE_VIDEO_OUTPUT_FORMAT;
            Check(a.SetUnknown(ref cb, notify));
            Check(a.SetUINT32(ref fmt, 87)); // DXGI_FORMAT_B8G8R8A8_UNORM
            if (allowHardware && InitD3D())
            {
                Guid g = MF_MEDIA_ENGINE_DXGI_MANAGER;
                object m = Marshal.GetObjectForIUnknown(mgr);
                if (a.SetUnknown(ref g, m) >= 0) Hardware = true;
                Marshal.ReleaseComObject(m);
            }
            var f = (IMFMediaEngineClassFactory)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_Factory));
            Check(f.CreateInstance(0, a, out eng));
            Marshal.ReleaseComObject(f);
            Marshal.ReleaseComObject(a);
        }

        bool InitD3D()
        {
            try
            {
                int level;
                // BGRA_SUPPORT | VIDEO_SUPPORT
                if (D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, 0x20 | 0x800, IntPtr.Zero, 0, 7, out dev, out level, out ctx) < 0) return false;
                IntPtr mt;
                Guid iid = IID_ID3D10Multithread;
                if (Marshal.QueryInterface(dev, ref iid, out mt) >= 0)
                {
                    Fn<FnSetMt>(mt, 5)(mt, 1);  // SetMultithreadProtected(TRUE)
                    Marshal.Release(mt);
                }
                uint token;
                if (MFCreateDXGIDeviceManager(out token, out mgr) < 0) { FreeD3D(); return false; }
                if (Fn<FnResetDevice>(mgr, 7)(mgr, dev, token) < 0) { FreeD3D(); return false; }
                createTex = Fn<FnCreateTex>(dev, 5);
                copyResource = Fn<FnCopyResource>(ctx, 47);
                map = Fn<FnMap>(ctx, 14);
                unmap = Fn<FnUnmap>(ctx, 15);
                return true;
            }
            catch { FreeD3D(); return false; }
        }

        void FreeD3D()
        {
            Rel(ref texRT); Rel(ref texStage); Rel(ref mgr); Rel(ref ctx); Rel(ref dev);
        }

        public void Open(string path)
        {
            HasVideo = HasAudio = false; Duration = 0; Error = null; FramePts = -1;
            NaturalVideoWidth = NaturalVideoHeight = 0;
            Check(eng.SetSource(new Uri(path).AbsoluteUri));
        }

        void OnEvent(int e, IntPtr p1, int p2)
        {
            if (closed) return;
            ui.BeginInvoke(new Action(() => Handle(e, p1, p2)));
        }

        void Handle(int e, IntPtr p1, int p2)
        {
            if (closed) return;
            switch (e)
            {
                case 10: // LOADEDMETADATA
                    HasVideo = eng.HasVideo() != 0;
                    HasAudio = eng.HasAudio() != 0;
                    ReadDuration();
                    int w, h;
                    if (HasVideo && eng.GetNativeVideoSize(out w, out h) >= 0) { NaturalVideoWidth = w; NaturalVideoHeight = h; }
                    eng.SetVolume(volume);
                    eng.SetMuted(muted ? 1 : 0);
                    Raise(MediaOpened);
                    break;
                case 21: ReadDuration(); break; // DURATIONCHANGE
                case 5: // ERROR
                    Error = "MF " + p1.ToInt64() + " / 0x" + p2.ToString("X8");
                    Raise(MediaFailed);
                    break;
                case 19: Raise(MediaEnded); break;
                case 11: case 17: case 1009: Raise(FrameWanted); break; // LOADEDDATA, SEEKED, FIRSTFRAMEREADY
            }
        }

        void ReadDuration()
        {
            double d = eng.GetDuration();
            Duration = (double.IsNaN(d) || double.IsInfinity(d) || d < 0) ? 0 : d;
        }

        void Raise(EventHandler h) { if (h != null) h(this, EventArgs.Empty); }

        public double Time
        {
            get { if (closed) return 0; double t = eng.GetCurrentTime(); return double.IsNaN(t) ? 0 : t; }
            set { if (!closed) eng.SetCurrentTime(value); }
        }

        public void Play() { if (!closed) eng.Play(); }
        public void Pause() { if (!closed) eng.Pause(); }

        public double Volume
        {
            get { return volume; }
            set { volume = value; if (!closed) eng.SetVolume(value); }
        }

        public bool IsMuted
        {
            get { return muted; }
            set { muted = value; if (!closed) eng.SetMuted(value ? 1 : 0); }
        }

        public ImageSource Frame { get { return wb; } }

        // (re)allocate the output surfaces at the on-screen pixel size; returns true when new ones were made
        public bool EnsureTarget(int w, int h)
        {
            if (w < 2 || h < 2 || closed) return false;
            if (wb != null && fw == w && fh == h) return false;
            wb = null;
            if (Hardware)
            {
                Rel(ref texRT); Rel(ref texStage);
                var d = new TexDesc { Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = 87, SampleCount = 1, Usage = 0, BindFlags = 0x20 };
                if (createTex(dev, ref d, IntPtr.Zero, out texRT) < 0) return false;
                d.Usage = 3; d.BindFlags = 0; d.CPUAccessFlags = 0x20000;  // STAGING, CPU read
                if (createTex(dev, ref d, IntPtr.Zero, out texStage) < 0) { Rel(ref texRT); return false; }
            }
            else
            {
                if (wic == null) wic = (IWICImagingFactory)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_WIC));
                if (wicBmp != null) { Rel(ref wicPtr); Marshal.ReleaseComObject(wicBmp); wicBmp = null; }
                Guid pf = WIC_32bppBGRA;
                IWICBitmap b;
                if (wic.CreateBitmap(w, h, ref pf, 2, out b) < 0) return false;
                wicBmp = b;
                wicPtr = Marshal.GetIUnknownForObject(b);
            }
            fw = w; fh = h;
            wb = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr32, null);
            return true;
        }

        // copy the current video frame into the bitmap; without force only when the engine has a new one
        public bool Pump(bool force)
        {
            if (wb == null || closed || !HasVideo) return false;
            long pts;
            int tick = eng.OnVideoStreamTick(out pts);
            if (!force && tick != 0) return false;
            var r = new RECT { L = 0, T = 0, R = fw, B = fh };
            uint border = 0xFF000000;
            if (eng.TransferVideoFrame(Hardware ? texRT : wicPtr, IntPtr.Zero, ref r, ref border) < 0) return false;
            bool ok;
            wb.Lock();
            try
            {
                if (Hardware)
                {
                    copyResource(ctx, texStage, texRT);
                    Mapped m;
                    ok = map(ctx, texStage, 0, 1, 0, out m) >= 0;  // D3D11_MAP_READ
                    if (ok)
                    {
                        int row = fw * 4, stride = wb.BackBufferStride;
                        if (m.RowPitch == stride) CopyMemory(wb.BackBuffer, m.Data, (IntPtr)((long)stride * fh));
                        else
                            for (int y = 0; y < fh; y++)
                                CopyMemory(wb.BackBuffer + y * stride, m.Data + (int)(y * m.RowPitch), (IntPtr)row);
                        unmap(ctx, texStage, 0);
                    }
                }
                else ok = wicBmp.CopyPixels(IntPtr.Zero, wb.BackBufferStride, wb.BackBufferStride * fh, wb.BackBuffer) >= 0;
                if (ok) wb.AddDirtyRect(new Int32Rect(0, 0, fw, fh));
            }
            finally { wb.Unlock(); }
            if (ok && tick == 0) FramePts = pts / 1e7;
            return ok;
        }

        public void Close()
        {
            if (closed) return;
            closed = true;
            try { eng.Shutdown(); } catch { }
            try { Marshal.ReleaseComObject(eng); } catch { }
            Rel(ref wicPtr);
            if (wicBmp != null) { try { Marshal.ReleaseComObject(wicBmp); } catch { } }
            if (wic != null) { try { Marshal.ReleaseComObject(wic); } catch { } }
            FreeD3D();
            try { MFShutdown(); } catch { }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int L, T, R, B; }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class Notify : IMFMediaEngineNotify
    {
        readonly Action<int, IntPtr, int> cb;
        public Notify(Action<int, IntPtr, int> cb) { this.cb = cb; }
        public int EventNotify(int evt, IntPtr param1, int param2)
        {
            try { cb(evt, param1, param2); } catch { }
            return 0;
        }
    }

    [ComImport, Guid("fee7c112-e776-42b5-9bbf-0048524e2bd5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFMediaEngineNotify
    {
        [PreserveSig] int EventNotify(int evt, IntPtr param1, int param2);
    }

    [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFAttributes
    {
        void _GetItem(); void _GetItemType(); void _CompareItem(); void _Compare();
        void _GetUINT32(); void _GetUINT64(); void _GetDouble(); void _GetGUID();
        void _GetStringLength(); void _GetString(); void _GetAllocatedString();
        void _GetBlobSize(); void _GetBlob(); void _GetAllocatedBlob(); void _GetUnknown();
        void _SetItem(); void _DeleteItem(); void _DeleteAllItems();
        [PreserveSig] int SetUINT32([In] ref Guid key, int value);
        void _SetUINT64(); void _SetDouble(); void _SetGUID(); void _SetString(); void _SetBlob();
        [PreserveSig] int SetUnknown([In] ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value);
    }

    [ComImport, Guid("4D645ACE-26AA-4688-9BE1-DF3516990B93"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFMediaEngineClassFactory
    {
        [PreserveSig] int CreateInstance(int flags, IMFAttributes attr, out IMFMediaEngine engine);
    }

    [ComImport, Guid("98a1b0bb-03eb-4935-ae7c-93c1fa0e1c93"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFMediaEngine
    {
        [PreserveSig] int GetError(out IntPtr err);
        [PreserveSig] int SetErrorCode(int code);
        [PreserveSig] int SetSourceElements(IntPtr elements);
        [PreserveSig] int SetSource([MarshalAs(UnmanagedType.BStr)] string url);
        [PreserveSig] int GetCurrentSource(out IntPtr url);
        [PreserveSig] ushort GetNetworkState();
        [PreserveSig] int GetPreload();
        [PreserveSig] int SetPreload(int p);
        [PreserveSig] int GetBuffered(out IntPtr ranges);
        [PreserveSig] int Load();
        [PreserveSig] int CanPlayType([MarshalAs(UnmanagedType.BStr)] string type, out int answer);
        [PreserveSig] ushort GetReadyState();
        [PreserveSig] int IsSeeking();
        [PreserveSig] double GetCurrentTime();
        [PreserveSig] int SetCurrentTime(double t);
        [PreserveSig] double GetStartTime();
        [PreserveSig] double GetDuration();
        [PreserveSig] int IsPaused();
        [PreserveSig] double GetDefaultPlaybackRate();
        [PreserveSig] int SetDefaultPlaybackRate(double r);
        [PreserveSig] double GetPlaybackRate();
        [PreserveSig] int SetPlaybackRate(double r);
        [PreserveSig] int GetPlayed(out IntPtr r);
        [PreserveSig] int GetSeekable(out IntPtr r);
        [PreserveSig] int IsEnded();
        [PreserveSig] int GetAutoPlay();
        [PreserveSig] int SetAutoPlay(int b);
        [PreserveSig] int GetLoop();
        [PreserveSig] int SetLoop(int b);
        [PreserveSig] int Play();
        [PreserveSig] int Pause();
        [PreserveSig] int GetMuted();
        [PreserveSig] int SetMuted(int b);
        [PreserveSig] double GetVolume();
        [PreserveSig] int SetVolume(double v);
        [PreserveSig] int HasVideo();
        [PreserveSig] int HasAudio();
        [PreserveSig] int GetNativeVideoSize(out int cx, out int cy);
        [PreserveSig] int GetVideoAspectRatio(out int cx, out int cy);
        [PreserveSig] int Shutdown();
        [PreserveSig] int TransferVideoFrame(IntPtr dst, IntPtr srcRect, ref RECT dstRect, ref uint border);
        [PreserveSig] int OnVideoStreamTick(out long pts);
    }

    [ComImport, Guid("ec5ec8a9-c395-4314-9c77-54d7a935ff70"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IWICImagingFactory
    {
        void _CreateDecoderFromFilename(); void _CreateDecoderFromStream(); void _CreateDecoderFromFileHandle();
        void _CreateComponentInfo(); void _CreateDecoder(); void _CreateEncoder(); void _CreatePalette();
        void _CreateFormatConverter(); void _CreateBitmapScaler(); void _CreateBitmapClipper();
        void _CreateBitmapFlipRotator(); void _CreateStream(); void _CreateColorContext(); void _CreateColorTransformer();
        [PreserveSig] int CreateBitmap(int w, int h, ref Guid format, int option, out IWICBitmap bitmap);
    }

    [ComImport, Guid("00000121-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IWICBitmap
    {
        void _GetSize(); void _GetPixelFormat(); void _GetResolution(); void _CopyPalette();
        [PreserveSig] int CopyPixels(IntPtr rect, int stride, int bufferSize, IntPtr buffer);
    }
}
