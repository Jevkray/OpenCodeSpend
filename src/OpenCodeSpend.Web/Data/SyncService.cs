namespace OpenCodeSpend.Data;

/// <summary>Фоновая синхронизация opencode.db -> PostgreSQL. Идемпотентно, по watermark.</summary>
public sealed class SyncService(SpendConfig config, OpencodeReader reader, SpendStore store, ILogger<SyncService> log)
    : BackgroundService
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DateTimeOffset? LastSync { get; private set; }
    public string? LastError { get; private set; }
    public long UsageRows { get; private set; }

    public async Task<(int usage, int sessions)> SyncAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!reader.Available)
            {
                LastError = $"нет файла {config.OpencodeDbPath}";
                return (0, 0);
            }

            var wmText = await store.GetStateAsync("usage_wm", null, ct);
            var usageSince = long.TryParse(wmText, out var wm) ? wm : 0;
            var usage = reader.ReadUsage(usageSince);

            var seeded = await store.GetStateAsync("sessions_seeded", null, ct) == "1";
            var sessSince = seeded ? DateTimeOffset.UtcNow.AddHours(-3).ToUnixTimeMilliseconds()
                                   : DateTimeOffset.UnixEpoch.ToUnixTimeMilliseconds();
            var sessions = reader.ReadSessions(sessSince);

            var n1 = await store.UpsertUsageAsync(usage, null, ct);
            var n2 = await store.UpsertSessionsAsync(sessions, null, ct);

            if (usage.Count > 0)
                await store.SetStateAsync("usage_wm", usage.Max(u => u.Ts.ToUnixTimeMilliseconds()).ToString(), null, ct);
            if (!seeded) await store.SetStateAsync("sessions_seeded", "1", null, ct);

            UsageRows += n1;
            LastSync = DateTimeOffset.UtcNow;
            LastError = null;
            return (n1, n2);
        }
        catch (Exception e)
        {
            LastError = e.Message;
            log.LogWarning(e, "sync failed");
            return (0, 0);
        }
        finally
        {
            _gate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // на сервере локальной opencode.db нет, а сборщик ничего не хранит
        if (config.IsServer || config.SkipLocalDb)
        {
            log.LogInformation("Режим {mode}: локальный синк opencode.db отключён", config.IsServer ? "сервера" : "сборщика");
            return;
        }
        try { await Task.Delay(800, ct); } catch { return; }
        while (!ct.IsCancellationRequested)
        {
            await SyncAsync(ct);
            try { await Task.Delay(TimeSpan.FromSeconds(config.SyncIntervalSeconds), ct); }
            catch { break; }
        }
    }
}
