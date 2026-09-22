namespace OpenCodeSpend.Data;

public sealed record UsageRecord(
    string Id,
    string SessionId,
    string? ParentSessionId,
    string? ProjectId,
    string? Agent,
    string? Mode,
    string? ProviderId,
    string? ModelId,
    string? Variant,
    DateTimeOffset Ts,
    decimal Cost,
    long InputTokens,
    long OutputTokens,
    long ReasoningTokens,
    long CacheRead,
    long CacheWrite)
{
    public long TotalTokens => InputTokens + OutputTokens + ReasoningTokens + CacheRead + CacheWrite;
}

public sealed record SessionRecord(
    string Id,
    string? ParentId,
    string? ProjectId,
    string? Agent,
    string? Title,
    string? ProviderId,
    string? ModelId,
    DateTimeOffset Created,
    DateTimeOffset Updated,
    decimal Cost,
    long InputTokens,
    long OutputTokens,
    long ReasoningTokens,
    long CacheRead);

public sealed record BudgetLine(string Name, string Period, decimal Limit, decimal Spent, double WarnAt)
{
    public double Ratio => Limit > 0 ? (double)(Spent / Limit) : 0;
}

public sealed class ControlSessionsDto
{
    public string? Device { get; set; }
    public List<object>? Sessions { get; set; }
}

public sealed class ControlAckDto
{
    public string? Id { get; set; }
    public bool Ok { get; set; }
}

public sealed class CookieDto
{
    public string? Cookie { get; set; }
}

public sealed class ClaimDto
{
    public string? Code { get; set; }
    public string? Name { get; set; }
    public string? MachineId { get; set; }
}

public sealed class RevokeDto
{
    public string? Id { get; set; }
}

/// <summary>Р“РѕС‚РѕРІС‹Рµ РґР°РЅРЅС‹Рµ РїСЂРѕС„РёР»СЏ opencode: РїСЂРёР»РѕР¶РµРЅРёРµ РїР°СЂСЃРёС‚ Р»РѕРєР°Р»СЊРЅРѕ Рё РѕС‚РґР°С‘С‚ СЃРµСЂРІРµСЂСѓ.</summary>
public sealed class ProfilePushDto
{
    public SiteLimits? Limits { get; set; }
    public List<SiteUsageDto>? Usage { get; set; }
    public List<SitePayment>? Payments { get; set; }
    public string? LiteSubId { get; set; }
}

public sealed class DeviceIngestDto
{
    public string? Device { get; set; }
    public List<UsageRecord>? Usage { get; set; }
    public List<SessionRecord>? Sessions { get; set; }
}

/// <summary>Единый пакет устройства: данные и снимок сессий одним запросом.</summary>
public sealed class DeviceSyncDto
{
    public string? Device { get; set; }
    public List<UsageRecord>? Usage { get; set; }
    public List<SessionRecord>? Sessions { get; set; }
    public List<SiteUsageDto>? Site { get; set; }
    public ProfilePushDto? Profile { get; set; }
    public ControlSessionsDto? Control { get; set; }
}

public sealed class SiteUsageDto
{
    public string? Id { get; set; }
    public DateTimeOffset? Time { get; set; }
    public string? Model { get; set; }
    public string? Provider { get; set; }
    public long Input { get; set; }
    public long Output { get; set; }
    public long Reasoning { get; set; }
    public long CacheRead { get; set; }
    public decimal Cost { get; set; }
    public string? SessionId { get; set; }
}

public sealed record User(string Id, string? Email, string? Name, string? Picture, string? Provider, DateTimeOffset Created);

public sealed record Device(string Id, string UserId, string? Name, DateTimeOffset Created, DateTimeOffset LastSeen);

public sealed class SettingsDto
{
    public string? ZenKey { get; set; }
    public string? ZenScope { get; set; }
    public string? ZenEmail { get; set; }
    public string? ProfileCookie { get; set; }
    public string? ZenWorkspace { get; set; }
}

public sealed class SpendConfig
{
    public string OpencodeDbPath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "share", "opencode", "opencode.db");

    /// <summary>Р¤Р°Р№Р» С…СЂР°РЅРёР»РёС‰Р° SQLite (РїРѕ СѓРјРѕР»С‡Р°РЅРёСЋ вЂ” РІ РїРѕР»СЊР·РѕРІР°С‚РµР»СЊСЃРєРёС… РґР°РЅРЅС‹С…).</summary>
    public string DatabasePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenCodeSpend", "opencodespend.db");

    public string TimeZone { get; set; } = "UTC";

    public int SyncIntervalSeconds { get; set; } = 10;

    /// <summary>РџРµСЂРёРѕРґ С„РѕРЅРѕРІРѕРіРѕ РѕР±РЅРѕРІР»РµРЅРёСЏ РїСЂРѕС„РёР»СЏ opencode, СЃРµРє.</summary>
    public int ProfileSyncSeconds { get; set; } = 10;

    /// <summary>РљР°Рє С‡Р°СЃС‚Рѕ РїРµСЂРµСЃРѕР±РёСЂР°С‚СЊ РІСЃСЋ РёСЃС‚РѕСЂРёСЋ С‚СЂР°С‚, РјРёРЅСѓС‚.</summary>
    public int FullCrawlMinutes { get; set; } = 10;

    /// <summary>РњР°РєСЃРёРјСѓРј СЃС‚СЂР°РЅРёС† РёСЃС‚РѕСЂРёРё Р·Р° РѕРґРёРЅ РїСЂРѕС…РѕРґ.</summary>
    public int MaxHistoryPages { get; set; } = 200;

    // --- СѓРїСЂР°РІР»РµРЅРёРµ Р»РѕРєР°Р»СЊРЅС‹Рј opencode (РєРЅРѕРїРєР° В«РЎС‚РѕРїВ») ---
    /// <summary>РђРґСЂРµСЃ СЃРµСЂРІРµСЂР° opencode (РїСѓСЃС‚Рѕ = РёСЃРєР°С‚СЊ Р°РІС‚РѕРјР°С‚РёС‡РµСЃРєРё РїРѕ СЃР»СѓС€Р°СЋС‰РёРј РїРѕСЂС‚Р°Рј).</summary>
    public string? OpencodeServerUrl { get; set; }

    /// <summary>РџР°СЂРѕР»СЊ СЃРµСЂРІРµСЂР° opencode (OPENCODE_SERVER_PASSWORD). Р‘РµР· РЅРµРіРѕ РѕСЃС‚Р°РЅРѕРІРєР° РЅРµРґРѕСЃС‚СѓРїРЅР°.</summary>
    public string? OpencodeServerPassword { get; set; }

    public string? OpencodeServerUsername { get; set; }

    public List<string> BudgetFiles { get; set; } = new()
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "opencode", "token-tracker.json"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "opencode", "cost-guard.config.json"),
    };

    public string? ZenServiceKey { get; set; }
    public string ZenConsoleUrl { get; set; } = "https://console.opencode.ai";
    public string ZenWorkspace { get; set; } = "wrk_01M23MJCERW1QBG80ZJ0VP1GK1";
    public string? ProfileCookie { get; set; }

    // --- Google OAuth (РІС…РѕРґ РІ РїСЂРёР»РѕР¶РµРЅРёРµ) ---
    public string? GoogleClientId { get; set; }
    public string? GoogleClientSecret { get; set; }
    public int SessionDays { get; set; } = 30;

    /// <summary>Р’РєР»СЋС‡Р°РµС‚СЃСЏ Р°РІС‚РѕРјР°С‚РёС‡РµСЃРєРё, РµСЃР»Рё Р·Р°РґР°РЅС‹ client id Рё secret.</summary>
    public bool GoogleEnabled => !string.IsNullOrWhiteSpace(GoogleClientId) && !string.IsNullOrWhiteSpace(GoogleClientSecret);

    // --- GitHub OAuth (РІС…РѕРґ РІ РїСЂРёР»РѕР¶РµРЅРёРµ) ---
    public string? GitHubClientId { get; set; }
    public string? GitHubClientSecret { get; set; }

    public bool GitHubEnabled => !string.IsNullOrWhiteSpace(GitHubClientId) && !string.IsNullOrWhiteSpace(GitHubClientSecret);

    /// <summary>Р•СЃС‚СЊ Р»Рё С…РѕС‚СЏ Р±С‹ РѕРґРёРЅ СЃРїРѕСЃРѕР± РІС…РѕРґР° С‡РµСЂРµР· РІРЅРµС€РЅРёР№ Р°РєРєР°СѓРЅС‚.</summary>
    public bool AnyLoginEnabled => GoogleEnabled || GitHubEnabled;

    // --- РјСѓР»СЊС‚РёСѓСЃС‚СЂРѕР№СЃС‚РІРѕ ---
    /// <summary>РўРѕРєРµРЅ СѓСЃС‚СЂРѕР№СЃС‚РІР° РґР»СЏ РїСЂРёС‘РјР° РґР°РЅРЅС‹С… РЅР° СЃРµСЂРІРµСЂРµ.</summary>
    public string? DeviceToken { get; set; }

    /// <summary>РђРґСЂРµСЃ СЃРµСЂРІРµСЂР° РґР»СЏ РѕС‚РїСЂР°РІРєРё РґР°РЅРЅС‹С… СЃ РґРµСЃРєС‚РѕРїР° (РїСѓСЃС‚Рѕ = РЅРµ РѕС‚РїСЂР°РІР»СЏС‚СЊ).</summary>
    public string? ServerUrl { get; set; }

    /// <summary>Сколько минут живёт код подключения компьютера.</summary>
    public int PairingTtlMinutes { get; set; } = 5;

    /// <summary>auto | server | collector. auto: СЃРµСЂРІРµСЂ, РµСЃР»Рё РЅРµС‚ Р»РѕРєР°Р»СЊРЅРѕР№ opencode.db.</summary>
    public string Mode { get; set; } = "auto";

    /// <summary>Р“РґРµ Р»РµР¶РёС‚ С„Р°Р№Р» РїСЂРёРІСЏР·РєРё Рє СЃРµСЂРІРµСЂСѓ (РµРґРёРЅСЃС‚РІРµРЅРЅРѕРµ Р»РѕРєР°Р»СЊРЅРѕРµ С…СЂР°РЅРёР»РёС‰Рµ).</summary>
    public string LinkPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenCodeSpend", "device.json");

    public bool HasLocalDb => File.Exists(OpencodeDbPath);

    /// <summary>Р РѕР»СЊ РїСЂРѕС†РµСЃСЃР°: Р°РіСЂРµРіР°С‚РѕСЂ-СЃРµСЂРІРµСЂ РёР»Рё Р»РѕРєР°Р»СЊРЅС‹Р№ СЃР±РѕСЂС‰РёРє.</summary>
    public bool IsServer => Mode.Equals("server", StringComparison.OrdinalIgnoreCase)
                            || (Mode.Equals("auto", StringComparison.OrdinalIgnoreCase) && !HasLocalDb);

    /// <summary>Р›РѕРєР°Р»СЊРЅР°СЏ СЂРѕР»СЊ: РЅРµ СЃРµСЂРІРµСЂ (СЃСЋРґР° РїРѕРїР°РґР°РµС‚ Рё РїСЂРёР»РѕР¶РµРЅРёРµ СЃ opencode.db).</summary>
    public bool IsCollector => !IsServer;

    /// <summary>РЈСЂРµР·Р°РЅРЅС‹Р№ СЂРµР¶РёРј Р±РµР· Р»РѕРєР°Р»СЊРЅРѕР№ Р±Р°Р·С‹ (Mode=collector): С‚РѕР»СЊРєРѕ С‡С‚РµРЅРёРµ Рё РѕС‚РїСЂР°РІРєР°.</summary>
    public bool SkipLocalDb => Mode.Equals("collector", StringComparison.OrdinalIgnoreCase);

    public static SpendConfig Load(IConfiguration cfg)
    {
        var c = new SpendConfig();
        cfg.GetSection("Spend").Bind(c);
        return c;
    }
}
