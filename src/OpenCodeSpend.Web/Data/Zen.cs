using System.Text;

namespace OpenCodeSpend.Data;

/// <summary>Синхронизация сводки с Zen/Console (Usage API, CSV). Требуется service-account ключ oc_sk_...</summary>
public sealed class ZenClient(SpendConfig config)
{
    private readonly SpendConfig _config = config;

    public static string[] Ranges { get; } = ["24h", "7d", "30d"];

    public async Task<(bool ok, string? error, List<Dictionary<string, string>> rows)> FetchAsync(
        string range, string scope, string? userEmail, CancellationToken ct = default)
    {
        var key = _config.ZenServiceKey;
        if (string.IsNullOrWhiteSpace(key))
            return (false, "не задан service-account ключ (настройки → Zen)", new());

        var url = $"{_config.ZenConsoleUrl.TrimEnd('/')}/api/v1/usage/export" +
                  $"?scope={Uri.EscapeDataString(scope)}&range={Uri.EscapeDataString(range)}";
        if (scope == "member" && !string.IsNullOrWhiteSpace(userEmail))
            url += "&user_email=" + Uri.EscapeDataString(userEmail);

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(40) };
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
            req.Headers.TryAddWithoutValidation("Accept", "text/csv");
            using var resp = await http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                return (false, $"HTTP {(int)resp.StatusCode}: {body[..Math.Min(200, body.Length)]}", new());
            return (true, null, ParseCsv(body));
        }
        catch (Exception e)
        {
            return (false, e.Message, new());
        }
    }

    /// <summary>Минимальный CSV-парсер с поддержкой кавычек.</summary>
    public static List<Dictionary<string, string>> ParseCsv(string text)
    {
        var rows = new List<Dictionary<string, string>>();
        var lines = SplitRecords(text);
        if (lines.Count == 0) return rows;
        var header = lines[0];
        for (var i = 1; i < lines.Count; i++)
        {
            var d = new Dictionary<string, string>();
            for (var c = 0; c < header.Count; c++)
                d[header[c]] = c < lines[i].Count ? lines[i][c] : "";
            rows.Add(d);
        }
        return rows;
    }

    private static List<List<string>> SplitRecords(string text)
    {
        var records = new List<List<string>>();
        var cur = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(ch);
            }
            else if (ch == '"') inQuotes = true;
            else if (ch == ',' || ch == ';')
            {
                cur.Add(sb.ToString().Trim());
                sb.Clear();
            }
            else if (ch == '\n')
            {
                cur.Add(sb.ToString().Trim());
                sb.Clear();
                if (cur.Count > 1 || cur[0].Length > 0) records.Add(cur);
                cur = new List<string>();
            }
            else if (ch != '\r') sb.Append(ch);
        }
        if (sb.Length > 0 || cur.Count > 0)
        {
            cur.Add(sb.ToString().Trim());
            if (cur.Count > 1 || cur[0].Length > 0) records.Add(cur);
        }
        return records;
    }
}
