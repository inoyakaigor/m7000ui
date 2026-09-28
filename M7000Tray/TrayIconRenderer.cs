using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace M7000Tray;

/// <summary>Что рисовать внутри кольца. Порядок — приоритет: LowBattery важнее Envelope, Envelope важнее Percent.</summary>
public enum TrayBadge { Percent, Envelope, LowBattery }

public static partial class TrayIconRenderer
{
    public static readonly Color Gold = Color.FromArgb(0xD2, 0xB0, 0x01); // как значок роуминга в окне настроек

    public static TrayBadge ChooseBadge(int? batteryPercent, int unreadSms) =>
        batteryPercent < 10 ? TrayBadge.LowBattery : unreadSms > 0 ? TrayBadge.Envelope : TrayBadge.Percent;

    public static Color ColorFor(double percent) => percent switch
    {
        >= 90 => Color.FromArgb(0x00, 0xBF, 0xFF), // небесно-голубой
        >= 40 => Color.FromArgb(0x2E, 0xCC, 0x40), // зелёный
        >= 10 => Color.FromArgb(0xFF, 0xD7, 0x00), // жёлтый
        _ => Color.FromArgb(0xFF, 0x3B, 0x30),     // красный
    };

    /// <summary>percent = остаток трафика 0..100; null — данных ещё нет, идёт загрузка (серое кольцо и «?»).</summary>
    public static Icon Render(double? percent, TrayBadge badge = TrayBadge.Percent, int batteryPercent = 0, bool charging = false)
    {
        int size = 16 * (int)GetDpiForSystem() / 96;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;

            float stroke = Math.Max(1.5f, size / 10f);
            var rect = new RectangleF(stroke / 2, stroke / 2, size - stroke, size - stroke);
            if (percent is { } p)
            {
                using var pen = new Pen(ColorFor(p), stroke);
                float sweep = (float)(Math.Clamp(p, 0, 100) * 3.6);
                if (sweep > 0) g.DrawArc(pen, rect, -90, sweep); // от 12 часов по часовой
            }
            else
            {
                using var pen = new Pen(Color.Gray, stroke);
                g.DrawEllipse(pen, rect);
            }

            string text; string fontName; FontStyle style; float fontPx; Color color;
            switch (badge)
            {
                case TrayBadge.LowBattery:
                    (text, fontName, style, fontPx, color) = (Glyphs.Battery(batteryPercent, charging), "Segoe Fluent Icons", FontStyle.Regular, size * 0.62f, ColorFor(0));
                    break;
                case TrayBadge.Envelope:
                    (text, fontName, style, fontPx, color) = ("\uE715", "Segoe Fluent Icons", FontStyle.Regular, size * 0.6f, Gold); // закрытый конверт
                    break;
                default:
                    text = percent is { } v ? ((int)Math.Floor(Math.Clamp(v, 0, 100))).ToString() : "?";
                    bool three = text.Length >= 3;
                    (fontName, style, fontPx) = (three ? "Segoe UI Semibold" : "Segoe UI", three ? FontStyle.Regular : FontStyle.Bold, size * (three ? 0.46f : 0.62f));
                    color = IsTaskbarLight() ? Color.Black : Color.White;
                    break;
            }
            using var family = new FontFamily(fontName);
            using var brush = new SolidBrush(color);

            // Центрируем по реальным контурам, а не по метрикам строки (там запас под выносные элементы — цифры уезжали).
            using var path = new GraphicsPath();
            path.AddString(text, family, (int)style, fontPx, PointF.Empty, StringFormat.GenericTypographic);
            var b = path.GetBounds();
            using var shift = new Matrix();
            shift.Translate(size / 2f - (b.X + b.Width / 2), size / 2f - (b.Y + b.Height / 2));
            path.Transform(shift);
            g.FillPath(brush, path);
            // Глифы Fluent — тонкий контур, в 16–32 px он бледнеет. Обводим тем же цветом для толщины.
            if (badge != TrayBadge.Percent)
            {
                using var pen = new Pen(color, Math.Max(1f, size / 20f)) { LineJoin = LineJoin.Round };
                g.DrawPath(pen, path);
            }
        }

        // Icon.FromHandle не владеет HICON: клонируем в самостоятельную иконку и сразу освобождаем исходный.
        IntPtr h = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(h).Clone(); }
        finally { DestroyIcon(h); }
    }

    static bool IsTaskbarLight() =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) is 1;

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForSystem();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr hIcon);
}
