using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenCodeSpend.Data;

/// <summary>Чтение переменных окружения другого процесса (чтобы найти пароль сервера opencode).</summary>
public static class ProcessEnv
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, int size, out IntPtr read);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);
    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr h, int cls, byte[] info, int size, out int ret);

    private const int ProcessQueryInformation = 0x0400;
    private const int ProcessVmRead = 0x0010;

    /// <summary>Находит переменную окружения процесса по его PID.
    /// EnvironmentSize в Windows часто равен 0, поэтому читаем блок кусками до двойного нуля.</summary>
    public static string? Find(int pid, string name)
    {
        var h = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var pbi = new byte[48];
            if (NtQueryInformationProcess(h, 0, pbi, pbi.Length, out _) != 0) return null;
            var peb = (IntPtr)BitConverter.ToInt64(pbi, 8);   // PebBaseAddress уже указатель — не разыменовываем повторно
            if (peb == IntPtr.Zero) return null;
            var parms = ReadPtr(h, IntPtr.Add(peb, 0x20));
            if (parms == IntPtr.Zero) return null;
            var env = ReadPtr(h, IntPtr.Add(parms, 0x80));
            if (env == IntPtr.Zero) return null;

            var text = new System.Text.StringBuilder();
            var chunk = new byte[4096];
            var addr = env;
            for (var i = 0; i < 64; i++)   // максимум 256 КБ
            {
                if (!ReadProcessMemory(h, addr, chunk, chunk.Length, out var read) || read <= 0) break;
                var s = System.Text.Encoding.Unicode.GetString(chunk, 0, (int)read);
                text.Append(s);
                if (s.Contains("\0\0")) break;   // конец блока окружения
                addr = IntPtr.Add(addr, (int)read);
            }

            foreach (var entry in text.ToString().Split('\0'))
            {
                if (entry.Length == 0) break;    // пустая строка = конец блока
                var i = entry.IndexOf('=');
                if (i > 0 && entry[..i].Equals(name, StringComparison.OrdinalIgnoreCase))
                    return entry[(i + 1)..];
            }
            return null;
        }
        catch { return null; }
        finally { CloseHandle(h); }
    }

    private static IntPtr ReadPtr(IntPtr h, IntPtr addr)
    {
        var buf = new byte[8];
        if (!ReadProcessMemory(h, addr, buf, 8, out _)) return IntPtr.Zero;
        return (IntPtr)BitConverter.ToInt64(buf, 0);
    }
}

/// <summary>Состояние канала управления на сервере: снимок активных сессий и очередь команд «стоп».
/// Всё привязано к пользователю — чужие сессии и команды недоступны.</summary>
public sealed class ControlState(Db db)
{
    private readonly Db _db = db;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private static string Key(string uid) => string.IsNullOrWhiteSpace(uid) ? Db.LocalUser : uid;

    private string? Get(string uid, string key)
    {
        using var cn = _db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select value from control where user_id=@1 and key=@2";
        cmd.Parameters.AddWithValue("@1", Key(uid));
        cmd.Parameters.AddWithValue("@2", key);
        return cmd.ExecuteScalar() as string;
    }

    private void Set(string uid, string key, string value)
    {
        using var cn = _db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = """
            insert into control(user_id,key,value,updated) values(@1,@2,@3,datetime('now'))
            on conflict(user_id,key) do update set value=excluded.value, updated=datetime('now')
            """;
        cmd.Parameters.AddWithValue("@1", Key(uid));
        cmd.Parameters.AddWithValue("@2", key);
        cmd.Parameters.AddWithValue("@3", value);
        cmd.ExecuteNonQuery();
    }

    public void SetSessions(string uid, object sessions, string? device = null)
        => Set(uid, "sessions", JsonSerializer.Serialize(new { device, at = DateTimeOffset.UtcNow, items = sessions }, Json));

    public string GetSessionsRaw(string uid) => Get(uid, "sessions") ?? """{"items":[]}""";

    public List<string> GetPending(string uid)
    {
        try { return JsonSerializer.Deserialize<List<string>>(Get(uid, "pending") ?? "[]") ?? new(); }
        catch { return new(); }
    }

    /// <summary>Отдаёт накопленные команды «стоп» и очищает очередь.</summary>
    public List<string> TakePending(string uid)
    {
        var list = GetPending(uid);
        if (list.Count > 0) Set(uid, "pending", JsonSerializer.Serialize(new List<string>(), Json));
        return list;
    }

    public void EnqueueStop(string uid, string id)
    {
        var list = GetPending(uid);
        if (!list.Contains(id)) { list.Add(id); Set(uid, "pending", JsonSerializer.Serialize(list, Json)); }
    }
}

/// <summary>Управление локальным сервером opencode: статус сессий и остановка всех активных.</summary>
public sealed class OpencodeControl(SpendConfig config, ILogger<OpencodeControl> log)
{
    private static readonly Regex NetstatLine = new(
        @"^\s*TCP\s+(?<addr>\S+):(?<port>\d+)\s+\S+\s+LISTENING\s+(?<pid>\d+)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    public string? LastError { get; private set; }
    public int? Port { get; private set; }
    public DateTimeOffset? CheckedAt { get; private set; }

    private string? _password;
    private DateTime _passwordTriedAt = DateTime.MinValue;
    private readonly object _passwordLock = new();
    private static readonly TimeSpan PasswordRetryEvery = TimeSpan.FromSeconds(5);

    // Кэш всех рабочих backend-ов: перебор процессов дорогой, панель опрашивает раз в секунду.
    private readonly object _backendsLock = new();
    private List<(string url, string? password)>? _backends;
    private DateTime _backendsAt = DateTime.MinValue;
    private static readonly TimeSpan BackendsTtl = TimeSpan.FromSeconds(5);

    // Какому backend-у принадлежит сессия — чтобы «стоп» ушёл именно туда.
    private readonly ConcurrentDictionary<string, (string url, string? password)> _sessionBackend = new();

    /// <summary>Пора ли повторить поиск пароля: он ещё не найден и с прошлой попытки прошло достаточно времени.</summary>
    public static bool ShouldRetryPassword(string? found, DateTime lastTryUtc, DateTime nowUtc, TimeSpan every)
        => string.IsNullOrEmpty(found) && (lastTryUtc == DateTime.MinValue || nowUtc - lastTryUtc >= every);

    /// <summary>Забывает найденный пароль, чтобы следующее обращение искало его заново (например, после 401).</summary>
    private void ResetPassword()
    {
        lock (_passwordLock)
        {
            _password = null;
            _passwordTriedAt = DateTime.MinValue;
        }
    }

    /// <summary>Пароль сервера opencode: из настроек, из окружения или найденный в процессе opencode.
    /// Пока пароль не найден, повторяем поиск не чаще PasswordRetryEvery — сервер может стартовать позже нас.</summary>
    private string? Pw
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(config.OpencodeServerPassword)) return config.OpencodeServerPassword;
            var env = Environment.GetEnvironmentVariable("OPENCODE_SERVER_PASSWORD");
            if (!string.IsNullOrWhiteSpace(env)) return env;
            if (ShouldRetryPassword(_password, _passwordTriedAt, DateTime.UtcNow, PasswordRetryEvery))
            {
                lock (_passwordLock)
                {
                    if (ShouldRetryPassword(_password, _passwordTriedAt, DateTime.UtcNow, PasswordRetryEvery))
                    {
                        _passwordTriedAt = DateTime.UtcNow;
                        _password = DiscoverPassword();
                        if (_password is not null) log.LogInformation("Пароль сервера opencode найден автоматически");
                    }
                }
            }
            return _password;
        }
    }

    /// <summary>Пароль процесса opencode по его PID; если у процесса не читается — общий поиск по всем процессам.</summary>
    private static string? DiscoverPassword(int pid)
    {
        var v = ProcessEnv.Find(pid, "OPENCODE_SERVER_PASSWORD");
        return !string.IsNullOrWhiteSpace(v) ? v : DiscoverPassword();
    }

    /// <summary>Ищет OPENCODE_SERVER_PASSWORD в переменных окружения процессов opencode.</summary>
    private static string? DiscoverPassword()
    {
        var self = Environment.ProcessId;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == self) continue;
                var n = p.ProcessName;
                if (!n.Contains("opencode", StringComparison.OrdinalIgnoreCase)) continue;
                if (n.Contains("spend", StringComparison.OrdinalIgnoreCase)) continue;
                var v = ProcessEnv.Find(p.Id, "OPENCODE_SERVER_PASSWORD");
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }
            catch { }
            finally { p.Dispose(); }
        }
        return null;
    }

    /// <summary>Порт-кандидат вместе с PID процесса-владельца: у каждого opencode свой пароль.</summary>
    private readonly record struct Candidate(int Port, int Pid);

    /// <summary>Порты-кандидаты вместе с PID: слушающие сокеты процессов opencode (кроме нашего).</summary>
    private List<Candidate> DiscoverCandidates()
    {
        var ports = new List<Candidate>();
        try
        {
            var pids = new HashSet<string>();
            var selfId = Environment.ProcessId;
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    var n = p.ProcessName;
                    if (p.Id != selfId
                        && n.Contains("opencode", StringComparison.OrdinalIgnoreCase)
                        && !n.Contains("spend", StringComparison.OrdinalIgnoreCase))
                        pids.Add(p.Id.ToString(CultureInfo.InvariantCulture));
                }
                catch { }
                finally { p.Dispose(); }
            }
            if (pids.Count == 0) return ports;

            var psi = new ProcessStartInfo("netstat", "-ano")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return ports;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);

            foreach (Match m in NetstatLine.Matches(output))
            {
                var pidText = m.Groups["pid"].Value;
                if (!pids.Contains(pidText)) continue;
                var addr = m.Groups["addr"].Value;
                if (addr is not ("127.0.0.1" or "0.0.0.0" or "[::1]" or "[::]")) continue;
                var port = int.Parse(m.Groups["port"].Value, CultureInfo.InvariantCulture);
                if (ports.All(c => c.Port != port))
                    ports.Add(new Candidate(port, int.Parse(pidText, CultureInfo.InvariantCulture)));
            }
        }
        catch (Exception e) { log.LogDebug(e, "discover opencode ports failed"); }
        return ports;
    }

    /// <summary>Только номера портов-кандидатов (обёртка над DiscoverCandidates).</summary>
    public List<int> DiscoverPorts() => DiscoverCandidates().Select(c => c.Port).ToList();

    /// <summary>Находит ВСЕ рабочие серверы opencode: каждый слушающий порт процессов opencode проверяется
    /// тем же запросом /session/status (401 тоже считается рабочим). Если задан config.OpencodeServerUrl —
    /// используется только он. Результат кэшируется, чтобы не сканировать процессы на каждый запрос.</summary>
    public async Task<List<(string url, string? password)>> ResolveAllAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(config.OpencodeServerUrl))
            return new List<(string url, string? password)> { (config.OpencodeServerUrl!.TrimEnd('/'), Pw) };

        lock (_backendsLock)
        {
            if (_backends is not null && DateTime.UtcNow - _backendsAt < BackendsTtl)
                return _backends;
        }

        var list = new List<(string url, string? password)>();
        int? firstPort = null;
        // Пароль подбираем под конкретный порт: у каждого процесса opencode он свой.
        foreach (var c in DiscoverCandidates())
        {
            var url = $"http://127.0.0.1:{c.Port}";
            var pw = DiscoverPassword(c.Pid);
            try
            {
                using var http = Client(pw);
                using var resp = await http.GetAsync(url + "/session/status", ct);
                var alive = resp.StatusCode == System.Net.HttpStatusCode.Unauthorized;
                if (!alive)
                {
                    var text = (await resp.Content.ReadAsStringAsync(ct)).TrimStart();
                    alive = resp.IsSuccessStatusCode && (text.StartsWith("{") || text.StartsWith("["));
                }
                if (alive)
                {
                    list.Add((url, pw));
                    firstPort ??= c.Port;
                }
            }
            catch { }
        }

        lock (_backendsLock)
        {
            _backends = list;
            _backendsAt = DateTime.UtcNow;
            Port = firstPort;
            CheckedAt = DateTimeOffset.UtcNow;
        }
        LastError = list.Count == 0 ? "локальный сервер opencode не найден (порт не определён)" : null;
        return list;
    }

    /// <summary>Сбрасывает кэш backend-ов, но не чаще раза в TTL: панель опрашивает раз в секунду,
    /// поэтому при сбое не сканируем процессы на каждый запрос.</summary>
    private void InvalidateBackends()
    {
        lock (_backendsLock)
        {
            if (_backends is not null && DateTime.UtcNow - _backendsAt < BackendsTtl) return;
            _backends = null;
        }
    }

    /// <summary>Backend, которому принадлежит сессия: сначала из запомненной карты, иначе поиск по списку.</summary>
    private async Task<(string url, string? password)?> BackendOfAsync(
        string sessionId, List<(string url, string? password)> backends, CancellationToken ct)
    {
        if (_sessionBackend.TryGetValue(sessionId, out var known))
            foreach (var b in backends)
                if (b.url == known.url) return b;

        foreach (var b in backends)
        {
            try
            {
                using var http = Client(b.password);
                using var resp = await http.GetAsync(b.url + "/session", ct);
                if (!resp.IsSuccessStatusCode) continue;
                var meta = ParseSessions(await resp.Content.ReadAsStringAsync(ct));
                if (meta.Any(m => m.Id == sessionId)) return b;
            }
            catch { }
        }
        return null;
    }


    private HttpClient Client(string? password)
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        if (!string.IsNullOrWhiteSpace(password))
        {
            var user = string.IsNullOrWhiteSpace(config.OpencodeServerUsername) ? "opencode" : config.OpencodeServerUsername!;
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        }
        return http;
    }

    public OpencodeEvents Events { get; } = new();

    // По одному потоку наблюдения на backend; ключ — адрес.
    private readonly ConcurrentDictionary<string, byte> _watched = new();

    /// <summary>Запускает чтение потока событий (SSE) для каждого рабочего backend-а.
    /// Вызывается на каждый опрос, но поток создаётся только для новых адресов.</summary>
    public void StartWatch() => _ = Task.Run(async () =>
    {
        try
        {
            foreach (var b in await ResolveAllAsync(CancellationToken.None))
            {
                if (!_watched.TryAdd(b.url, 0)) continue;
                var (url, password) = b;
                _ = Task.Run(async () =>
                {
                    try { await WatchBackendAsync(url, password); }
                    finally { _watched.TryRemove(url, out _); }
                });
            }
        }
        catch { }
    });

    /// <summary>Читает SSE-поток одного backend-а. Когда backend исчезает — поток завершается,
    /// повторный запуск произойдёт при следующем StartWatch, если сервер вернётся.</summary>
    private async Task WatchBackendAsync(string url, string? password)
    {
        try
        {
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            if (!string.IsNullOrWhiteSpace(password))
            {
                var user = string.IsNullOrWhiteSpace(config.OpencodeServerUsername) ? "opencode" : config.OpencodeServerUsername!;
                var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
            }
            using var req = new HttpRequestMessage(HttpMethod.Get, url + "/event");
            req.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized) { ResetPassword(); InvalidateBackends(); return; }
            if (!resp.IsSuccessStatusCode) { InvalidateBackends(); return; }
            using var stream = await resp.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream);
            while (true)
            {
                var line = await reader.ReadLineAsync();
                if (line is null) return;                     // поток закрылся — завершаем, перезапустит StartWatch
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var json = line[5..].Trim();
                if (json.Length > 0) Events.Apply(json);
            }
        }
        catch { InvalidateBackends(); }
    }

    /// <summary>Активен ли статус сессии из opencode: всё, что не "idle" (например "busy" или "retry").</summary>
    public static bool IsActiveStatus(string? type)
        => !string.IsNullOrEmpty(type) && !string.Equals(type, "idle", StringComparison.OrdinalIgnoreCase);

    /// <summary>Код фазы по реальному статусу: busy → работа, retry → повтор, иначе завершена.</summary>
    public static string PhaseFor(string? statusType) => statusType?.ToLowerInvariant() switch
    {
        "busy" => "working",
        "retry" => "retry",
        _ => "done",
    };

    /// <summary>Активность с учётом субагентов: сессия активна, если активна сама или любой её потомок.</summary>
    public static Dictionary<string, bool> EffectiveActive(IEnumerable<(string Id, string? ParentId, bool Active)> nodes)
    {
        var own = new Dictionary<string, bool>();
        var children = new Dictionary<string, List<string>>();
        foreach (var n in nodes)
        {
            own[n.Id] = n.Active;
            if (n.ParentId is not null)
            {
                if (!children.TryGetValue(n.ParentId, out var list)) children[n.ParentId] = list = new List<string>();
                list.Add(n.Id);
            }
        }
        var result = new Dictionary<string, bool>();
        var visiting = new HashSet<string>();
        bool Resolve(string id)
        {
            if (result.TryGetValue(id, out var known)) return known;
            if (!visiting.Add(id)) return own.TryGetValue(id, out var o) && o;   // защита от цикла
            var active = own.TryGetValue(id, out var a) && a;
            if (children.TryGetValue(id, out var ch))
                foreach (var c in ch)
                    if (Resolve(c)) active = true;
            visiting.Remove(id);
            result[id] = active;
            return active;
        }
        foreach (var n in nodes) Resolve(n.Id);
        return result;
    }

    private sealed record Meta(string Id, string Title, string? Agent, long Updated, string? Parent, string? Directory);

    /// <summary>Разбирает список сессий из GET /session (id, родитель, каталог, время).</summary>
    private static List<Meta> ParseSessions(string? json)
    {
        var list = new List<Meta>();
        if (json is null) return list;
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var id = e.TryGetProperty("id", out var i) ? i.GetString() : null;
            if (string.IsNullOrEmpty(id)) continue;
            var title = e.TryGetProperty("title", out var t) ? t.GetString() : null;
            var agent = e.TryGetProperty("agent", out var a) ? a.GetString() : null;
            var parent = e.TryGetProperty("parentID", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            var dir = e.TryGetProperty("directory", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            long updated = 0;
            if (e.TryGetProperty("time", out var tm) && tm.ValueKind == JsonValueKind.Object && tm.TryGetProperty("updated", out var u)
                && u.ValueKind == JsonValueKind.Number) updated = u.GetInt64();
            list.Add(new Meta(id, string.IsNullOrWhiteSpace(title) ? "сессия ····" + id[^Math.Min(4, id.Length)..] : title!, agent, updated, parent, dir));
        }
        return list;
    }

    /// <summary>Статусы сессий по каталогам: /session/status без directory видит только каталог сервера,
    /// поэтому спрашиваем отдельно для каждого каталога из списка сессий.</summary>
    private static async Task<Dictionary<string, string>> FetchStatusesAsync(HttpClient http, string url, IEnumerable<string?> dirs, CancellationToken ct)
    {
        var result = new Dictionary<string, string>();
        var unique = dirs.Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => d!).Distinct().ToList();
        if (dirs.Any(string.IsNullOrWhiteSpace)) unique.Add("");   // сессии без каталога — общий запрос
        foreach (var dir in unique)
        {
            try
            {
                var q = url + "/session/status" + (dir.Length > 0 ? "?directory=" + Uri.EscapeDataString(dir) : "");
                using var resp = await http.GetAsync(q, ct);
                if (!resp.IsSuccessStatusCode) continue;
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    var type = prop.Value.ValueKind == JsonValueKind.Object && prop.Value.TryGetProperty("type", out var t) ? t.GetString() : null;
                    if (type is not null) result[prop.Name] = type;
                }
            }
            catch { }
        }
        return result;
    }

    /// <summary>Останавливает все активные сессии (включая субагентов активных родителей) на всех backend-ах.</summary>
    public async Task<(bool ok, int aborted, string? error)> StopAllAsync(CancellationToken ct = default)
    {
        var backends = await ResolveAllAsync(ct);
        if (backends.Count == 0) return (false, 0, "сервер opencode не найден");
        var aborted = 0;
        var any = false;
        foreach (var b in backends)
        {
            try
            {
                using var http = Client(b.password);
                string? sessionsJson = null;
                using (var resp = await http.GetAsync(b.url + "/session", ct))
                {
                    if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized) { ResetPassword(); InvalidateBackends(); continue; }
                    if (!resp.IsSuccessStatusCode) { InvalidateBackends(); continue; }
                    sessionsJson = await resp.Content.ReadAsStringAsync(ct);
                }
                any = true;

                var meta = ParseSessions(sessionsJson);
                var statuses = await FetchStatusesAsync(http, b.url, meta.Select(m => m.Directory), ct);
                var effective = EffectiveActive(meta.Select(m => (m.Id, m.Parent, IsActiveStatus(statuses.GetValueOrDefault(m.Id)))));
                foreach (var id in effective.Where(kv => kv.Value).Select(kv => kv.Key))
                {
                    try
                    {
                        using var resp = await http.PostAsync($"{b.url}/session/{id}/abort", null, ct);
                        if (resp.IsSuccessStatusCode) aborted++;
                    }
                    catch { }
                }
            }
            catch { InvalidateBackends(); }
        }
        return (any, aborted, any ? null : "сервер opencode не найден");
    }

    private sealed record Node(string Id, string Title, string? Agent, long Updated, string? ParentId, string Status, bool Active, string? Detail);

    /// <summary>Дерево сессий (родитель → субагенты) со статусом активности.
    /// Активность берётся из реального /session/status (по каталогу сессии); если активен субагент — родитель тоже активен.</summary>
    public async Task<(bool ok, List<object> tree, int active, List<object> recent, string? error)> SessionsAsync(CancellationToken ct = default)
    {
        StartWatch();
        var backends = await ResolveAllAsync(ct);
        if (backends.Count == 0) return (false, new(), 0, new(), LastError ?? "сервер opencode не найден");
        try
        {
            var raw = new List<Node>();
            var seen = new HashSet<string>();
            foreach (var b in backends)
            {
                try
                {
                    using var http = Client(b.password);
                    string? sessionsJson = null;
                    using (var resp = await http.GetAsync(b.url + "/session", ct))
                    {
                        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized) { ResetPassword(); InvalidateBackends(); continue; }
                        if (!resp.IsSuccessStatusCode) { InvalidateBackends(); continue; }
                        sessionsJson = await resp.Content.ReadAsStringAsync(ct);
                    }

                    var meta = ParseSessions(sessionsJson);
                    var statuses = await FetchStatusesAsync(http, b.url, meta.Select(m => m.Directory), ct);
                    foreach (var m in meta)
                    {
                        if (!seen.Add(m.Id)) continue;   // id уже пришёл с другого backend-а — не дублируем
                        statuses.TryGetValue(m.Id, out var statusType);
                        raw.Add(new Node(m.Id, m.Title, m.Agent, m.Updated, m.Parent, PhaseFor(statusType), IsActiveStatus(statusType), null));
                        _sessionBackend[m.Id] = b;       // запоминаем backend, чтобы «стоп» ушёл именно туда
                    }
                }
                catch { InvalidateBackends(); }
            }

            // если активен потомок — родитель тоже активен
            var effective = EffectiveActive(raw.Select(n => (n.Id, n.ParentId, n.Active)));
            var nodes = raw.Select(n =>
            {
                var active = effective[n.Id];
                var phase = active ? (n.Active ? n.Status : PhaseFor("busy")) : PhaseFor(null);
                return n with { Active = active, Status = phase, Detail = Events.Get(n.Id)?.Detail };
            }).ToList();

            var byId = nodes.ToDictionary(n => n.Id);
            var childrenOf = nodes.Where(n => n.ParentId is not null && byId.ContainsKey(n.ParentId))
                .GroupBy(n => n.ParentId!)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(n => n.Updated).ToList());

            object Shape(Node n) => new
            {
                id = n.Id,
                title = n.Title,
                agent = n.Agent,
                status = n.Status,
                phase = n.Status,
                detail = n.Detail,
                active = n.Active,
                updated = n.Updated,
                children = childrenOf.TryGetValue(n.Id, out var ch) ? ch.Select(Shape).ToList() : new List<object>(),
            };

            // корни: активные + недавно завершённые (последние 5 минут), но не больше 30
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var recentWindow = now - 5 * 60 * 1000;
            bool IsRoot(Node n) => n.ParentId is null || !byId.ContainsKey(n.ParentId);
            var rootNodes = nodes
                .Where(IsRoot)
                .Where(n => n.Active || n.Updated >= recentWindow)
                .OrderByDescending(n => n.Updated)
                .Take(30)
                .ToList();

            // backend, у которого все сессии давно завершены, иначе «пропадает» из панели:
            // добавляем по одной самой свежей корневой сессии на каждый сервер
            var represented = rootNodes
                .Select(n => _sessionBackend.TryGetValue(n.Id, out var b) ? b.url : null)
                .ToHashSet();
            foreach (var b in backends)
            {
                if (represented.Contains(b.url)) continue;
                var newest = nodes.Where(IsRoot)
                    .Where(n => _sessionBackend.TryGetValue(n.Id, out var nb) && nb.url == b.url)
                    .OrderByDescending(n => n.Updated)
                    .FirstOrDefault();
                if (newest is not null) rootNodes.Add(newest);
            }

            var roots = rootNodes.Select(Shape).ToList();

            var activeCount = nodes.Count(n => n.Active);
            var recent = nodes.OrderByDescending(n => n.Updated).Take(15)
                .Select(n => (object)new { id = n.Id, title = n.Title, agent = n.Agent, status = n.Status, detail = n.Detail, active = n.Active })
                .ToList();

            LastError = null;
            return (true, roots, activeCount, recent, null);
        }
        catch (Exception e) { LastError = e.Message; return (false, new(), 0, new(), e.Message); }
    }

    /// <summary>Прервать сессию и все её дочерние (субагентов) на ЕЁ backend-е.</summary>
    public async Task<(bool ok, int stopped, string? error)> StopAsync(string sessionId, CancellationToken ct = default)
    {
        var backends = await ResolveAllAsync(ct);
        if (backends.Count == 0) return (false, 0, LastError ?? "сервер opencode не найден");
        var target = await BackendOfAsync(sessionId, backends, ct);
        if (target is null) return (false, 0, "сессия не найдена ни на одном сервере opencode");
        try
        {
            using var http = Client(target.Value.password);
            var ids = new List<string> { sessionId };
            try
            {
                using var resp = await http.GetAsync(target.Value.url + "/session", ct);
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                    var children = new Dictionary<string, List<string>>();
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                        foreach (var e in doc.RootElement.EnumerateArray())
                        {
                            var id = e.TryGetProperty("id", out var i) ? i.GetString() : null;
                            var pid = e.TryGetProperty("parentID", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                            if (id is null || pid is null) continue;
                            if (!children.TryGetValue(pid, out var lst)) children[pid] = lst = new List<string>();
                            lst.Add(id);
                        }
                    var queue = new Queue<string>(ids);
                    while (queue.Count > 0)
                    {
                        var cur = queue.Dequeue();
                        if (!children.TryGetValue(cur, out var ch)) continue;
                        foreach (var c in ch)
                            if (!ids.Contains(c)) { ids.Add(c); queue.Enqueue(c); }
                    }
                }
            }
            catch { }

            var stopped = 0;
            foreach (var id in ids)
            {
                try
                {
                    using var resp = await http.PostAsync($"{target.Value.url}/session/{id}/abort", null, ct);
                    if (resp.IsSuccessStatusCode) stopped++;
                }
                catch { }
            }
            _sessionBackend.TryRemove(sessionId, out _);
            return stopped > 0 ? (true, stopped, null) : (false, 0, "не удалось прервать");
        }
        catch (Exception e) { return (false, 0, e.Message); }
    }
}

