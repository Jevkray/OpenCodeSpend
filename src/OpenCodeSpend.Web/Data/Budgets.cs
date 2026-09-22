using System.Text.Json;

namespace OpenCodeSpend.Data;

/// <summary>Бюджеты и лимиты: token-tracker.json, cost-guard.config.json и лимиты OpenCode Go.</summary>
public sealed class Budgets(SpendConfig config)
{
    private readonly SpendConfig _config = config;

    private (decimal daily, decimal weekly, decimal monthly, double warnAt) ReadTokenTracker()
    {
        var f = _config.BudgetFiles.FirstOrDefault(p => Path.GetFileName(p).Contains("token-tracker"));
        if (f is null || !File.Exists(f)) return (0, 0, 0, 0.8);
        using var doc = JsonDocument.Parse(File.ReadAllText(f));
        var b = doc.RootElement.GetProperty("budget");
        decimal Get(string n, decimal dflt) => b.TryGetProperty(n, out var v) ? v.GetDecimal() : dflt;
        var warn = b.TryGetProperty("warnAt", out var w) ? w.GetDouble() : 0.8;
        return (Get("daily", 0), Get("weekly", 0), Get("monthly", 0), warn);
    }

    private (decimal maxCost, double warnAt, string mode) ReadCostGuard()
    {
        var f = _config.BudgetFiles.FirstOrDefault(p => Path.GetFileName(p).Contains("cost-guard"));
        if (f is null || !File.Exists(f)) return (0, 0.6, "block");
        using var doc = JsonDocument.Parse(File.ReadAllText(f));
        var r = doc.RootElement;
        decimal max = r.TryGetProperty("maxCostUsd", out var m) ? m.GetDecimal() : 0;
        double warn = r.TryGetProperty("warnAtPercent", out var w) ? w.GetDouble() / 100.0 : 0.6;
        string mode = r.TryGetProperty("mode", out var md) ? md.GetString() ?? "block" : "block";
        return (max, warn, mode);
    }

    public async Task<List<BudgetLine>> BuildAsync(SpendStore store, TimeZoneInfo tz, string? uid = null, CancellationToken ct = default)
    {
        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz);
        var dayStart = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, tz.GetUtcOffset(now)).ToUniversalTime();
        var monday = now.Date.AddDays(-(((int)now.DayOfWeek + 6) % 7));
        var weekStart = new DateTimeOffset(monday, tz.GetUtcOffset(now)).ToUniversalTime();
        var monthStart = new DateTimeOffset(new DateTime(now.Year, now.Month, 1), tz.GetUtcOffset(now)).ToUniversalTime();
        var fiveHours = DateTimeOffset.UtcNow.AddHours(-5);

        var tt = ReadTokenTracker();
        var cg = ReadCostGuard();

        var day = await store.TotalsAsync(dayStart, DateTimeOffset.UtcNow.AddDays(1), uid, ct);
        var week = await store.TotalsAsync(weekStart, DateTimeOffset.UtcNow.AddDays(1), uid, ct);
        var month = await store.TotalsAsync(monthStart, DateTimeOffset.UtcNow.AddDays(1), uid, ct);
        var five = await store.TotalsAsync(fiveHours, DateTimeOffset.UtcNow.AddDays(1), uid, ct);
        var sessions = await store.SessionsAsync(monthStart, 500, uid, ct);
        var maxSession = sessions.Count > 0 ? sessions.Max(s => s.Cost) : 0m;

        var lines = new List<BudgetLine>
        {
            new("Локально: день", "day", tt.daily, day.Cost, tt.warnAt),
            new("Локально: неделя", "week", tt.weekly, week.Cost, tt.warnAt),
            new("Локально: месяц", "month", tt.monthly, month.Cost, tt.warnAt),
            new("OpenCode Go: 5 часов", "5h", 12m, five.Cost, 0.8),
            new("OpenCode Go: неделя", "week", 30m, week.Cost, 0.8),
            new("OpenCode Go: месяц", "month", 60m, month.Cost, 0.8),
            new("cost-guard: макс. сессия", "session", cg.maxCost, maxSession, cg.warnAt),
        };

        return lines.Where(l => l.Limit > 0).ToList();
    }
}
