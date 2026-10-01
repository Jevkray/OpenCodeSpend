using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenCodeSpend.Desktop;

/// <summary>
/// Пока приложение запущено — одним запросом отправляет локальные данные на сервер
/// и забирает оттуда команды «стоп».
/// Настройки: Spend:ServerUrl, Spend:DeviceToken (appsettings.json).
/// </summary>
public sealed class Pusher
{
    private readonly string _serverUrl;
    private readonly string _deviceToken;
    private readonly string _localUrl;
    private readonly string? _deviceId;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly string _statePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "opencode-spend", "push-state.json");
    private readonly string _devicesPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenCodeSpend", "server-devices.json");

    /// <summary>Файлы состояния читаются и пишутся из нескольких вызовов — сериализуем доступ.</summary>
    private static readonly object FileLock = new();

    /// <summary>Сервер отверг токен — этот ПК отключён от аккаунта.</summary>
    public bool Revoked { get; private set; }

    public string? LastError { get; private set; }
    public int PushedUsage { get; private set; }
    public int PushedSite { get; private set; }

    /// <summary>Профиль тяжёлый — отправляем его не чаще раза в минуту (отдельный счётчик времени).</summary>
    private static readonly TimeSpan ProfileInterval = TimeSpan.FromSeconds(60);

    /// <summary>Защита от наложения: пока предыдущая отправка не завершилась, новую не начинаем.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Хеш последнего успешно отправленного тела: совпал — во внешнюю сеть не идём.</summary>
    private string? _lastSentHash;

    private DateTimeOffset _lastProfileAt = DateTimeOffset.MinValue;

    /// <summary>Последний прочитанный профиль: обновляем раз в минуту, кладём во все отправки.
    /// Так тело не «мигает» профилем и хеш стабилен, когда ничего не меняется.</summary>
    private JsonNode? _profile;

    public Pusher(string serverUrl, string deviceToken, string localUrl, string? deviceId = null)
    {
        _serverUrl = serverUrl.TrimEnd('/');
        _deviceToken = deviceToken;
        _localUrl = localUrl.TrimEnd('/');
        _deviceId = deviceId;
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
    }

    private (long usage, long site, string cookie) ReadState()
    {
        try
        {
            lock (FileLock)
            {
                if (!File.Exists(_statePath)) return (0, 0, "");
                using var doc = JsonDocument.Parse(File.ReadAllText(_statePath));
                return (doc.RootElement.GetProperty("usageMs").GetInt64(),
                        doc.RootElement.GetProperty("siteMs").GetInt64(),
                        doc.RootElement.TryGetProperty("cookie", out var c) ? c.GetString() ?? "" : "");
            }
        }
        catch { return (0, 0, ""); }
    }

    private void WriteState(long usageMs, long siteMs, string cookie)
    {
        lock (FileLock)
            File.WriteAllText(_statePath, JsonSerializer.Serialize(new { usageMs, siteMs, cookie }));
    }

    /// <summary>Один вызов: локальные данные + снимок сессий → сервер, команды «стоп» ← сервер.
    /// Если тело не изменилось с прошлой удачной отправки — во внешнюю сеть не обращаемся.</summary>
    public async Task SyncAsync()
    {
        if (Revoked || string.IsNullOrWhiteSpace(_serverUrl)) return;
        // таймер и событийный путь могут совпасть — наложенный вызов пропускаем
        if (!await _gate.WaitAsync(0)) return;
        try
        {
            var (usageMs, siteMs, lastCookie) = ReadState();

            var usageJson = await _http.GetStringAsync($"{_localUrl}/api/export/usage?sinceMs={usageMs}");
            var usageNode = JsonNode.Parse(usageJson);
            var usage = usageNode?["usage"]?.AsArray() ?? new JsonArray();
            var sessions = usageNode?["sessions"]?.AsArray() ?? new JsonArray();

            var ctlJson = await _http.GetStringAsync($"{_localUrl}/api/control/sessions");
            var control = JsonNode.Parse(ctlJson)?["tree"]?.AsArray() ?? new JsonArray();

            var siteJson = await _http.GetStringAsync($"{_localUrl}/api/export/site-usage?sinceMs={siteMs}");
            var site = JsonNode.Parse(siteJson)?.AsArray() ?? new JsonArray();

            if (DateTimeOffset.UtcNow - _lastProfileAt >= ProfileInterval)
            {
                _lastProfileAt = DateTimeOffset.UtcNow;
                _profile = JsonNode.Parse(await _http.GetStringAsync($"{_localUrl}/api/export/profile"));
            }

            var body = new JsonObject
            {
                ["device"] = Environment.MachineName,
                ["usage"] = usage.DeepClone(),
                ["sessions"] = sessions.DeepClone(),
                ["site"] = site.DeepClone(),
                ["control"] = new JsonObject
                {
                    ["device"] = Environment.MachineName,
                    ["sessions"] = control.DeepClone(),
                },
            };
            if (_profile is not null) body["profile"] = _profile.DeepClone();

            var json = body.ToJsonString();
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
            if (hash == _lastSentHash)
            {
                // ничего не изменилось — во внешнюю сеть не идём, watermarks уже зафиксированы прошлой отправкой
                PushedUsage = usage.Count;
                PushedSite = site.Count;
                LastError = null;
                return;
            }

            using var req = new HttpRequestMessage(HttpMethod.Post, _serverUrl + "/api/device/sync")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            req.Headers.TryAddWithoutValidation("X-Device-Token", _deviceToken);
            using var resp = await _http.SendAsync(req);
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                Revoked = true;
                return;
            }
            var text = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                LastError = $"{(int)resp.StatusCode}: {text[..Math.Min(200, text.Length)]}";
                return;
            }
            _lastSentHash = hash;   // тело принято — при повторе того же не шлём

            // watermarks — по максимальному времени отправленного (только после успеха, чтобы не потерять данные)
            if (usage.Count > 0)
                usageMs = usage.Select(u => u!["ts"]!.GetValue<DateTimeOffset>().ToUnixTimeMilliseconds()).Max();
            if (site.Count > 0)
                siteMs = site.Select(s => s!["time"]!.GetValue<DateTimeOffset>().ToUnixTimeMilliseconds()).Max();
            WriteState(usageMs, siteMs, lastCookie);

            // список компьютеров аккаунта — локальная панель показывает его пользователю
            try
            {
                using var devDoc = JsonDocument.Parse(text);
                if (devDoc.RootElement.TryGetProperty("devices", out var devs))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_devicesPath)!);
                    lock (FileLock)
                        File.WriteAllText(_devicesPath,
                            JsonSerializer.Serialize(new { self = _deviceId, devices = devs.Clone() }));
                }
            }
            catch { }

            // команды «стоп»: выполняем локально
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("commands", out var commands))
                    foreach (var c in commands.EnumerateArray())
                    {
                        if (!c.TryGetProperty("type", out var t) || t.GetString() != "stop") continue;
                        var id = c.TryGetProperty("id", out var i) ? i.GetString() : null;
                        if (string.IsNullOrEmpty(id)) continue;
                        using var stop = new HttpRequestMessage(HttpMethod.Post,
                            $"{_localUrl}/api/control/sessions/{Uri.EscapeDataString(id)}/stop");
                        using var stopResp = await _http.SendAsync(stop);
                    }
            }
            catch { }

            PushedUsage = usage.Count;
            PushedSite = site.Count;
            LastError = null;
        }
        catch (Exception e)
        {
            LastError = e.Message;
        }
        finally
        {
            _gate.Release();
        }
    }
}
