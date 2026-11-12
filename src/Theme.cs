using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace TrimApp
{
    static class Theme
    {
        public static readonly Color BgColor = Color.FromRgb(0x11, 0x11, 0x13);
        public static readonly Brush Bg = B(0x11, 0x11, 0x13);
        public static readonly Brush Surface = B(0x1B, 0x1B, 0x1F);
        public static readonly Brush Surface2 = B(0x2A, 0x2A, 0x30);
        public static readonly Brush Surface3 = B(0x38, 0x38, 0x40);
        public static readonly Brush Text = B(0xF4, 0xF4, 0xF6);
        public static readonly Brush Text2 = B(0xA1, 0xA1, 0xAA);
        public static readonly Brush Text3 = B(0x66, 0x66, 0x70);
        public static readonly Brush Accent = B(0xFF, 0xC5, 0x3D);
        public static readonly Brush AccentHi = B(0xFF, 0xD4, 0x6E);
        public static readonly Brush AccentInk = B(0x26, 0x1B, 0x00);
        public static readonly Brush AccentSoft = A(0x70, 0xFF, 0xC5, 0x3D);
        public static readonly Brush Dim = A(0xB0, 0x0C, 0x0C, 0x0E);
        public static readonly Brush Shadow = A(0x99, 0x00, 0x00, 0x00);
        public static readonly Brush Danger = B(0xFF, 0x80, 0x80);
        public static readonly Brush Good = B(0x5E, 0xE0, 0x8F);
        public static readonly Brush WaveAud = B(0xCF, 0xCF, 0xD8);
        public static readonly Brush WaveVid = A(0xE0, 0xFF, 0xFF, 0xFF);
        public static readonly Brush WaveBand = A(0xA0, 0x00, 0x00, 0x00);
        public static readonly Brush HoverLine = A(0x70, 0xFF, 0xFF, 0xFF);
        public static readonly Brush Card = A(0xF5, 0x1E, 0x1E, 0x23);
        public static readonly Pen CardPen = MakePen(A(0xFF, 0x30, 0x30, 0x37), 1, false);
        public static readonly Pen InkPen = MakePen(B(0x26, 0x1B, 0x00), 2, true);
        public static readonly Pen TextPen = MakePen(B(0xF4, 0xF4, 0xF6), 2.2, true);
        public static readonly Pen GoodPen = MakePen(B(0x5E, 0xE0, 0x8F), 2, true);
        public static readonly Pen MiniPen = MakePen(B(0xA1, 0xA1, 0xAA), 1, false);
        public static readonly FontFamily Font = new FontFamily("Segoe UI");
        public static readonly Typeface Reg = new Typeface(Font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        public static readonly Typeface Semi = new Typeface(Font, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        public static double Dpi = 1;

        static Brush B(byte r, byte g, byte b)
        {
            var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x;
        }
        static Brush A(byte a, byte r, byte g, byte b)
        {
            var x = new SolidColorBrush(Color.FromArgb(a, r, g, b)); x.Freeze(); return x;
        }
        static Pen MakePen(Brush b, double w, bool round)
        {
            var p = new Pen(b, w);
            if (round) { p.StartLineCap = PenLineCap.Round; p.EndLineCap = PenLineCap.Round; p.LineJoin = PenLineJoin.Round; }
            p.Freeze(); return p;
        }

        public static FormattedText Txt(string s, double size, Brush b, bool semi)
        {
            return new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, semi ? Semi : Reg,
                size, b, null, TextFormattingMode.Display, Dpi);
        }

        public static string Fmt(double t)
        {
            if (double.IsNaN(t) || t < 0) t = 0;
            long ms = (long)Math.Round(t * 1000);
            long h = ms / 3600000, m = (ms / 60000) % 60, s = (ms / 1000) % 60, f = ms % 1000;
            if (h > 0) return string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}.{3:000}", h, m, s, f);
            return string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}.{2:000}", m, s, f);
        }

        public static string FmtDur(double d)
        {
            if (d < 60) return d.ToString("0.000", CultureInfo.InvariantCulture) + "s";
            return Fmt(d);
        }

        public static bool TryParseTime(string s, out double t)
        {
            t = 0;
            s = (s ?? "").Trim().Replace(',', '.');
            if (s.Length == 0) return false;
            string[] parts = s.Split(':');
            if (parts.Length > 3) return false;
            double acc = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                double v;
                if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v) || v < 0) return false;
                acc = acc * 60 + v;
            }
            t = acc;
            return true;
        }

        public static double Clamp(double v, double lo, double hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }
    }
}
