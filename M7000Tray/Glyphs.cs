namespace M7000Tray;

/// <summary>Глифы Segoe Fluent Icons для шапки окна.</summary>
public static class Glyphs
{
    /// <summary>E850…E859 — по десяткам процентов, E83F — полная. При зарядке E85A…E862 и E83E.</summary>
    public static string Battery(int percent, bool charging)
    {
        percent = Math.Clamp(percent, 0, 100);
        if (percent == 100) return charging ? "\uE83E" : "\uE83F";
        return charging
            ? char.ConvertFromUtf32(0xE85A + percent * 9 / 100)  // 9 ступеней
            : char.ConvertFromUtf32(0xE850 + percent / 10);      // 10 ступеней
    }
}
