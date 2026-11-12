using System;
using System.IO;
using System.Text;

namespace TrimApp
{
    // Windows Media Foundation ignores MP4/MOV edit lists (elst) while ffmpeg honors them. Files with B-frames
    // usually start with an edit that skips the decoder delay, so the Windows player runs a few frames off
    // from ffmpeg. This reads the first video track's edit list and returns the offset (in seconds) to add to an
    // ffmpeg timestamp to get the matching Media Foundation timestamp.
    static class Mp4
    {
        public static bool IsIsoBmff(string path)
        {
            string e = Path.GetExtension(path).ToLowerInvariant();
            return e == ".mp4" || e == ".m4v" || e == ".mov" || e == ".3gp" || e == ".3g2";
        }

        public static double VideoEditOffset(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096))
                {
                    long len = fs.Length;
                    foreach (var b in Children(fs, 0, len))
                        if (b.Type == "moov") return Moov(fs, b);
                }
            }
            catch { }
            return 0;
        }

        struct Box { public string Type; public long Body, End; }

        static System.Collections.Generic.IEnumerable<Box> Children(FileStream fs, long start, long end)
        {
            long pos = start;
            while (pos + 8 <= end)
            {
                fs.Position = pos;
                long size = U32(fs);
                string type = Fourcc(fs);
                long hdr = 8;
                if (size == 1) { size = (long)U64(fs); hdr = 16; }
                else if (size == 0) size = end - pos;
                if (size < hdr || pos + size > end + 8) yield break;
                yield return new Box { Type = type, Body = pos + hdr, End = Math.Min(end, pos + size) };
                pos += size;
            }
        }

        static double Moov(FileStream fs, Box moov)
        {
            long movieTs = 0;
            foreach (var b in Children(fs, moov.Body, moov.End))
            {
                if (b.Type == "mvhd") movieTs = Timescale(fs, b);
                else if (b.Type == "trak")
                {
                    bool video = false; long mediaTs = 0; long mediaTime = 0; long emptyDur = 0; bool hasEdit = false;
                    foreach (var t in Children(fs, b.Body, b.End))
                    {
                        if (t.Type == "edts")
                        {
                            foreach (var e in Children(fs, t.Body, t.End))
                                if (e.Type == "elst") { hasEdit = Elst(fs, e, out mediaTime, out emptyDur); }
                        }
                        else if (t.Type == "mdia")
                        {
                            foreach (var m in Children(fs, t.Body, t.End))
                            {
                                if (m.Type == "mdhd") mediaTs = Timescale(fs, m);
                                else if (m.Type == "hdlr") { fs.Position = m.Body + 8; video = Fourcc(fs) == "vide"; }
                            }
                        }
                    }
                    if (!video) continue;
                    if (!hasEdit || mediaTs <= 0) return 0;
                    if (movieTs <= 0) { foreach (var x in Children(fs, moov.Body, moov.End)) if (x.Type == "mvhd") movieTs = Timescale(fs, x); }
                    double off = (double)mediaTime / mediaTs - (movieTs > 0 ? (double)emptyDur / movieTs : 0);
                    return Math.Abs(off) < 5 ? off : 0;
                }
            }
            return 0;
        }

        // mvhd / mdhd: version(1) flags(3) then times, timescale
        static long Timescale(FileStream fs, Box b)
        {
            fs.Position = b.Body;
            int ver = fs.ReadByte();
            fs.Position = b.Body + 4 + (ver == 1 ? 16 : 8);
            return U32(fs);
        }

        static bool Elst(FileStream fs, Box b, out long mediaTime, out long emptyDur)
        {
            mediaTime = 0; emptyDur = 0;
            fs.Position = b.Body;
            int ver = fs.ReadByte();
            fs.Position = b.Body + 4;
            long n = U32(fs);
            for (long i = 0; i < n && i < 64; i++)
            {
                long segDur, mt;
                if (ver == 1) { segDur = (long)U64(fs); mt = (long)U64(fs); }
                else { segDur = U32(fs); mt = (int)(uint)U32(fs); }
                U32(fs); // media rate
                if (mt == -1) { emptyDur += segDur; continue; }
                mediaTime = mt;
                return true;
            }
            return emptyDur > 0;
        }

        static long U32(Stream s)
        {
            int a = s.ReadByte(), b = s.ReadByte(), c = s.ReadByte(), d = s.ReadByte();
            if (d < 0) throw new EndOfStreamException();
            return (long)(((uint)a << 24) | ((uint)b << 16) | ((uint)c << 8) | (uint)d);
        }

        static ulong U64(Stream s) { return ((ulong)U32(s) << 32) | (ulong)U32(s); }

        static string Fourcc(Stream s)
        {
            var b = new byte[4];
            if (s.Read(b, 0, 4) < 4) throw new EndOfStreamException();
            return Encoding.ASCII.GetString(b);
        }
    }
}
