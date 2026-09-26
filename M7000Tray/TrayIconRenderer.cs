using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace M7000Tray;

public static partial class TrayIconRenderer
{
    public static Color ColorFor(double percent) => percent switch
    {
        >= 90 => Color.FromArgb(0x00, 0xBF, 0xFF), // небесно-голубой
        >= 40 => Color.FromArgb(0x2E, 0xCC, 0x40), // зелёный
        >= 10 => Color.FromArgb(0xFF, 0xD7, 0x00), // жёлтый
        _ => Color.FromArgb(0xFF, 0x3B, 0x30),     // красный
    };

    /// <summary>percent = остаток трафика 0..100; null — данных ещё нет, идёт загрузка (серое кольцо и «?»).</summary>
    public static Icon Render(double? percent)
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

            string text = percent is { } v ? ((int)Math.Floor(Math.Clamp(v, 0, 100))).ToString() : "?";
            float fontPx = size * (text.Length >= 3 ? 0.46f : 0.62f);
            using var font = new Font(text.Length >= 3 ? "Segoe UI Semibold" : "Segoe UI", fontPx, text.Length >= 3 ? FontStyle.Regular : FontStyle.Bold, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(IsTaskbarLight() ? Color.Black : Color.White);
            using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(text, font, brush, new RectangleF(0, 0, size, size + 1), sf);
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
