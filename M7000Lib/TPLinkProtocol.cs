using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

public class TPLinkProtocol
{
    private readonly HttpClient _http;
    private readonly System.Net.CookieContainer _cookies;
    private string _aesKey = string.Empty;
    private string _aesIv = string.Empty;
    private string _hash = string.Empty;
    private long _seq;
    private BigInteger _rsaMod;
    private string _nonce = string.Empty;
    private string? _token;
    private bool _useGdpr = false;

    public TPLinkProtocol(HttpClient http, System.Net.CookieContainer cookies)
    {
        _http = http;
        _cookies = cookies;
        GenAESKey();

        // Console.WriteLine("[DEBUG][INIT] Сгенерированы локальными ключи:");
        // Console.WriteLine($"      AES Key: {_aesKey}");
        // Console.WriteLine($"      AES IV:  {_aesIv}");
    }

    private void LogCookies(string context)
    {
        try
        {
            var baseAddress = _http.BaseAddress ?? new Uri("http://192.168.0.1/");
            var cookies = _cookies.GetCookies(baseAddress);
            // Console.WriteLine($"[DEBUG][COOKIES] {context}: Count={cookies.Count}");
            foreach (System.Net.Cookie c in cookies)
            {
                // Console.WriteLine($"[DEBUG][COOKIES]   - {c.Name} = {c.Value}");
            }
        }
        catch (Exception ex)
        {
            // Console.WriteLine($"[DEBUG][COOKIES] Failed to dump cookies: {ex.Message}");
        }
    }

    private void GenAESKey()
    {
        // В JS: var c = ((new Date).getTime() + "" + 1e9 * Math.random()).substr(0, 16)
        string GenerateJsToken()
        {
            string ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
            string rand = new Random().Next(1000000000).ToString();
            string combined = ts + rand;
            return combined.Substring(0, 16); // Обрезаем до 16 символов
        }

        _aesKey = GenerateJsToken();
        _aesIv = GenerateJsToken();

        // Console.WriteLine($"[DEBUG][GEN] Сгенерирован ключ: {_aesKey}");
        // Console.WriteLine($"[DEBUG][GEN] Сгенерирован IV:  {_aesIv}");
    }

    public async Task<bool> Handshake()
    {
        // Console.WriteLine("\n[DEBUG][STEP 1] Начало Handshake...");
        var payload = new { module = "authenticator", action = 0 };
        string jsonPayload = JsonSerializer.Serialize(payload);
        string base64Payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(jsonPayload));

        // Роутер ожидает JSON с полем data
        string requestBody = $"{{\"data\":\"{base64Payload}\"}}";
        // Console.WriteLine($"[DEBUG][HTTP] POST to /cgi-bin/auth_cgi: {requestBody}");

        var response = await _http.PostAsync("cgi-bin/auth_cgi",
            new StringContent(requestBody, Encoding.UTF8, "application/json"));

        string body = await response.Content.ReadAsStringAsync();
        // Console.WriteLine($"[DEBUG][HTTP] Raw Response: {body}");

        // --- ИСПРАВЛЕНИЕ: Декодируем Base64 ответ, если он пришел сырой строкой ---
        string decodedJson = body;
        try
        {
            if (!body.Trim().StartsWith("{"))
            {
                byte[] decodedBytes = Convert.FromBase64String(body);
                decodedJson = Encoding.UTF8.GetString(decodedBytes);
                // Console.WriteLine($"[DEBUG][HANDSHAKE] Декодированный JSON из Base64: {decodedJson}");
            }
        }
        catch (Exception ex)
        {
            // Console.WriteLine($"[DEBUG][HANDSHAKE] Не удалось декодировать Base64: {ex.Message}");
        }

        using var doc = JsonDocument.Parse(decodedJson);
        var root = doc.RootElement;

        if (root.TryGetProperty("rsaMod", out var mod))
        {
            _rsaMod = BigInteger.Parse("0" + mod.GetString(), System.Globalization.NumberStyles.HexNumber);
            _seq = root.GetProperty("seqNum").GetInt64();
            _nonce = root.GetProperty("nonce").GetString() ?? string.Empty;

            // Console.WriteLine("[DEBUG][HANDSHAKE] Параметры получены успешно.");
            // Console.WriteLine($"      Sequence: {_seq} | Nonce: {_nonce}");
            return true;
        }

        // Console.WriteLine("[DEBUG][HANDSHAKE] Ошибка: rsaMod не найден в ответе.");
        return false;
    }



    public async Task<string> RequestModule(string moduleName)
    {
        // Console.WriteLine($"\n[DEBUG][STEP 3] Запрос модуля: {moduleName}");
        var payload = new { module = moduleName, action = 0 };
        return await Request(moduleName, payload, "cgi-bin/web_cgi");
    }

    public async Task<string> RequestUnreadSms(int page = 1, int amountPerPage = 8)
    {
        // Console.WriteLine($"\n[DEBUG][STEP 4] Запрос непрочитанных SMS сообщений (message action=2)...");
        var payload = new
        {
            module = "message",
            action = 2,
            pageNumber = page,
            amountPerPage = amountPerPage,
            box = 0
        };
        return await Request("message", payload, "cgi-bin/web_cgi");
    }

    /// <summary>Помечает SMS прочитанным (message action=6, как markRead в tpweb.min.js). index — поле index из messageList.</summary>
    public async Task<string> MarkSmsRead(int index)
    {
        var payload = new
        {
            module = "message",
            action = 6,
            markReadMessage = index
        };
        return await Request("message", payload, "cgi-bin/web_cgi");
    }

    private string CalculateMD5(string input)
    {
        using MD5 md5 = MD5.Create();
        byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(input));
        string md5string = BitConverter.ToString(hash).Replace("-", "").ToLower();
        // Console.WriteLine($"MD5 string is: {md5string}");
        return md5string;
    }

    private string DecryptAESWithZeroIV(string encryptedBody)
    {
        try
        {
            string base64Data = encryptedBody;
            if (encryptedBody.Contains("\"data\""))
            {
                using var doc = JsonDocument.Parse(encryptedBody);
                base64Data = doc.RootElement.GetProperty("data").GetString() ?? string.Empty;
            }

            byte[] cipherBytes = Convert.FromBase64String(base64Data ?? string.Empty);
            using Aes aes = Aes.Create();
            aes.Key = Encoding.UTF8.GetBytes(_aesKey);
            aes.IV = new byte[16]; // 16 нулей
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var decryptor = aes.CreateDecryptor();
            byte[] plainBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);

            return Encoding.UTF8.GetString(plainBytes).Trim();
        }
        catch (Exception ex)
        {
            // Console.WriteLine($"[DEBUG][DECRYPT] Zero-IV decryption failed: {ex.Message}");
            return "DECRYPT_FINAL_FAILURE";
        }
    }
    private string DecryptAES(string encryptedBody)
    {
        string trimmed = encryptedBody.Trim();
        // Console.WriteLine($"[DEBUG][DECRYPT] Raw response length={trimmed.Length}, first 20 chars: {trimmed.Substring(0, Math.Min(20, trimmed.Length))}");
        
        // Check if it's already plain JSON
        if (trimmed.StartsWith("{") || trimmed.StartsWith("["))
        {
            // Console.WriteLine($"[DEBUG][DECRYPT] Response is plain JSON, returning as-is");
            return trimmed;
        }

        // Try to decode JSON-wrapped encrypted data first
        try
        {
            using var jsonDoc = JsonDocument.Parse(trimmed);
            if (jsonDoc.RootElement.TryGetProperty("data", out var dataElem))
            {
                string wrapped = dataElem.GetString() ?? string.Empty;
                // Console.WriteLine($"[DEBUG][DECRYPT] Response contains JSON data wrapper.");
                trimmed = wrapped;
            }
        }
        catch (JsonException)
        {
            // Not JSON, continue with base64 decode
        }

        try
        {
            byte[] base64Bytes = Convert.FromBase64String(trimmed);
            string decoded = Encoding.UTF8.GetString(base64Bytes);

            if (decoded.StartsWith("{") || decoded.StartsWith("["))
            {
                // Console.WriteLine($"[DEBUG][DECRYPT] Response was base64-wrapped JSON, decoded successfully: {decoded.Substring(0, Math.Min(80, decoded.Length))}");
                return decoded;
            }

            // Console.WriteLine($"[DEBUG][DECRYPT] Response is base64-encoded encrypted data, attempting AES decryption...");
            using Aes aes = Aes.Create();
            aes.Key = Encoding.UTF8.GetBytes(_aesKey);
            aes.IV = Encoding.UTF8.GetBytes(_aesIv);
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var decryptor = aes.CreateDecryptor();
            byte[] plainBytes = decryptor.TransformFinalBlock(base64Bytes, 0, base64Bytes.Length);
            string result = Encoding.UTF8.GetString(plainBytes).Trim();
            // Console.WriteLine($"[DEBUG][DECRYPT] AES decryption SUCCESS");
            return result;
        }
        catch (FormatException)
        {
            // Console.WriteLine($"[DEBUG][DECRYPT] Not base64 format");
            return "DECRYPT_FAILURE: Invalid format";
        }
        catch (Exception ex)
        {
            // Console.WriteLine($"[DEBUG][DECRYPT] Error: {ex.Message}. Trying Zero IV...");
            string zeroIvResult = DecryptAESWithZeroIV(encryptedBody);
            if (zeroIvResult == "DECRYPT_FINAL_FAILURE")
            {
                // Debug raw AES output with no padding to inspect malformed/invalid payloads.
                try
                {
                    string debugTrimmed = encryptedBody.Trim();
                    if (debugTrimmed.StartsWith('{') || debugTrimmed.StartsWith('['))
                    {
                        using var doc = JsonDocument.Parse(debugTrimmed);
                        if (doc.RootElement.TryGetProperty("data", out var wrapped))
                        {
                            debugTrimmed = wrapped.GetString() ?? string.Empty;
                        }
                    }

                    byte[] base64Bytes = Convert.FromBase64String(debugTrimmed);
                    using Aes aes = Aes.Create();
                    aes.Key = Encoding.UTF8.GetBytes(_aesKey);
                    aes.IV = Encoding.UTF8.GetBytes(_aesIv);
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.None;

                    using var decryptor = aes.CreateDecryptor();
                    byte[] rawPlainBytes = decryptor.TransformFinalBlock(base64Bytes, 0, base64Bytes.Length);
                    // Console.WriteLine($"[DEBUG][DECRYPT] NoPadding raw plaintext bytes: {BitConverter.ToString(rawPlainBytes)}");
                    // Console.WriteLine($"[DEBUG][DECRYPT] NoPadding raw plaintext UTF8: {Encoding.UTF8.GetString(rawPlainBytes)}");
                }
                catch (Exception ex2)
                {
                    // Console.WriteLine($"[DEBUG][DECRYPT] NoPadding attempt failed: {ex2.Message}");
                }
            }
            return zeroIvResult;
        }
    }

    private string EncryptAesToBase64(string jsonData, out int cipherLength)
    {
        byte[] keyBytes = Encoding.UTF8.GetBytes(_aesKey);
        byte[] ivBytes = Encoding.UTF8.GetBytes(_aesIv);

        using Aes aes = Aes.Create();
        aes.Key = keyBytes;
        aes.IV = ivBytes;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var encryptor = aes.CreateEncryptor();
        byte[] plainBytes = Encoding.UTF8.GetBytes(jsonData);
        byte[] cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
        string base64 = Convert.ToBase64String(cipherBytes);
        cipherLength = base64.Length;
        return base64;
    }

    private string BuildSignature(int encryptedPayloadLength, bool includeAesKey)
    {
        long seqValue = _seq + encryptedPayloadLength;
        string signRaw;

        if (includeAesKey)
        {
            string escapedKey = Uri.EscapeDataString(_aesKey);
            string escapedIv = Uri.EscapeDataString(_aesIv);
            signRaw = $"key={escapedKey}&iv={escapedIv}&h={_hash}&s={seqValue}";
        }
        else
        {
            signRaw = $"h={_hash}&s={seqValue}";
        }

        return EncryptRSA(signRaw);
    }

    public async Task<bool> Login(string password)
    {
        // RSA хеш: MD5("admin" + password)
        _hash = CalculateMD5("admin" + password);

        // AES digest: MD5(password + ":" + nonce)
        string digest = CalculateMD5($"{password}:{_nonce}");

        var loginData = new
        {
            module = "authenticator",
            action = 1,
            digest = digest
        };

        string resultJson = await Request("authenticator", loginData, "cgi-bin/auth_cgi", isLogin: true);
        // Console.WriteLine($"[DEBUG][LOGIN] Login response: {resultJson}");

        // DEBUG: Check cookies after login
        var baseAddress = _http.BaseAddress ?? new Uri("http://192.168.0.1/");
        var cookies = _cookies.GetCookies(baseAddress);
        // Console.WriteLine($"[DEBUG][LOGIN] Cookies after login: Count={cookies.Count}");
        foreach (System.Net.Cookie c in cookies)
        {
            // Console.WriteLine($"  - {c.Name} = {c.Value}");
        }

        try
        {
            using var doc = JsonDocument.Parse(resultJson);
            int? resultInt = null;
            if (doc.RootElement.TryGetProperty("token", out var tokenValue))
            {
                _token = tokenValue.GetString();
            }
            if (doc.RootElement.TryGetProperty("result", out var resultValue) && resultValue.ValueKind == JsonValueKind.Number)
            {
                resultInt = resultValue.GetInt32();
            }

            // В login.min.js прошивки: success = 0, pwdWrong = 1. Остальное — неизвестная ошибка.
            bool success = resultInt == 0;
            if (success)
            {
                // Detect GDPR/encryptor mode before returning
                try
                {
                    await DetectGdprAsync();
                }
                catch (Exception ex)
                {
                    // Console.WriteLine($"[DEBUG][LOGIN] GDPR detection failed: {ex.Message}");
                }

                return true;
            }
        }
        catch (JsonException)
        {
            // Console.WriteLine($"[DEBUG][LOGIN] Ответ не является валидным JSON, пытаемся парсить как строку...");
            // Fall back to string parsing if the response is not valid JSON.
        }

        return false;
    }

    private async Task DetectGdprAsync()
    {
        // Console.WriteLine("[DEBUG][DETECT] Checking web server for supportGDPR...");
        var payload = new { module = "webServer", action = 0 };
        string jsonPayload = JsonSerializer.Serialize(payload);
        string base64Payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(jsonPayload));
        string requestBody = $"{{\"data\":\"{base64Payload}\"}}";

        var response = await _http.PostAsync("cgi-bin/web_cgi", new StringContent(requestBody, Encoding.UTF8, "application/x-www-form-urlencoded"));
        string body = await response.Content.ReadAsStringAsync();
        // Console.WriteLine($"[DEBUG][DETECT] Raw web_cgi response: {body}");

        try
        {
            // Use existing DecryptAES which will return JSON if response is plain/base64
            string decoded = DecryptAES(body);
            using var doc = JsonDocument.Parse(decoded);
            if (doc.RootElement.TryGetProperty("others", out var others) && others.ValueKind == JsonValueKind.Object)
            {
                if (others.TryGetProperty("supportGDPR", out var gdpr))
                {
                    _useGdpr = gdpr.GetBoolean();
                }
            }
            // Console.WriteLine($"[DEBUG][DETECT] supportGDPR={_useGdpr}");
        }
        catch (Exception ex)
        {
            // Console.WriteLine($"[DEBUG][DETECT] Could not parse web_cgi features: {ex.Message}");
        }
    }

    private string EncryptRSA(string text)
    {
        byte[] modBytes = _rsaMod.ToByteArray(isUnsigned: true, isBigEndian: true);
        byte[] expBytes = new byte[] { 1, 0, 1 };
        BigInteger exponent = new BigInteger(expBytes, isUnsigned: true, isBigEndian: true);

        int keySizeBytes = modBytes.Length;
        if (keySizeBytes <= 0)
        {
            throw new InvalidOperationException($"Unsupported RSA modulus size: {keySizeBytes * 8} bits");
        }

        int maxPlaintextPerBlock = keySizeBytes - 11;
        byte[] dataToEncrypt = Encoding.UTF8.GetBytes(text);

        using RandomNumberGenerator rng = RandomNumberGenerator.Create();
        List<byte> totalEncrypted = new List<byte>(keySizeBytes * ((dataToEncrypt.Length + maxPlaintextPerBlock - 1) / maxPlaintextPerBlock));

        for (int offset = 0; offset < dataToEncrypt.Length; offset += maxPlaintextPerBlock)
        {
            int chunkSize = Math.Min(maxPlaintextPerBlock, dataToEncrypt.Length - offset);
            byte[] chunk = new byte[chunkSize];
            Array.Copy(dataToEncrypt, offset, chunk, 0, chunkSize);

            byte[] padded = new byte[keySizeBytes];
            padded[0] = 0x00;
            padded[1] = 0x02;

            int paddingLength = keySizeBytes - chunkSize - 3;
            byte[] padding = new byte[paddingLength];
            rng.GetBytes(padding);
            for (int i = 0; i < padding.Length; i++)
            {
                while (padding[i] == 0)
                {
                    rng.GetBytes(padding, i, 1);
                }
                padded[2 + i] = padding[i];
            }

            padded[2 + paddingLength] = 0x00;
            Array.Copy(chunk, 0, padded, 3 + paddingLength, chunkSize);

            BigInteger plaintext = new BigInteger(padded, isUnsigned: true, isBigEndian: true);
            BigInteger cipher = BigInteger.ModPow(plaintext, exponent, _rsaMod);
            byte[] encryptedBlock = cipher.ToByteArray(isUnsigned: true, isBigEndian: true);

            if (encryptedBlock.Length < keySizeBytes)
            {
                byte[] prefixed = new byte[keySizeBytes];
                Array.Copy(encryptedBlock, 0, prefixed, keySizeBytes - encryptedBlock.Length, encryptedBlock.Length);
                encryptedBlock = prefixed;
            }

            totalEncrypted.AddRange(encryptedBlock);
        }

        return BitConverter.ToString(totalEncrypted.ToArray()).Replace("-", "").ToLower();
    }

    private string? GetToken()
    {
        var baseAddress = _http.BaseAddress ?? new Uri("http://192.168.0.1/");
        var cookie = _cookies.GetCookies(baseAddress)["tpweb_token"];
        return cookie?.Value;
    }

    private static object? JsonElementToObject(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var longValue) ? longValue : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(prop => prop.Name, prop => JsonElementToObject(prop.Value)),
            JsonValueKind.Array => element.EnumerateArray().Select(JsonElementToObject).ToList(),
            _ => element.GetRawText(),
        };
    }

    private static int GetActionValue(object? value)
    {
        return value switch
        {
            int i => i,
            long l => (int)l,
            double d => (int)d,
            string s when int.TryParse(s, out var result) => result,
            JsonElement e when e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var result) => result,
            _ => 0,
        };
    }

    private async Task<string> Request(string module, object data, string endpoint, bool isLogin = false)
    {
        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var serialised = JsonSerializer.SerializeToElement(data);
        if (serialised.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in serialised.EnumerateObject())
            {
                payload[prop.Name] = JsonElementToObject(prop.Value);
            }
        }

        payload["module"] = module;
        if (!payload.ContainsKey("action"))
        {
            payload["action"] = 0;
        }

        if (!payload.ContainsKey("token") && !string.IsNullOrEmpty(_token) && !string.Equals(module, "authenticator", StringComparison.OrdinalIgnoreCase))
        {
            payload["token"] = _token;
        }

        if (isLogin)
        {
            string jsonData = JsonSerializer.Serialize(payload);
            string dataB64 = EncryptAesToBase64(jsonData, out int cipherLength);
            string signature = BuildSignature(cipherLength, includeAesKey: true);
            var requestObj = new { data = dataB64, sign = signature };
            string contentStr = JsonSerializer.Serialize(requestObj);
            var content = new StringContent(contentStr, Encoding.UTF8, "application/x-www-form-urlencoded");

            // Console.WriteLine($"[DEBUG][REQUEST] Login request body: {contentStr}");
            LogCookies("before login POST");
            var response = await _http.PostAsync(endpoint, content);
            string responseBody = await response.Content.ReadAsStringAsync();
            // Console.WriteLine($"[DEBUG][REQUEST] Login response: {responseBody}");
            LogCookies("after login POST");
            return DecryptAES(responseBody);
        }
        else
        {
            bool forceEncrypted = string.Equals(module, "message", StringComparison.OrdinalIgnoreCase);
            if (forceEncrypted && !_useGdpr)
            {
                // Console.WriteLine($"[DEBUG][REQUEST] Forcing encrypted request for module={module} despite GDPR detection.");
            }

            // If server does not use GDPR/encryptor mode, many non-login requests are sent as plain Base64 JSON
            if (!_useGdpr && !forceEncrypted)
            {
                string plainJson_nonGdpr = JsonSerializer.Serialize(payload);
                string dataB64_nonGdpr = Convert.ToBase64String(Encoding.UTF8.GetBytes(plainJson_nonGdpr));
                var requestObj = new { data = dataB64_nonGdpr };
                string contentStr_plain = JsonSerializer.Serialize(requestObj);
                var content = new StringContent(contentStr_plain, Encoding.UTF8, "application/x-www-form-urlencoded");

                // Console.WriteLine($"[DEBUG][REQUEST] Sending plain Base64 request for module={module}, body: {contentStr_plain}");
                LogCookies($"before POST module={module}");
                var response = await _http.PostAsync(endpoint, content);
                string responseBody = await response.Content.ReadAsStringAsync();
                // Console.WriteLine($"[DEBUG][REQUEST] Response (first 200 chars): {responseBody.Substring(0, Math.Min(200, responseBody.Length))}");
                LogCookies($"after POST module={module}");
                return DecryptAES(responseBody);
            }

            // GDPR/encryptor mode or forced encryption: AES encrypt and sign as before
            string plainJson_gdpr = JsonSerializer.Serialize(payload);
            string dataB64_gdpr = EncryptAesToBase64(plainJson_gdpr, out int cipherLength);
            string signature = BuildSignature(cipherLength, includeAesKey: false);
            // Console.WriteLine($"[DEBUG][REQUEST] Non-login request plaintext len={plainJson_gdpr.Length}, dataB64 len={dataB64_gdpr.Length}, signature len={signature.Length}");
            // Console.WriteLine($"[DEBUG][REQUEST] Non-login signature prefix={signature.Substring(0, Math.Min(32, signature.Length))}");
            var requestObj2 = new { data = dataB64_gdpr, sign = signature };
            string contentStr_gdpr = JsonSerializer.Serialize(requestObj2);
            var content2 = new StringContent(contentStr_gdpr, Encoding.UTF8, "application/x-www-form-urlencoded");

            // Console.WriteLine($"[DEBUG][REQUEST] Encrypted request body for module={module}: {contentStr_gdpr}");
            LogCookies($"before POST module={module} (gdpr)");
            var response2 = await _http.PostAsync(endpoint, content2);
            string responseBody2 = await response2.Content.ReadAsStringAsync();
            // Console.WriteLine($"[DEBUG][REQUEST] Response (first 200 chars): {responseBody2.Substring(0, Math.Min(200, responseBody2.Length))}");
            LogCookies($"after POST module={module} (gdpr)");
            return DecryptAES(responseBody2);
        }
    }

}
