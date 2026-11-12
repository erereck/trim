// Draws the Trim icon (dark rounded square + amber trim frame + white playhead) and writes a multi-size .ico
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

static class MakeIcon
{
    static GraphicsPath Round(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    static Bitmap Draw(int s)
    {
        var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            float S = s;
            float m = s <= 20 ? 0 : S * 0.04f;
            using (var bg = Round(new RectangleF(m, m, S - 2 * m, S - 2 * m), S * 0.22f))
            using (var br = new LinearGradientBrush(new PointF(0, 0), new PointF(0, S), Color.FromArgb(255, 0x24, 0x24, 0x29), Color.FromArgb(255, 0x13, 0x13, 0x16)))
                g.FillPath(br, bg);

            var amber = Color.FromArgb(255, 0xFF, 0xC5, 0x3D);
            float fx0 = S * 0.15f, fx1 = S * 0.85f, fy0 = S * 0.29f, fy1 = S * 0.71f;
            float hw = S * 0.12f, bt = Math.Max(1.2f, S * 0.055f);
            using (var frame = new GraphicsPath())
            {
                frame.FillMode = FillMode.Alternate;
                frame.AddPath(Round(new RectangleF(fx0, fy0, fx1 - fx0, fy1 - fy0), S * 0.07f), false);
                frame.AddRectangle(new RectangleF(fx0 + hw, fy0 + bt, (fx1 - fx0) - 2 * hw, (fy1 - fy0) - 2 * bt));
                using (var b = new SolidBrush(amber)) g.FillPath(b, frame);
            }
            if (s >= 32)
            {
                using (var pen = new Pen(Color.FromArgb(255, 0x26, 0x1B, 0x00), Math.Max(1.2f, S * 0.028f)))
                {
                    pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round; pen.LineJoin = LineJoin.Round;
                    float cy = S * 0.5f, ch = S * 0.075f, cw = S * 0.025f;
                    float lx = fx0 + hw / 2, rx = fx1 - hw / 2;
                    g.DrawLines(pen, new[] { new PointF(lx + cw, cy - ch), new PointF(lx - cw, cy), new PointF(lx + cw, cy + ch) });
                    g.DrawLines(pen, new[] { new PointF(rx - cw, cy - ch), new PointF(rx + cw, cy), new PointF(rx - cw, cy + ch) });
                }
            }
            float px = S * 0.585f, pw = Math.Max(1.4f, S * 0.05f);
            using (var ph = Round(new RectangleF(px - pw / 2, S * 0.2f, pw, S * 0.6f), pw / 2))
            using (var b = new SolidBrush(Color.White)) g.FillPath(b, ph);
        }
        return bmp;
    }

    static byte[] Dib(Bitmap b)
    {
        int s = b.Width;
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms))
        {
            w.Write(40); w.Write(s); w.Write(s * 2); w.Write((short)1); w.Write((short)32);
            w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
            for (int y = s - 1; y >= 0; y--)
                for (int x = 0; x < s; x++)
                {
                    var c = b.GetPixel(x, y);
                    w.Write(c.B); w.Write(c.G); w.Write(c.R); w.Write(c.A);
                }
            int stride = ((s + 31) / 32) * 4;
            w.Write(new byte[stride * s]);
            return ms.ToArray();
        }
    }

    static int Main(string[] args)
    {
        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 256 };
        var imgs = new List<byte[]>();
        foreach (int s in sizes)
        {
            using (var b = Draw(s))
            {
                if (s == 256)
                {
                    using (var ms = new MemoryStream()) { b.Save(ms, ImageFormat.Png); imgs.Add(ms.ToArray()); }
                    if (args.Length > 1) b.Save(args[1], ImageFormat.Png);
                }
                else imgs.Add(Dib(b));
            }
        }
        using (var f = new BinaryWriter(File.Create(args[0])))
        {
            f.Write((short)0); f.Write((short)1); f.Write((short)sizes.Length);
            int off = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                int s = sizes[i];
                f.Write((byte)(s >= 256 ? 0 : s)); f.Write((byte)(s >= 256 ? 0 : s));
                f.Write((byte)0); f.Write((byte)0); f.Write((short)1); f.Write((short)32);
                f.Write(imgs[i].Length); f.Write(off);
                off += imgs[i].Length;
            }
            foreach (var d in imgs) f.Write(d);
        }
        return 0;
    }
}
