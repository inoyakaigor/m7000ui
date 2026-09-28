using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace M7000Tray;

public sealed class Settings
{
    // Пароль хранится зашифрованным DPAPI (привязан к пользователю Windows).
    public string? PasswordProtected { get; set; }
    public double LimitGb { get; set; } = 50;
    // receivedTime самой новой SMS, о которой уже знаем ("yyyy-MM-dd HH:mm:ss").
    public string? LastSmsTime { get; set; }
    public bool AutoStart { get; set; } = true;
    // Израсходовано из текущего пакета. Копится приложением из приращений счётчика роутера,
    // потому что пакет живёт по своему графику, а счётчик роутера обнуляется в день оплаты.
    public double UsedBytes { get; set; }
    // Последнее увиденное значение totalStatistics роутера — база для следующего приращения.
    public double? LastRouterTotal { get; set; }

    public const double BytesPerGb = 1024d * 1024 * 1024; // роутер считает в ГиБ

    /// <summary>Сколько байт ушло с прошлого опроса. Счётчик роутера уменьшился — значит, он обнулился, и всё текущее — новое.</summary>
    public static double RouterDelta(double? last, double current) =>
        last is not { } l ? 0 : current >= l ? current - l : current;

    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Igor Zviagintsev", "M7000");
    static readonly string FilePath = Path.Combine(Dir, "settings.ini");

    // Время записи файла, который отражает этот объект. Новее — значит, ini поправили руками.
    DateTime _fileTime;

    public string Password
    {
        get => PasswordProtected is null ? "" :
            Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(PasswordProtected), null, DataProtectionScope.CurrentUser));
        set => PasswordProtected = string.IsNullOrEmpty(value) ? null :
            Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
    }

    /// <summary>Приводит HKCU\...\Run в соответствие с AutoStart. Путь переписывается каждый раз — exe могли переместить.</summary>
    public void ApplyAutoStart()
    {
        using var run = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (AutoStart) run.SetValue("M7000", $"\"{Environment.ProcessPath}\"");
        else run.DeleteValue("M7000", throwOnMissingValue: false);
    }

    /// <summary>Пишет необработанное исключение в crash.log рядом с settings.ini — у tray-приложения нет консоли.</summary>
    public static void LogCrash(object error)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.AppendAllText(Path.Combine(Dir, "crash.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {error}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { }
    }

    public static Settings Load()
    {
        var s = new Settings();
        if (!File.Exists(FilePath)) return s;
        s._fileTime = File.GetLastWriteTimeUtc(FilePath);

        // Плоский ini: key=value, секции и комментарии (; #) игнорируются.
        foreach (var line in File.ReadAllLines(FilePath))
        {
            int eq = line.IndexOf('=');
            if (eq <= 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
            string key = line[..eq].Trim(), value = line[(eq + 1)..].Trim();
            switch (key)
            {
                case nameof(PasswordProtected): s.PasswordProtected = value.Length > 0 ? value : null; break;
                case nameof(LimitGb) when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var gb) && gb > 0: s.LimitGb = gb; break;
                case nameof(LastSmsTime): s.LastSmsTime = value.Length > 0 ? value : null; break;
                case nameof(AutoStart) when bool.TryParse(value, out var on): s.AutoStart = on; break;
                case nameof(UsedBytes) when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var used): s.UsedBytes = used; break;
                case nameof(LastRouterTotal) when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var total): s.LastRouterTotal = total; break;
            }
        }
        return s;
    }

    /// <summary>Перечитывает ini, если его изменили снаружи. Объект остаётся тем же — на него ссылаются App и окно.</summary>
    public bool ReloadIfChanged()
    {
        if (!File.Exists(FilePath) || File.GetLastWriteTimeUtc(FilePath) == _fileTime) return false;
        var f = Load();
        PasswordProtected = f.PasswordProtected;
        LimitGb = f.LimitGb;
        LastSmsTime = f.LastSmsTime;
        AutoStart = f.AutoStart;
        UsedBytes = f.UsedBytes;
        LastRouterTotal = f.LastRouterTotal;
        _fileTime = f._fileTime;
        return true;
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllLines(FilePath,
        [
            "[M7000]",
            $"{nameof(PasswordProtected)}={PasswordProtected}",
            $"{nameof(LimitGb)}={LimitGb.ToString(CultureInfo.InvariantCulture)}",
            $"{nameof(LastSmsTime)}={LastSmsTime}",
            $"{nameof(AutoStart)}={AutoStart}",
            $"{nameof(UsedBytes)}={UsedBytes.ToString("R", CultureInfo.InvariantCulture)}",
            $"{nameof(LastRouterTotal)}={LastRouterTotal?.ToString("R", CultureInfo.InvariantCulture)}",
        ]);
        _fileTime = File.GetLastWriteTimeUtc(FilePath);
    }
}
