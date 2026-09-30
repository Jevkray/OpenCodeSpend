using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace OpenCodeSpend.Data;

public sealed record SiteLimits(
    string? Email, string? Plan, string? Region, bool UseBalance, decimal Balance, decimal MonthlySpendLimit,
    decimal RollingUsage, decimal RollingLimit, decimal RollingPct, int RollingResetSec,
    decimal WeeklyUsage, decimal WeeklyLimit, decimal WeeklyPct, int WeeklyResetSec,
    decimal MonthlyUsage, decimal MonthlyLimit, decimal MonthlyPct, int MonthlyResetSec,
    DateTimeOffset FetchedAt);

public sealed record SitePayment(string Id, DateTimeOffset? PaidAt, decimal Amount, bool Refunded, string? ReceiptUrl);

public sealed record SiteUsage(
    string Id, DateTimeOffset TimeCreated, string? Model, string? Provider, string? Plan,
    long InputTokens, long OutputTokens, long ReasoningTokens, long CacheRead,
    decimal Cost, string? SessionId, string? KeyId);

/// <summary>Тянет данные профиля через JSON API консоли opencode.ai по сессионной cookie.</summary>
public sealed class ProfileClient(SpendStore state, ILogger<ProfileClient> log)
{
    private const string Api = "https://opencode.ai/console/api";
    private const decimal Unit = 100_000_000m; // microCents → USD (1 USD = 100M)
    private string? _org; // найденный org id держим в памяти, чтобы не читать БД каждый тик

    /// <summary>GET JSON из консольного API. null — если ответ не 200.</summary>
    private async Task<string?> GetJsonAsync(string path, string cookie, string? org, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(40) };
        using var req = new HttpRequestMessage(HttpMethod.Get, Api + path);
        req.Headers.TryAddWithoutValidation("Cookie", cookie);
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        req.Headers.TryAddWithoutValidation("Referer", "https://opencode.ai/console/");
        req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        if (!string.IsNullOrWhiteSpace(org)) req.Headers.TryAddWithoutValidation("x-org-id", org);
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadAsStringAsync(ct);
    }

    /// <summary>org id: пользовательская настройка важнее всего, затем кэш на диске, затем первый org из /orgs.</summary>
    private async Task<string?> ResolveOrgAsync(string cookie, string workspace, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(workspace)) return workspace.Trim();
        if (!string.IsNullOrWhiteSpace(_org)) return _org;

        var cached = await state.GetStateAsync("zen_workspace", null, ct);
        if (!string.IsNullOrWhiteSpace(cached)) { _org = cached; return cached; }

        var json = await GetJsonAsync("/orgs", cookie, null, ct);
        if (json is null)
        {
            log.LogWarning("не удалось определить org id opencode: запрос /orgs не удался (сессия?)");
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(json);
            var id = doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0
                ? Str(doc.RootElement[0], "id")
                : null;
            if (string.IsNullOrWhiteSpace(id))
            {
                log.LogWarning("не удалось определить org id opencode: /orgs вернул пустой список");
                return null;
            }
            _org = id;
            await state.SetStateAsync("zen_workspace", id, null, ct); // кэш на диск — переживает перезапуск
            return id;
        }
        catch (Exception e)
        {
            log.LogWarning(e, "не удалось разобрать ответ /orgs");
            return null;
        }
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Число, которое может прийти и строкой, и числом.</summary>
    private static long Lng(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt64(out var n) ? n : (long)v.GetDouble(),
            JsonValueKind.String => long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 0,
            _ => 0,
        };
    }

    private static decimal Dec(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return 0m;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDecimal(),
            JsonValueKind.String => decimal.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var s) ? s : 0m,
            _ => 0m,
        };
    }

    private static string? IdStr(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetInt64().ToString(CultureInfo.InvariantCulture),
            _ => null,
        };
    }

    public async Task<(SiteLimits? profile, string? error)> FetchLimitsAsync(string cookie, string workspace, CancellationToken ct = default)
    {
        var org = await ResolveOrgAsync(cookie, workspace, ct);
        var json = await GetJsonAsync("/go/status", cookie, org, ct);
        if (json is null) return (null, "консоль: не удалось получить лимиты (сессия?)");

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var meters = root.GetProperty("access").GetProperty("meters");
            var now = DateTimeOffset.UtcNow;

            (decimal used, decimal limit, decimal pct, int reset) Meter(string name)
            {
                if (!meters.TryGetProperty(name, out var m)) return (0m, 0m, 0m, 0);
                var limit = Dec(m, "limitMicroCents") / Unit;
                var used = Dec(m, "usedMicroCents") / Unit;
                var pct = limit > 0 ? used / limit * 100 : 0m;
                var reset = 0;
                var resets = Str(m, "resetsAt");
                if (resets is not null && DateTimeOffset.TryParse(resets, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var dt))
                    reset = Math.Max(0, (int)(dt - now).TotalSeconds);
                return (used, limit, pct, reset);
            }

            var five = Meter("fiveHour");
            var week = Meter("week");
            var month = Meter("month");
            var useBalance = root.TryGetProperty("useBalance", out var ub) && ub.ValueKind == JsonValueKind.True;

            string? email = null;
            var members = await GetJsonAsync("/members", cookie, org, ct);
            if (members is not null)
            {
                try
                {
                    using var md = JsonDocument.Parse(members);
                    if (md.RootElement.TryGetProperty("items", out var items)
                        && items.ValueKind == JsonValueKind.Array && items.GetArrayLength() > 0)
                        email = Str(items[0], "email");
                }
                catch { }
            }

            var p = new SiteLimits(
                email, "", "", useBalance, 0m, month.limit,
                five.used, five.limit, five.pct, five.reset,
                week.used, week.limit, week.pct, week.reset,
                month.used, month.limit, month.pct, month.reset,
                DateTimeOffset.Now);
            return (p, null);
        }
        catch { return (null, "консоль: не удалось разобрать лимиты"); }
    }

    /// <summary>Дневная сводка трат из консоли: одна запись на календарный день.</summary>
    private async Task<List<SiteUsage>> FetchUsageDaysAsync(string cookie, string? org, CancellationToken ct)
    {
        var json = await GetJsonAsync("/usage/cost-by-day?range=30d", cookie, org, ct);
        if (json is null) return new();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return new();
            var rows = new List<SiteUsage>();
            foreach (var it in doc.RootElement.EnumerateArray())
            {
                var date = Str(it, "date");
                if (date is null || !DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d))
                    continue;
                rows.Add(new SiteUsage(
                    "day:" + date, d, null, null, null,
                    Lng(it, "totalTokens"), 0, 0, 0,
                    Dec(it, "totalCostMicroCents") / Unit,
                    null, null));
            }
            return rows;
        }
        catch { return new(); }
    }

    /// <summary>Дневная сводка трат (консольный API отдаёт агрегаты по дням, не отдельные записи).</summary>
    public async Task<List<SiteUsage>> FetchUsagePageAsync(string cookie, string workspace, int page, CancellationToken ct = default)
    {
        var org = await ResolveOrgAsync(cookie, workspace, ct);
        return await FetchUsageDaysAsync(cookie, org, ct);
    }

    /// <summary>Совместимость: дневная сводка приходит одним запросом, страницы не нужны.</summary>
    public Task<List<SiteUsage>> FetchUsageAllAsync(string cookie, string workspace, int maxPages, CancellationToken ct = default)
        => FetchUsagePageAsync(cookie, workspace, 1, ct);

    public async Task<(List<SiteUsage> rows, string? error)> FetchUsageAsync(string cookie, string workspace, CancellationToken ct = default)
        => (await FetchUsagePageAsync(cookie, workspace, 1, ct), null);

    /// <summary>Разбивка трат по моделям из консоли: готовый агрегат за период.</summary>
    public async Task<List<SiteModelRow>> FetchModelsAsync(string cookie, string workspace, CancellationToken ct = default)
    {
        var org = await ResolveOrgAsync(cookie, workspace, ct);
        var json = await GetJsonAsync("/usage/models?range=30d", cookie, org, ct);
        if (json is null) return new();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                return new();
            var list = new List<SiteModelRow>();
            foreach (var it in items.EnumerateArray())
                list.Add(new SiteModelRow(
                    Str(it, "provider") ?? "?",
                    Str(it, "model") ?? "?",
                    Dec(it, "totalCostMicroCents") / Unit,
                    Lng(it, "totalRequests")));
            return list.OrderByDescending(m => m.Cost).ToList();
        }
        catch { return new(); }
    }

    /// <summary>Платежи и подписка из консольного API.</summary>
    public async Task<(List<SitePayment> payments, string? liteSubId, string? error)> FetchBillingAsync(
        string cookie, string workspace, CancellationToken ct = default)
    {
        var org = await ResolveOrgAsync(cookie, workspace, ct);
        var json = await GetJsonAsync("/billing/invoices", cookie, org, ct);
        if (json is null) return (new(), null, "консоль: не удалось получить платежи (сессия?)");

        var list = new List<SitePayment>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                foreach (var it in doc.RootElement.EnumerateArray())
                {
                    var id = IdStr(it, "id");
                    if (string.IsNullOrEmpty(id)) continue;
                    var paid = Str(it, "paidAt");
                    var status = Str(it, "status");
                    list.Add(new SitePayment(
                        id,
                        DateTimeOffset.TryParse(paid, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var d) ? d : null,
                        Dec(it, "amountPaidMicroCents") / Unit,
                        status is "voided" or "refunded",
                        Str(it, "hostedInvoiceUrl")));
                }
        }
        catch { }

        string? sub = null;
        var statusJson = await GetJsonAsync("/go/status", cookie, org, ct);
        if (statusJson is not null)
        {
            try { using var sd = JsonDocument.Parse(statusJson); sub = Str(sd.RootElement, "subscriberUserId"); }
            catch { }
        }
        return (list, sub, null);
    }
}

/// <summary>Фоновое обновление профиля opencode (лимиты, траты, платежи) — раз в N секунд, без участия браузера.
/// На сервере у каждого пользователя свой cookie — обходим всех.</summary>
public sealed class ProfileSyncService(ProfileClient client, ProfileStore store, SpendStore spend, SpendConfig config, ILogger<ProfileSyncService> log)
    : BackgroundService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, DateTimeOffset> _lastFullCrawl = new();
    private readonly Dictionary<string, DateTimeOffset> _lastUsage = new();
    public DateTimeOffset? LastSync { get; private set; }
    public string? LastError { get; private set; }

    public async Task<bool> SyncAsync(CancellationToken ct = default)
    {
        var any = false;
        if (config.IsServer)
        {
            var cookies = await spend.AllStateAsync("profile_cookie", ct);
            if (cookies.Count == 0) { LastError = "нет подключённых профилей opencode"; return false; }
            foreach (var (uid, cookie) in cookies) any |= await SyncOneAsync(uid, cookie, ct);
        }
        else
        {
            var cookie = config.ProfileCookie ?? await spend.GetStateAsync("profile_cookie", null, ct);
            if (string.IsNullOrWhiteSpace(cookie)) { LastError = "нет cookie профиля"; return false; }
            any = await SyncOneAsync(null, cookie, ct);
        }
        if (any) LastSync = DateTimeOffset.UtcNow;
        return any;
    }

    private async Task<bool> SyncOneAsync(string? uid, string cookie, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var key = uid ?? "local";
            var now = DateTimeOffset.UtcNow;

            // моментальный баланс и лимиты — каждый тик (раз в секунду)
            var (limits, e1) = await client.FetchLimitsAsync(cookie, config.ZenWorkspace, ct);
            if (limits is not null) await store.SaveProfileAsync(limits, uid, ct);

            // свежая страница истории — реже: сайт opencode не долбим зря
            var usageEvery = TimeSpan.FromSeconds(Math.Max(5, config.ProfileSyncSeconds * 5));
            if (!_lastUsage.TryGetValue(key, out var lastUsage) || now - lastUsage > usageEvery)
            {
                _lastUsage[key] = now;
                var rows = await client.FetchUsagePageAsync(cookie, config.ZenWorkspace, 1, ct);
                if (rows.Count > 0) await store.ReplaceUsageAsync(rows, uid, ct);
                var models = await client.FetchModelsAsync(cookie, config.ZenWorkspace, ct);
                if (models.Count > 0) await store.ReplaceModelsAsync(models, uid, ct);
            }

            // полная история и платежи — раз в FullCrawlMinutes
            var e3 = (string?)null;
            var needFull = !_lastFullCrawl.TryGetValue(key, out var last)
                           || now - last > TimeSpan.FromMinutes(config.FullCrawlMinutes);
            if (needFull)
            {
                _lastFullCrawl[key] = now;
                var all = await client.FetchUsageAllAsync(cookie, config.ZenWorkspace, config.MaxHistoryPages, ct);
                if (all.Count > 0) await store.ReplaceUsageAsync(all, uid, ct);
                var (payments, sub, billingErr) = await client.FetchBillingAsync(cookie, config.ZenWorkspace, ct);
                if (payments.Count > 0 || sub is not null) await store.SavePaymentsAsync(payments, sub, uid, ct);
                e3 = billingErr;
            }
            LastError = e1 ?? e3;
            // причину неудачи показываем в панели, чтобы было понятно, что не так с cookie
            await spend.SetStateAsync("profile_error", LastError ?? "", uid, ct);
            return limits is not null;
        }
        catch (Exception e) { LastError = e.Message; log.LogWarning(e, "profile sync failed"); return false; }
        finally { _gate.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // сервер в opencode не ходит вообще: готовые данные ему присылает приложение
        if (config.IsServer) return;
        try { await Task.Delay(1500, ct); } catch { return; }
        while (!ct.IsCancellationRequested)
        {
            await SyncAsync(ct);
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, config.ProfileSyncSeconds)), ct); }
            catch { break; }
        }
    }
}

