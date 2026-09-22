using System.Text.Json;

namespace OpenCodeSpend.Data;

/// <summary>Вход через Google (наш собственный OAuth-клиент).</summary>
public sealed class GoogleAuth(SpendConfig config)
{
    private readonly SpendConfig _config = config;
    public bool Enabled => _config.GoogleEnabled;

    public string AuthorizeUrl(string state, string redirectUri)
    {
        var q = new Dictionary<string, string>
        {
            ["client_id"] = _config.GoogleClientId!,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = "openid email profile",
            ["state"] = state,
            ["access_type"] = "online",
            ["prompt"] = "select_account",
        };
        return "https://accounts.google.com/o/oauth2/v2/auth?" +
               string.Join("&", q.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
    }

    public async Task<(string? email, string? name, string? picture, string? error)> ExchangeAsync(string code, string redirectUri, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var tokenReq = new HttpRequestMessage(HttpMethod.Post, "https://oauth2.googleapis.com/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["code"] = code,
                ["client_id"] = _config.GoogleClientId!,
                ["client_secret"] = _config.GoogleClientSecret!,
                ["redirect_uri"] = redirectUri,
                ["grant_type"] = "authorization_code",
            }),
        };
        using var tokenResp = await http.SendAsync(tokenReq, ct);
        var tokenBody = await tokenResp.Content.ReadAsStringAsync(ct);
        if (!tokenResp.IsSuccessStatusCode) return (null, null, null, $"token: HTTP {(int)tokenResp.StatusCode} {Trim(tokenBody)}");

        string? access = null;
        try { using var doc = JsonDocument.Parse(tokenBody); access = doc.RootElement.GetProperty("access_token").GetString(); }
        catch { }
        if (string.IsNullOrEmpty(access)) return (null, null, null, "нет access_token в ответе Google");

        using var infoReq = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/oauth2/v3/userinfo");
        infoReq.Headers.TryAddWithoutValidation("Authorization", "Bearer " + access);
        using var infoResp = await http.SendAsync(infoReq, ct);
        var infoBody = await infoResp.Content.ReadAsStringAsync(ct);
        if (!infoResp.IsSuccessStatusCode) return (null, null, null, $"userinfo: HTTP {(int)infoResp.StatusCode} {Trim(infoBody)}");

        using var info = JsonDocument.Parse(infoBody);
        var email = info.RootElement.TryGetProperty("email", out var e) ? e.GetString() : null;
        var name = info.RootElement.TryGetProperty("name", out var n) ? n.GetString() : null;
        var pic = info.RootElement.TryGetProperty("picture", out var p) ? p.GetString() : null;
        if (string.IsNullOrEmpty(email)) return (null, null, null, "Google не вернул email");
        return (email, name, pic, null);
    }

    private static string Trim(string s) => s.Length > 200 ? s[..200] : s;
}

/// <summary>Вход через GitHub (свой OAuth App).</summary>
public sealed class GitHubAuth(SpendConfig config)
{
    private readonly SpendConfig _config = config;
    public bool Enabled => _config.GitHubEnabled;

    public string AuthorizeUrl(string state, string redirectUri)
    {
        var q = new Dictionary<string, string>
        {
            ["client_id"] = _config.GitHubClientId!,
            ["redirect_uri"] = redirectUri,
            ["scope"] = "read:user user:email",
            ["state"] = state,
            ["allow_signup"] = "true",
        };
        return "https://github.com/login/oauth/authorize?" +
               string.Join("&", q.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
    }

    public async Task<(string? email, string? name, string? picture, string? error)> ExchangeAsync(string code, string redirectUri, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "OpenCodeSpend");

        using var tokenReq = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/oauth/access_token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _config.GitHubClientId!,
                ["client_secret"] = _config.GitHubClientSecret!,
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
            }),
        };
        using var tokenResp = await http.SendAsync(tokenReq, ct);
        var tokenBody = await tokenResp.Content.ReadAsStringAsync(ct);
        if (!tokenResp.IsSuccessStatusCode) return (null, null, null, $"token: HTTP {(int)tokenResp.StatusCode} {Trim(tokenBody)}");

        string? access = null, why = null;
        try
        {
            using var doc = JsonDocument.Parse(tokenBody);
            if (doc.RootElement.TryGetProperty("access_token", out var a)) access = a.GetString();
            if (doc.RootElement.TryGetProperty("error_description", out var d)) why = d.GetString();
            else if (doc.RootElement.TryGetProperty("error", out var e)) why = e.GetString();
        }
        catch { }
        if (string.IsNullOrEmpty(access)) return (null, null, null, "GitHub: " + (why ?? Trim(tokenBody)));

        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", access);
        using var userResp = await http.GetAsync("https://api.github.com/user", ct);
        var userBody = await userResp.Content.ReadAsStringAsync(ct);
        if (!userResp.IsSuccessStatusCode) return (null, null, null, $"user: HTTP {(int)userResp.StatusCode} {Trim(userBody)}");

        using var u = JsonDocument.Parse(userBody);
        var name = u.RootElement.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        var login = u.RootElement.TryGetProperty("login", out var l) ? l.GetString() : null;
        var avatar = u.RootElement.TryGetProperty("avatar_url", out var av) ? av.GetString() : null;
        var id = u.RootElement.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt64() : 0;
        string? email = u.RootElement.TryGetProperty("email", out var em) && em.ValueKind == JsonValueKind.String ? em.GetString() : null;

        // почта может быть скрыта — берём основную подтверждённую
        if (string.IsNullOrEmpty(email))
        {
            using var eResp = await http.GetAsync("https://api.github.com/user/emails", ct);
            if (eResp.IsSuccessStatusCode)
            {
                using var ed = JsonDocument.Parse(await eResp.Content.ReadAsStringAsync(ct));
                if (ed.RootElement.ValueKind == JsonValueKind.Array)
                    foreach (var e in ed.RootElement.EnumerateArray())
                    {
                        var addr = e.TryGetProperty("email", out var a2) ? a2.GetString() : null;
                        if (addr is null) continue;
                        email ??= addr;
                        var primary = e.TryGetProperty("primary", out var p) && p.ValueKind == JsonValueKind.True;
                        var verified = e.TryGetProperty("verified", out var v) && v.ValueKind == JsonValueKind.True;
                        if (primary && verified) { email = addr; break; }
                    }
            }
        }
        if (string.IsNullOrEmpty(email)) email = $"{id}+{login}@users.noreply.github.com";
        return (email, name ?? login, avatar, null);
    }

    private static string Trim(string s) => s.Length > 200 ? s[..200] : s;
}

/// <summary>Кто выполняет запрос: локальный пользователь или авторизованный через Google.</summary>
public static class CurrentUser
{
    public const string LocalId = "local";
    private const string Cookie = "ocspend_session";

    /// <summary>Идентификатор пользователя, определённый middleware. null — не авторизован.</summary>
    public static string? Uid(HttpContext ctx) => ctx.Items["userId"] as string;

    /// <summary>Есть ли настоящий аккаунт (не локальный режим без авторизации).</summary>
    public static bool IsAccount(string? uid) => !string.IsNullOrWhiteSpace(uid) && uid != LocalId;

    /// <summary>Читает токен сессии из cookie.</summary>
    public static string? Read(HttpContext ctx) => ctx.Request.Cookies[Cookie];

    /// <summary>Кладёт токен сессии в защищённую cookie.</summary>
    public static void Write(HttpContext ctx, string token, int days, bool secure)
        => ctx.Response.Cookies.Append(Cookie, token, new CookieOptions
        {
            HttpOnly = true, SameSite = SameSiteMode.Lax, Secure = secure,
            MaxAge = TimeSpan.FromDays(days), IsEssential = true,
        });

    public static void Clear(HttpContext ctx) => ctx.Response.Cookies.Delete(Cookie);
}
