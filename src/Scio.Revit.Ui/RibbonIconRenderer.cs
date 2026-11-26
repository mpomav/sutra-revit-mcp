#nullable enable
using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Scio.Revit.Ui
{
    internal enum RibbonIconKind
    {
        Status,
        Settings,
        Pause,
        Python,
        About
    }

    /// <summary>
    /// Renders crisp, resolution-exact WPF vector icons (16x16 and 32x32) for Revit Ribbon buttons
    /// using the SCIO brand palette (#E06400), eliminating missing-PNG blank buttons and misalignment.
    /// </summary>
    internal static class RibbonIconRenderer
    {
        private static readonly Color ScioOrange = Color.FromRgb(0xE0, 0x64, 0x00);
        private static readonly Color ScioOrangeDark = Color.FromRgb(0xB8, 0x4E, 0x00);
        private static readonly Color SlateBg = Color.FromRgb(0x2B, 0x2E, 0x36);
        private static readonly Color SlateBorder = Color.FromRgb(0x1C, 0x1E, 0x24);
        private static readonly Color AmberBg = Color.FromRgb(0xD9, 0x77, 0x06);
        private static readonly Color AmberBorder = Color.FromRgb(0xA1, 0x55, 0x04);
        private static readonly Color TerminalBg = Color.FromRgb(0x1E, 0x24, 0x30);
        private static readonly Color TerminalBorder = Color.FromRgb(0x11, 0x15, 0x1D);
        private static readonly Color LiveGreen = Color.FromRgb(0x22, 0xC5, 0x5E);
        private static readonly Color SoftAccent = Color.FromRgb(0xFB, 0x92, 0x3C);

        public static BitmapSource Render(RibbonIconKind kind, int size)
        {
            var visual = new DrawingVisual();
            RenderOptions.SetEdgeMode(visual, EdgeMode.Unspecified);

            using (DrawingContext dc = visual.RenderOpen())
            {
                // Draw on a normalized 32x32 coordinate system scaled cleanly to target size.
                double scale = size / 32.0;
                dc.PushTransform(new ScaleTransform(scale, scale));

                switch (kind)
                {
                    case RibbonIconKind.Status:
                        DrawStatus(dc);
                        break;
                    case RibbonIconKind.Settings:
                        DrawSettings(dc);
                        break;
                    case RibbonIconKind.Pause:
                        DrawPause(dc);
                        break;
                    case RibbonIconKind.Python:
                        DrawPython(dc);
                        break;
                    case RibbonIconKind.About:
                        DrawAbout(dc);
                        break;
                }

                dc.Pop();
            }

            var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            bmp.Freeze();
            return bmp;
        }

        private static void DrawSquircle(DrawingContext dc, Color topColor, Color bottomColor, Color borderColor)
        {
            var bg = new LinearGradientBrush(topColor, bottomColor, 90);
            bg.Freeze();
            var borderPen = new Pen(FrozenBrush(borderColor), 1.0);
            borderPen.Freeze();
            dc.DrawRoundedRectangle(bg, borderPen, new Rect(2.0, 2.0, 28.0, 28.0), 7.0, 7.0);
        }

        private static void DrawStatus(DrawingContext dc)
        {
            DrawSquircle(dc, Color.FromRgb(0xEC, 0x72, 0x10), ScioOrangeDark, ScioOrangeDark);

            // Crisp white heartbeat / bridge signal line
            var whitePen = new Pen(Brushes.White, 2.4)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            };
            whitePen.Freeze();

            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(new Point(6.5, 16.5), false, false);
                ctx.LineTo(new Point(11.0, 16.5), true, false);
                ctx.LineTo(new Point(13.8, 10.0), true, false);
                ctx.LineTo(new Point(17.8, 22.0), true, false);
                ctx.LineTo(new Point(20.8, 16.5), true, false);
                ctx.LineTo(new Point(25.5, 16.5), true, false);
            }
            geo.Freeze();
            dc.DrawGeometry(null, whitePen, geo);

            // Live green status badge dot in bottom-right corner
            var ringPen = new Pen(Brushes.White, 1.8);
            ringPen.Freeze();
            dc.DrawEllipse(FrozenBrush(LiveGreen), ringPen, new Point(25.5, 25.5), 4.0, 4.0);
        }

        private static void DrawSettings(DrawingContext dc)
        {
            DrawSquircle(dc, Color.FromRgb(0x38, 0x3C, 0x46), SlateBg, SlateBorder);

            var trackPen = new Pen(FrozenBrush(Color.FromRgb(0xD4, 0xD4, 0xD8)), 2.2)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            trackPen.Freeze();

            var accentBrush = FrozenBrush(ScioOrange);
            var whitePen = new Pen(Brushes.White, 1.4);
            whitePen.Freeze();

            // Three clean horizontal slider rows
            dc.DrawLine(trackPen, new Point(7.5, 10.0), new Point(24.5, 10.0));
            dc.DrawLine(trackPen, new Point(7.5, 16.0), new Point(24.5, 16.0));
            dc.DrawLine(trackPen, new Point(7.5, 22.0), new Point(24.5, 22.0));

            // Slider knobs (top & bottom white, middle SCIO Orange)
            dc.DrawEllipse(Brushes.White, null, new Point(12.0, 10.0), 2.7, 2.7);
            dc.DrawEllipse(accentBrush, whitePen, new Point(20.0, 16.0), 3.1, 3.1);
            dc.DrawEllipse(Brushes.White, null, new Point(14.0, 22.0), 2.7, 2.7);
        }

        private static void DrawPause(DrawingContext dc)
        {
            DrawSquircle(dc, Color.FromRgb(0xEA, 0x8A, 0x16), AmberBg, AmberBorder);

            dc.DrawRoundedRectangle(Brushes.White, null, new Rect(10.0, 9.5, 4.2, 13.0), 1.5, 1.5);
            dc.DrawRoundedRectangle(Brushes.White, null, new Rect(17.8, 9.5, 4.2, 13.0), 1.5, 1.5);
        }

        private static void DrawPython(DrawingContext dc)
        {
            DrawSquircle(dc, Color.FromRgb(0x2A, 0x32, 0x42), TerminalBg, TerminalBorder);

            var promptPen = new Pen(FrozenBrush(SoftAccent), 2.5)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            };
            promptPen.Freeze();

            // '>' chevron
            var chevron = new StreamGeometry();
            using (var ctx = chevron.Open())
            {
                ctx.BeginFigure(new Point(8.5, 11.0), false, false);
                ctx.LineTo(new Point(14.5, 16.0), true, false);
                ctx.LineTo(new Point(8.5, 21.0), true, false);
            }
            chevron.Freeze();
            dc.DrawGeometry(null, promptPen, chevron);

            // '_' cursor bar
            var cursorPen = new Pen(Brushes.White, 2.5)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            cursorPen.Freeze();
            dc.DrawLine(cursorPen, new Point(16.5, 21.0), new Point(23.5, 21.0));
        }

        private static void DrawAbout(DrawingContext dc)
        {
            DrawSquircle(dc, Color.FromRgb(0xEC, 0x72, 0x10), ScioOrangeDark, ScioOrangeDark);

            // Subtle inner architectural ring
            var ringPen = new Pen(FrozenBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)), 1.4);
            ringPen.Freeze();
            dc.DrawEllipse(null, ringPen, new Point(16.0, 16.0), 10.0, 10.0);

            // Crisp 'i' dot + stem
            dc.DrawEllipse(Brushes.White, null, new Point(16.0, 10.5), 1.9, 1.9);
            dc.DrawRoundedRectangle(Brushes.White, null, new Rect(14.3, 14.3, 3.4, 8.8), 1.6, 1.6);
        }

        private static SolidColorBrush FrozenBrush(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }
}
