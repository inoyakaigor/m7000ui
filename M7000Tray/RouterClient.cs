using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace M7000Tray;

public record Sms(string From, string ReceivedTime, string Content, int Index = -1);

/// <summary>RouterTotalBytes — счётчик роутера за его расчётный период (обнуляется в день оплаты).</summary>
public record RouterSnapshot(double RouterTotalBytes, string? SimNumber, IReadOnlyList<Sms> LatestSms,
    int? BatteryPercent, bool Charging, int? SignalLevel, bool Roaming, bool Lte,
    double? Rsrp, double? Rsrq, double? Snr, int UnreadSms, string? RouterMac);

/// <summary>Роутер отклонил пароль. Повторять нельзя: после 10 неудач вход блокируется на 2 часа.</summary>
public sealed class LoginRejectedException() : Exception("Роутер отклонил пароль");

/// <summary>Ответ роутера не разобрался как JSON — обычно расшифровался в мусор. Помогает новая сессия.</summary>
public sealed class BadResponseException(string message) : Exception(message);

public static partial class RouterClient
{
    public const string RouterUrl = "http://192.168.0.1/";

    // Сессии к роутеру — строго по одной. Второй handshake выдаёт новый nonce, и логин первой сессии
    // роутер засчитывает как неверный пароль (тратится попытка из 10). Так было: опрос + «прочитано» при запуске.
    static readonly SemaphoreSlim Gate = new(1, 1);

    // ponytail: новая сессия (handshake + login) на каждый опрос — раз в минуту это дёшево
    // и не нужно ловить протухание сессии. Если роутер начнёт выкидывать веб-админку — держать сессию.
    static async Task<(HttpClient Http, TPLinkProtocol Router)> OpenSessionAsync(string password)
    {
        var cookies = new CookieContainer();
        var http = new HttpClient(new HttpClientHandler { CookieContainer = cookies })
        {
            BaseAddress = new Uri(RouterUrl),
            Timeout = TimeSpan.FromSeconds(15),
        };
        var router = new TPLinkProtocol(http, cookies);
        try
        {
            if (!await router.Handshake()) throw new Exception("Роутер не ответил на handshake");
            if (!await router.Login(password)) throw new LoginRejectedException();
            return (http, router);
        }
        catch { http.Dispose(); throw; }
    }

    public static async Task MarkReadAsync(string password, int index)
    {
        await Gate.WaitAsync();
        try
        {
            var (http, router) = await OpenSessionAsync(password);
            using (http) Parse(await router.MarkSmsRead(index), "пометки SMS").Dispose();
        }
        finally { Gate.Release(); }
    }

    public static async Task<RouterSnapshot> PollAsync(string password)
    {
        await Gate.WaitAsync();
        try
        {
            try { return await PollLockedAsync(password); }
            // Изредка ответ на запрос SMS расшифровывается в мусор (сырой ответ — в crash.log). Одна повторная попытка в новой сессии.
            catch (BadResponseException) { return await PollLockedAsync(password); }
        }
        finally { Gate.Release(); }
    }

    static async Task<RouterSnapshot> PollLockedAsync(string password)
    {
        var (http, router) = await OpenSessionAsync(password);
        using var _ = http;

        using var status = Parse(await router.RequestModule("status"), "status");
        // totalStatistics — байты за текущий расчётный период (сбрасывается в день оплаты).
        double used = double.Parse(status.RootElement.GetProperty("wan").GetProperty("totalStatistics").GetString()!, CultureInfo.InvariantCulture);

        // Первая страница входящих, от новых к старым.
        using var sms = Parse(await router.RequestUnreadSms(1, 8), "SMS");
        var list = sms.RootElement.TryGetProperty("messageList", out var ml) && ml.ValueKind == JsonValueKind.Array
            ? ml.EnumerateArray().Select(m => new Sms(
                m.GetProperty("from").GetString() ?? "",
                m.GetProperty("receivedTime").GetString() ?? "",
                m.GetProperty("content").GetString() ?? "",
                m.TryGetProperty("index", out var ix) && ix.TryGetInt32(out var iv) ? iv : -1)).ToList()
            : [];

        string? sim = status.RootElement.TryGetProperty("deviceInfo", out var di) && di.TryGetProperty("simNumber", out var sn) ? sn.GetString() : null;

        var root = status.RootElement;
        // battery.voltage у M7000 — это процент заряда 0–100, а не вольты.
        int? battery = root.TryGetProperty("battery", out var bat) && bat.TryGetProperty("voltage", out var v) && v.TryGetInt32(out var pct) ? pct : null;
        bool charging = bat.ValueKind == JsonValueKind.Object && bat.TryGetProperty("charging", out var ch) && ch.ValueKind == JsonValueKind.True;

        var wan = root.GetProperty("wan");
        // networkType 0 — нет сети (login.min.js: noSevrice: 0). Тогда уровень сигнала не показываем.
        int networkType = wan.TryGetProperty("networkType", out var nt) && nt.TryGetInt32(out var ntv) ? ntv : 0;
        bool hasNetwork = networkType != 0;
        int? signal = hasNetwork && wan.TryGetProperty("signalStrength", out var ss) && ss.TryGetInt32(out var sv) ? sv : null;
        bool roaming = wan.TryGetProperty("roaming", out var rm) && rm.TryGetInt32(out var rv) && rv != 0;

        bool lte = networkType == 3; // login.min.js: lte: 3

        double? Num(string name) => wan.TryGetProperty(name, out var e) && e.TryGetDouble(out var d) ? d : null;
        // ponytail: snr приходит как 40/80 — считаем, что это десятые доли dB (в прошивке масштаб не нашли). Сверить с админкой.
        double? snr = Num("snr") / 10;

        return new RouterSnapshot(used, sim, list, battery, charging, signal, roaming, lte, Num("rsrp"), Num("rsrq"), snr,
            root.TryGetProperty("message", out var msg) && msg.TryGetProperty("unreadMessages", out var um) && um.TryGetInt32(out var u) ? u : 0,
            di.ValueKind == JsonValueKind.Object && di.TryGetProperty("mac", out var mac) && mac.GetString() is { } m ? NormalizeMac(m) : null);
    }

    /// <summary>"3C:6A:D2:24:DC:A0" / "3c-6a-..." → "3C6AD224DCA0".</summary>
    public static string NormalizeMac(string mac) => new string(mac.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();

    /// <summary>
    /// MAC устройства на 192.168.0.1 через ARP (SendARP: из кэша или ARP-запросом). Это не HTTP и не логин —
    /// роутер не выдаёт nonce и не тратит попытку входа. null — никто не ответил (нет сети или роутера).
    /// Блокирует поток до ~3 с, звать из Task.Run.
    /// </summary>
    public static string? GatewayMac()
    {
        uint ip = BitConverter.ToUInt32(IPAddress.Parse(new Uri(RouterUrl).Host).GetAddressBytes(), 0);
        var buf = new byte[6];
        uint len = (uint)buf.Length;
        if (SendARP(ip, 0, buf, ref len) != 0 || len == 0) return null;
        return Convert.ToHexString(buf, 0, (int)len);
    }

    [LibraryImport("iphlpapi.dll")]
    private static partial int SendARP(uint destIp, uint srcIp, byte[] macAddr, ref uint macAddrLen);

    /// <summary>Сетевой сбой (нет сети, обрыв, таймаут) — не баг приложения, в crash.log не пишем.</summary>
    public static bool IsNetworkError(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e is SocketException or HttpRequestException or TaskCanceledException or TimeoutException or IOException) return true;
        return false;
    }

    // Ответ с result != 0 (например -3 — нет сессии) превращаем в понятную ошибку, а не KeyNotFound дальше.
    static JsonDocument Parse(string body, string what)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException)
        {
            // Сбой плавающий — сохраняем сырой ответ, чтобы было что разбирать.
            Settings.LogCrash($"Не JSON в ответе {what} (длина {body.Length}): {(body.Length > 500 ? body[..500] : body)}");
            string head = body.Length > 40 ? body[..40] : body;
            throw new BadResponseException($"Не удалось прочитать ответ {what}: {head}");
        }
        if (doc.RootElement.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.Number && r.GetInt32() != 0)
        {
            int code = r.GetInt32();
            doc.Dispose();
            throw new Exception($"Роутер вернул ошибку {code} на запрос {what}");
        }
        return doc;
    }
}
