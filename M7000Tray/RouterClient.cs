using System.Globalization;
using System.Net;
using System.Text.Json;

namespace M7000Tray;

public record Sms(string From, string ReceivedTime, string Content);

/// <summary>RouterTotalBytes — счётчик роутера за его расчётный период (обнуляется в день оплаты).</summary>
public record RouterSnapshot(double RouterTotalBytes, string? SimNumber, IReadOnlyList<Sms> LatestSms);

/// <summary>Роутер отклонил пароль. Повторять нельзя: после 10 неудач вход блокируется на 2 часа.</summary>
public sealed class LoginRejectedException() : Exception("Роутер отклонил пароль");

public static class RouterClient
{
    public const string RouterUrl = "http://192.168.0.1/";

    // ponytail: новая сессия (handshake + login) на каждый опрос — раз в минуту это дёшево
    // и не нужно ловить протухание сессии. Если роутер начнёт выкидывать веб-админку — держать сессию.
    public static async Task<RouterSnapshot> PollAsync(string password)
    {
        var cookies = new CookieContainer();
        using var http = new HttpClient(new HttpClientHandler { CookieContainer = cookies })
        {
            BaseAddress = new Uri(RouterUrl),
            Timeout = TimeSpan.FromSeconds(15),
        };
        var router = new TPLinkProtocol(http, cookies);

        if (!await router.Handshake()) throw new Exception("Роутер не ответил на handshake");
        if (!await router.Login(password)) throw new LoginRejectedException();

        using var status = Parse(await router.RequestModule("status"), "status");
        // totalStatistics — байты за текущий расчётный период (сбрасывается в день оплаты).
        double used = double.Parse(status.RootElement.GetProperty("wan").GetProperty("totalStatistics").GetString()!, CultureInfo.InvariantCulture);

        // Первая страница входящих, от новых к старым.
        using var sms = Parse(await router.RequestUnreadSms(1, 8), "SMS");
        var list = sms.RootElement.TryGetProperty("messageList", out var ml) && ml.ValueKind == JsonValueKind.Array
            ? ml.EnumerateArray().Select(m => new Sms(
                m.GetProperty("from").GetString() ?? "",
                m.GetProperty("receivedTime").GetString() ?? "",
                m.GetProperty("content").GetString() ?? "")).ToList()
            : [];

        string? sim = status.RootElement.TryGetProperty("deviceInfo", out var di) && di.TryGetProperty("simNumber", out var sn) ? sn.GetString() : null;

        return new RouterSnapshot(used, sim, list);
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
            throw new Exception($"Не удалось прочитать ответ {what}: {head}");
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
