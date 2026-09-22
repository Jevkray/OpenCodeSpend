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
            var peb = ReadPtr(h, (IntPtr)BitConverter.ToInt64(pbi, 8));
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

    public void Ack(string uid, string id)
    {
        var list = GetPending(uid);
        if (list.Remove(id)) Set(uid, "pending", JsonSerializer.Serialize(list, Json));
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
    private bool _verified;

    private string? _password;
    private DateTime _passwordTriedAt = DateTime.MinValue;
    private readonly object _passwordLock = new();
    private static readonly TimeSpan PasswordRetryEvery = TimeSpan.FromSeconds(5);

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

    /// <summary>Порты-кандидаты: слушающие сокеты процессов opencode (кроме нашего).</summary>
    public List<int> DiscoverPorts()
    {
        var ports = new List<int>();
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
                if (!pids.Contains(m.Groups["pid"].Value)) continue;
                var addr = m.Groups["addr"].Value;
                if (addr is not ("127.0.0.1" or "0.0.0.0" or "[::1]" or "[::]")) continue;
                var port = int.Parse(m.Groups["port"].Value, CultureInfo.InvariantCulture);
                if (!ports.Contains(port)) ports.Add(port);
            }
        }
        catch (Exception e) { log.LogDebug(e, "discover opencode ports failed"); }
        return ports;
    }

    /// <summary>Находит рабочий адрес сервера opencode (проверяя кандидатов).</summary>
    private async Task<(string url, string? password)?> ResolveAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(config.OpencodeServerUrl))
            return (config.OpencodeServerUrl!.TrimEnd('/'), Pw);

        if (Port is not null && _verified)
            return ($"http://127.0.0.1:{Port}", Pw);

        foreach (var port in DiscoverPorts())
        {
            var url = $"http://127.0.0.1:{port}";
            try
            {
                using var http = Client(Pw);
                using var resp = await http.GetAsync(url + "/session/status", ct);
                if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    Port = port; _verified = true;
                    ResetPassword();   // пароль устарел — искать заново при следующем обращении
                    return (url, Pw);
                }
                var text = (await resp.Content.ReadAsStringAsync(ct)).TrimStart();
                if (text.StartsWith("{") || text.StartsWith("["))
                {
                    Port = port; _verified = true;
                    return (url, Pw);
                }
            }
            catch { }
        }
        LastError = "локальный сервер opencode не найден (порт не определён)";
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
    private Task? _watch;
    private readonly object _watchLock = new();

    /// <summary>Один раз запускает чтение потока событий opencode.</summary>
    public void StartWatch()
    {
        lock (_watchLock)
        {
            if (_watch is not null) return;
            _watch = Task.Run(() => WatchLoopAsync());
        }
    }

    private async Task WatchLoopAsync()
    {
        while (true)
        {
            try
            {
                var r = await ResolveAsync(CancellationToken.None);
                if (r is null) { await Task.Delay(3000); continue; }
                using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
                if (!string.IsNullOrWhiteSpace(r.Value.password))
                {
                    var user = string.IsNullOrWhiteSpace(config.OpencodeServerUsername) ? "opencode" : config.OpencodeServerUsername!;
                    var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{r.Value.password}"));
                    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
                }
                using var req = new HttpRequestMessage(HttpMethod.Get, r.Value.url + "/event");
                req.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized) { ResetPassword(); await Task.Delay(3000); continue; }
                if (!resp.IsSuccessStatusCode) { await Task.Delay(3000); continue; }
                using var stream = await resp.Content.ReadAsStreamAsync();
                using var reader = new StreamReader(stream);
                while (true)
                {
                    var line = await reader.ReadLineAsync();
                    if (line is null) break;                      // поток закрылся — переподключаемся
                    if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                    var json = line[5..].Trim();
                    if (json.Length > 0) Events.Apply(json);
                }
            }
            catch { Port = null; _verified = false; }   // соединение потеряно — порт могли сменить, ищем заново
            await Task.Delay(2000);
        }
    }

    /// <summary>Сколько сессий сейчас активно (не idle).</summary>
    public async Task<(bool ok, int active, List<string> ids)> StatusAsync(CancellationToken ct = default)
    {
        var r = await ResolveAsync(ct);
        if (r is null) { LastError = "локальный сервер opencode не найден"; CheckedAt = DateTimeOffset.UtcNow; return (false, 0, new()); }
        try
        {
            using var http = Client(r.Value.password);
            using var resp = await http.GetAsync(r.Value.url + "/session/status", ct);
            CheckedAt = DateTimeOffset.UtcNow;
            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                LastError = "нужен OPENCODE_SERVER_PASSWORD (см. настройки)";
                return (false, 0, new());
            }
            if (!resp.IsSuccessStatusCode) { LastError = $"HTTP {(int)resp.StatusCode}"; return (false, 0, new()); }

            var json = await resp.Content.ReadAsStringAsync(ct);
            var ids = new List<string>();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    var v = prop.Value;
                    var type = v.ValueKind == JsonValueKind.Object && v.TryGetProperty("type", out var t) ? t.GetString() : v.ToString();
                    if (!string.Equals(type, "idle", StringComparison.OrdinalIgnoreCase)) ids.Add(prop.Name);
                }
            }
            LastError = null;
            return (true, ids.Count, ids);
        }
        catch (Exception e) { LastError = e.Message; CheckedAt = DateTimeOffset.UtcNow; return (false, 0, new()); }
    }

    /// <summary>Останавливает все активные сессии.</summary>
    public async Task<(bool ok, int aborted, string? error)> StopAllAsync(CancellationToken ct = default)
    {
        var (ok, _, ids) = await StatusAsync(ct);
        if (!ok)
        {
            // даже если статус не отдался — пробуем взять список и прервать все
            var all = await ListSessionsAsync(ct);
            ids = all;
        }
        if (ids.Count == 0) return (true, 0, null);

        var r = await ResolveAsync(ct);
        if (r is null) return (false, 0, "сервер opencode не найден");

        var aborted = 0;
        try
        {
            using var http = Client(r.Value.password);
            foreach (var id in ids)
            {
                try
                {
                    using var resp = await http.PostAsync($"{r.Value.url}/session/{id}/abort", null, ct);
                    if (resp.IsSuccessStatusCode) aborted++;
                }
                catch { }
            }
            return (true, aborted, null);
        }
        catch (Exception e) { return (false, aborted, e.Message); }
    }

    /// <summary>Диагностика: сырые ответы сервера opencode.</summary>
    public async Task<Dictionary<string, object?>> DebugAsync(CancellationToken ct = default)
    {
        var res = new Dictionary<string, object?>
        {
            ["passwordFound"] = !string.IsNullOrWhiteSpace(Pw),
            ["port"] = Port,
            ["candidates"] = DiscoverPorts(),
            ["error"] = LastError,
        };
        var r = await ResolveAsync(ct);
        if (r is null) return res;
        res["url"] = r.Value.url;
        try
        {
            using var http = Client(Pw);
            using var s = await http.GetAsync(r.Value.url + "/session/status", ct);
            res["statusCode"] = (int)s.StatusCode;
            var t = await s.Content.ReadAsStringAsync(ct);
            res["status"] = t.Length > 3000 ? t[..3000] : t;
            foreach (var path in new[] { "/api/session/active", "/experimental/workspace/status", "/session/status" })
            {
                using var sx = await http.GetAsync(r.Value.url + path, ct);
                var tx = await sx.Content.ReadAsStringAsync(ct);
                res["probe " + path] = (int)sx.StatusCode + " :: " + (tx.Length > 600 ? tx[..600] : tx);
            }
            using var s2 = await http.GetAsync(r.Value.url + "/session", ct);
            res["sessionsCode"] = (int)s2.StatusCode;
            var t2 = await s2.Content.ReadAsStringAsync(ct);
            res["sessions"] = t2.Length > 3000 ? t2[..3000] : t2;
        }
        catch (Exception e) { res["error2"] = e.Message; }
        return res;
    }

    private sealed record Node(string Id, string Title, string? Agent, long Updated, string? ParentId, string Status, bool Active, string? Detail);

    private readonly Dictionary<string, long> _lastSeen = new();
    private readonly SessionActivityGate _activity = new(5000);

    /// <summary>Дерево сессий (родитель → субагенты) со статусом активности.</summary>
    public async Task<(bool ok, List<object> tree, int active, List<object> recent, string? error)> SessionsAsync(CancellationToken ct = default)
    {
        StartWatch();
        var r = await ResolveAsync(ct);
        if (r is null) return (false, new(), 0, new(), LastError ?? "сервер opencode не найден");
        try
        {
            using var http = Client(Pw);
            string? sessionsJson = null, statusJson = null;
            using (var resp = await http.GetAsync(r.Value.url + "/session", ct))
            {
                if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized) { LastError = "нужен OPENCODE_SERVER_PASSWORD"; return (false, new(), 0, new(), LastError); }
                if (resp.IsSuccessStatusCode) sessionsJson = await resp.Content.ReadAsStringAsync(ct);
            }
            using (var resp = await http.GetAsync(r.Value.url + "/session/status", ct))
            {
                if (resp.IsSuccessStatusCode) statusJson = await resp.Content.ReadAsStringAsync(ct);
            }

            var meta = new List<(string id, string title, string? agent, long updated, string? parent)>();
            if (sessionsJson is not null)
            {
                using var doc = JsonDocument.Parse(sessionsJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    foreach (var e in doc.RootElement.EnumerateArray())
                    {
                        var id = e.TryGetProperty("id", out var i) ? i.GetString() : null;
                        if (string.IsNullOrEmpty(id)) continue;
                        var title = e.TryGetProperty("title", out var t) ? t.GetString() : null;
                        var agent = e.TryGetProperty("agent", out var a) ? a.GetString() : null;
                        var parent = e.TryGetProperty("parentID", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                        long updated = 0;
                        if (e.TryGetProperty("time", out var tm) && tm.ValueKind == JsonValueKind.Object && tm.TryGetProperty("updated", out var u)
                            && u.ValueKind == JsonValueKind.Number) updated = u.GetInt64();
                        meta.Add((id, string.IsNullOrWhiteSpace(title) ? "сессия ····" + id[^Math.Min(4, id.Length)..] : title!, agent, updated, parent));
                    }
            }

            var statuses = new Dictionary<string, string>();
            if (statusJson is not null)
            {
                using var doc = JsonDocument.Parse(statusJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        var v = prop.Value;
                        var type = v.ValueKind == JsonValueKind.Object && v.TryGetProperty("type", out var t) ? t.GetString() : v.ToString();
                        statuses[prop.Name] = type ?? "unknown";
                    }
            }

            // Живое состояние из SSE-потока, если оно есть; иначе запасная эвристика:
            //  • пришёл не-idle статус, либо
            //  • сессия обновилась с прошлой проверки (идёт работа), либо
            //  • обновлялась последние 3 секунды.
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var nodes = new List<Node>();
            foreach (var m in meta)
            {
                var live = Events.Get(m.id);
                var idle = statuses.TryGetValue(m.id, out var s) && string.Equals(s, "idle", StringComparison.OrdinalIgnoreCase);
                var changed = _lastSeen.TryGetValue(m.id, out var prev) ? prev != m.updated : false;
                var fresh = m.updated > 0 && now - m.updated < 3000;
                var rawActive = live?.Active ?? (!idle && (changed || fresh));
                var active = _activity.Apply(m.id, rawActive, now);
                string phase;
                if (active && live is not null && !live.Active) phase = "завершается…";   // держим активной по гистерезису
                else if (active) phase = live?.Phase ?? "работает";
                else phase = "завершена";
                nodes.Add(new Node(m.id, m.title, m.agent, m.updated, m.parent, phase, active, live?.Detail));
            }
            foreach (var m in meta) _lastSeen[m.id] = m.updated;

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
            var recentWindow = now - 5 * 60 * 1000;
            var roots = nodes
                .Where(n => n.ParentId is null || !byId.ContainsKey(n.ParentId))
                .Where(n => SubtreeActive(n) || n.Updated >= recentWindow)
                .OrderByDescending(n => n.Updated)
                .Take(30)
                .Select(Shape)
                .ToList();

            bool SubtreeActive(Node n) => n.Active || (childrenOf.TryGetValue(n.Id, out var ch2) && ch2.Any(SubtreeActive));

            var activeCount = nodes.Count(n => n.Active);
            var recent = nodes.OrderByDescending(n => n.Updated).Take(15)
                .Select(n => (object)new { id = n.Id, title = n.Title, agent = n.Agent, status = n.Status, detail = n.Detail, active = n.Active })
                .ToList();

            LastError = null;
            return (true, roots, activeCount, recent, null);
        }
        catch (Exception e) { LastError = e.Message; return (false, new(), 0, new(), e.Message); }
    }

    /// <summary>Прервать сессию и все её дочерние (субагентов).</summary>
    public async Task<(bool ok, int stopped, string? error)> StopAsync(string sessionId, CancellationToken ct = default)
    {
        var r = await ResolveAsync(ct);
        if (r is null) return (false, 0, LastError ?? "сервер opencode не найден");
        try
        {
            using var http = Client(Pw);
            var ids = new List<string> { sessionId };
            try
            {
                using var resp = await http.GetAsync(r.Value.url + "/session", ct);
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
                    using var resp = await http.PostAsync($"{r.Value.url}/session/{id}/abort", null, ct);
                    if (resp.IsSuccessStatusCode) stopped++;
                }
                catch { }
            }
            if (stopped > 0)
            {
                var gateNow = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                foreach (var id in ids) _activity.ForceInactive(id, gateNow);
            }
            return stopped > 0 ? (true, stopped, null) : (false, 0, "не удалось прервать");
        }
        catch (Exception e) { return (false, 0, e.Message); }
    }

    private async Task<List<string>> ListSessionsAsync(CancellationToken ct)
    {
        var r = await ResolveAsync(ct);
        if (r is null) return new();
        try
        {
            using var http = Client(r.Value.password);
            using var resp = await http.GetAsync(r.Value.url + "/session", ct);
            if (!resp.IsSuccessStatusCode) return new();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var ids = new List<string>();
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                foreach (var e in doc.RootElement.EnumerateArray())
                    if (e.TryGetProperty("id", out var id)) ids.Add(id.GetString() ?? "");
            return ids.Where(x => x.Length > 0).ToList();
        }
        catch { return new(); }
    }
}
