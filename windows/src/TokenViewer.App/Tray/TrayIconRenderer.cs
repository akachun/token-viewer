using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TokenViewer.Core.Models;
using TokenViewer.Core.Services;

namespace TokenViewer.App.Tray;

/// <summary>
/// Draws one provider's tray icon (ring gauge, big number, or letter + status dot) with WPF and
/// converts it to a GDI icon for the notification area.
/// </summary>
public static class TrayIconRenderer
{
    public const int DefaultSize = 32;

    private static readonly Typeface Bold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    public static BitmapSource RenderBitmap(ProviderUsageSnapshot snapshot, AppSettings settings, bool lightTaskbar, int size = DefaultSize)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            Draw(dc, snapshot, settings, lightTaskbar, size);

        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }

    public static System.Drawing.Icon RenderIcon(ProviderUsageSnapshot snapshot, AppSettings settings, bool lightTaskbar, int size = DefaultSize)
    {
        var bitmap = RenderBitmap(snapshot, settings, lightTaskbar, size);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        ms.Position = 0;

        using var gdiBitmap = new System.Drawing.Bitmap(ms);
        var hIcon = gdiBitmap.GetHicon();
        try
        {
            using var temp = System.Drawing.Icon.FromHandle(hIcon);
            return (System.Drawing.Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    private static void Draw(DrawingContext dc, ProviderUsageSnapshot snapshot, AppSettings settings, bool lightTaskbar, int size)
    {
        var s = (double)size;
        var foreground = lightTaskbar ? Color.FromRgb(0x1F, 0x1F, 0x1F) : Colors.White;
        var window = snapshot.PrimaryDisplayWindow(settings.WindowStrategy);
        var active = snapshot.AuthStatus.IsAuthenticated && window is not null;
        var tint = active ? ToColor(ResolveTint(snapshot.Provider, window, settings, foreground)) : WithAlpha(foreground, 0x80);
        var value = TrayText.IconValue(snapshot, settings);
        var letter = snapshot.Provider.SingleLetter();

        switch (settings.DisplayStyle)
        {
            case TrayDisplayStyle.Number:
            {
                var text = active ? value : letter;
                var fontSize = text.Length >= 3 ? s * 0.50 : s * 0.72;
                DrawCenteredText(dc, text, fontSize, new SolidColorBrush(active ? tint : WithAlpha(foreground, 0x99)), s);
                break;
            }

            case TrayDisplayStyle.StatusDot:
            {
                DrawCenteredText(dc, letter, s * 0.78, new SolidColorBrush(foreground), s, offsetX: -s * 0.08, offsetY: -s * 0.04);
                var r = s * 0.2;
                var center = new Point(s - r - 1, s - r - 1);
                dc.DrawEllipse(new SolidColorBrush(tint), new Pen(new SolidColorBrush(lightTaskbar ? Colors.White : Colors.Black), 1.5), center, r, r);
                break;
            }

            default: // Ring
            {
                var thickness = s * 0.14;
                var radius = (s - thickness) / 2 - 0.5;
                var center = new Point(s / 2, s / 2);

                dc.DrawEllipse(null, new Pen(new SolidColorBrush(WithAlpha(foreground, 0x40)), thickness), center, radius, radius);
                if (active)
                    DrawArc(dc, center, radius, window!.DisplayFraction(settings.MetricMode), new Pen(new SolidColorBrush(tint), thickness)
                    {
                        StartLineCap = PenLineCap.Round,
                        EndLineCap = PenLineCap.Round,
                    });

                var showValue = active && settings.ShowPercentInIcon && value.Length <= 2;
                var text = showValue ? value : letter;
                DrawCenteredText(dc, text, showValue ? s * 0.40 : s * 0.46, new SolidColorBrush(foreground), s);
                break;
            }
        }
    }

    private static RgbColor ResolveTint(ProviderType provider, UsageWindow? window, AppSettings settings, Color foreground) =>
        settings.ColorMode switch
        {
            ColorMode.BrandColor => provider.AccentColor(),
            ColorMode.Monochrome => new RgbColor(foreground.R, foreground.G, foreground.B),
            _ => HealthColors.For(window, settings.WarningThresholdPercent),
        };

    /// <summary>Clockwise arc starting at 12 o'clock covering <paramref name="fraction"/> of the circle.</summary>
    private static void DrawArc(DrawingContext dc, Point center, double radius, double fraction, Pen pen)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        if (fraction <= 0.005) return;
        if (fraction >= 0.995)
        {
            dc.DrawEllipse(null, pen, center, radius, radius);
            return;
        }

        var angle = fraction * 2 * Math.PI;
        var start = new Point(center.X, center.Y - radius);
        var end = new Point(center.X + radius * Math.Sin(angle), center.Y - radius * Math.Cos(angle));

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(start, isFilled: false, isClosed: false);
            ctx.ArcTo(end, new Size(radius, radius), 0, isLargeArc: fraction > 0.5, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    private static void DrawCenteredText(DrawingContext dc, string text, double fontSize, Brush brush, double s, double offsetX = 0, double offsetY = 0)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Bold, fontSize, brush, 1.0);
        // Shrink to fit if the text is wider than the icon.
        if (ft.Width > s - 2) ft.SetFontSize(fontSize * (s - 2) / ft.Width);
        var origin = new Point((s - ft.Width) / 2 + offsetX, (s - ft.Height) / 2 + offsetY);
        dc.DrawText(ft, origin);
    }

    private static Color ToColor(RgbColor c) => Color.FromRgb(c.R, c.G, c.B);
    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
