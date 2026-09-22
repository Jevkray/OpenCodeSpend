using Microsoft.Data.Sqlite;

namespace OpenCodeSpend.Data;

public sealed record Totals(decimal Cost, long Tokens, long Messages, long Sessions);
public sealed record ModelSpend(string Provider, string Model, decimal Cost, long Tokens, long Messages);
public sealed record DaySpend(DateOnly Day, string Provider, string Model, decimal Cost, long Tokens);
public sealed record SessionSpend(string Id, string? ParentId, string? Agent, string? Title, string? Provider,
    string? Model, DateTimeOffset Updated, decimal Cost, long Tokens, long Messages);
public sealed record LiveSub(string Id, string? Agent, string? Title, decimal Cost, long Tokens, long Messages, DateTimeOffset Updated);

/// <summary>Данные локального opencode.db. uid = null — локальная область (машина-сборщик, без фильтра).</summary>
public sealed class SpendStore(Db db)
{
    private readonly Db _db = db;

    public Task EnsureSchemaAsync(CancellationToken ct = default) { _db.EnsureSchema(); return Task.CompletedTask; }

    private static void Bind(SqliteCommand cmd, params object?[] values)
    {
        for (var i = 0; i < values.Length; i++)
            cmd.Parameters.AddWithValue("@" + (i + 1), values[i] ?? DBNull.Value);
    }

    /// <summary>Фильтр владельца: null — вся локальная область.</summary>
    private static void Uid(SqliteCommand cmd, string? uid)
        => cmd.Parameters.AddWithValue("@u", (object?)uid ?? DBNull.Value);

    /// <summary>Владелец для записи: null — локальная область.</summary>
    private static void Owner(SqliteCommand cmd, string? uid)
        => cmd.Parameters.AddWithValue("@u", string.IsNullOrWhiteSpace(uid) ? Db.LocalUser : uid);

    // ---------- состояние синхронизации ----------

    public Task<string?> GetStateAsync(string key, string? uid = null, CancellationToken ct = default)
    {
        using var cn = _db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select value from sync_state where (@u is null or user_id=@u) and key=@1";
        Uid(cmd, uid); Bind(cmd, key);
        return Task.FromResult(cmd.ExecuteScalar() as string);
    }

    public Task SetStateAsync(string key, string value, string? uid = null, CancellationToken ct = default)
    {
        using var cn = _db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = """
            insert into sync_state(user_id,key,value,updated) values(@u,@1,@2,datetime('now'))
            on conflict(user_id,key) do update set value=excluded.value, updated=datetime('now')
            """;
        Owner(cmd, uid); Bind(cmd, key, value);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    /// <summary>Все значения ключа по владельцам (для профиля каждого пользователя).</summary>
    public Task<List<(string Uid, string Value)>> AllStateAsync(string key, CancellationToken ct = default)
    {
        var list = new List<(string, string)>();
        using var cn = _db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select user_id, value from sync_state where key=@1 and value is not null and value <> ''";
        Bind(cmd, key);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((r.GetString(0), r.GetString(1)));
        return Task.FromResult(list);
    }

    // ---------- записи opencode.db ----------

    public Task<int> UpsertUsageAsync(IReadOnlyList<UsageRecord> rows, string? uid = null, CancellationToken ct = default)
    {
        if (rows.Count == 0) return Task.FromResult(0);
        using var cn = _db.Open();
        using var tx = cn.BeginTransaction();
        using var cmd = cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            insert into usage_record(user_id,id,session_id,parent_session_id,project_id,agent,mode,provider_id,model_id,variant,ts,
                cost,input_tokens,output_tokens,reasoning_tokens,cache_read,cache_write)
            values(@u,@1,@2,@3,@4,@5,@6,@7,@8,@9,@10,@11,@12,@13,@14,@15,@16)
            on conflict(user_id,id) do update set cost=excluded.cost, input_tokens=excluded.input_tokens,
                output_tokens=excluded.output_tokens, reasoning_tokens=excluded.reasoning_tokens,
                cache_read=excluded.cache_read, cache_write=excluded.cache_write
            """;
        for (var i = 0; i < 16; i++) cmd.Parameters.Add(new SqliteParameter { ParameterName = "@" + (i + 1) });
        Owner(cmd, uid);
        foreach (var r in rows)
        {
            cmd.Parameters[0].Value = r.Id;
            cmd.Parameters[1].Value = r.SessionId;
            cmd.Parameters[2].Value = (object?)r.ParentSessionId ?? DBNull.Value;
            cmd.Parameters[3].Value = (object?)r.ProjectId ?? DBNull.Value;
            cmd.Parameters[4].Value = (object?)r.Agent ?? DBNull.Value;
            cmd.Parameters[5].Value = (object?)r.Mode ?? DBNull.Value;
            cmd.Parameters[6].Value = (object?)r.ProviderId ?? DBNull.Value;
            cmd.Parameters[7].Value = (object?)r.ModelId ?? DBNull.Value;
            cmd.Parameters[8].Value = (object?)r.Variant ?? DBNull.Value;
            cmd.Parameters[9].Value = Db.Iso(r.Ts);
            cmd.Parameters[10].Value = (double)r.Cost;
            cmd.Parameters[11].Value = r.InputTokens;
            cmd.Parameters[12].Value = r.OutputTokens;
            cmd.Parameters[13].Value = r.ReasoningTokens;
            cmd.Parameters[14].Value = r.CacheRead;
            cmd.Parameters[15].Value = r.CacheWrite;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return Task.FromResult(rows.Count);
    }

    public Task<int> UpsertSessionsAsync(IReadOnlyList<SessionRecord> rows, string? uid = null, CancellationToken ct = default)
    {
        if (rows.Count == 0) return Task.FromResult(0);
        using var cn = _db.Open();
        using var tx = cn.BeginTransaction();
        using var cmd = cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            insert into session_meta(user_id,id,parent_id,project_id,agent,title,provider_id,model_id,created,updated,
                cost,input_tokens,output_tokens,reasoning_tokens,cache_read)
            values(@u,@1,@2,@3,@4,@5,@6,@7,@8,@9,@10,@11,@12,@13,@14)
            on conflict(user_id,id) do update set title=excluded.title, updated=excluded.updated, cost=excluded.cost,
                input_tokens=excluded.input_tokens, output_tokens=excluded.output_tokens,
                reasoning_tokens=excluded.reasoning_tokens, cache_read=excluded.cache_read
            """;
        for (var i = 0; i < 14; i++) cmd.Parameters.Add(new SqliteParameter { ParameterName = "@" + (i + 1) });
        Owner(cmd, uid);
        foreach (var r in rows)
        {
            cmd.Parameters[0].Value = r.Id;
            cmd.Parameters[1].Value = (object?)r.ParentId ?? DBNull.Value;
            cmd.Parameters[2].Value = (object?)r.ProjectId ?? DBNull.Value;
            cmd.Parameters[3].Value = (object?)r.Agent ?? DBNull.Value;
            cmd.Parameters[4].Value = (object?)r.Title ?? DBNull.Value;
            cmd.Parameters[5].Value = (object?)r.ProviderId ?? DBNull.Value;
            cmd.Parameters[6].Value = (object?)r.ModelId ?? DBNull.Value;
            cmd.Parameters[7].Value = Db.Iso(r.Created);
            cmd.Parameters[8].Value = Db.Iso(r.Updated);
            cmd.Parameters[9].Value = (double)r.Cost;
            cmd.Parameters[10].Value = r.InputTokens;
            cmd.Parameters[11].Value = r.OutputTokens;
            cmd.Parameters[12].Value = r.ReasoningTokens;
            cmd.Parameters[13].Value = r.CacheRead;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return Task.FromResult(rows.Count);
    }

    private const string TokenSum = "(input_tokens+output_tokens+reasoning_tokens+cache_read+cache_write)";

    public Task<Totals> TotalsAsync(DateTimeOffset from, DateTimeOffset to, string? uid = null, CancellationToken ct = default)
    {
        using var cn = _db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = $"""
            select coalesce(sum(cost),0), coalesce(sum({TokenSum}),0), count(*), count(distinct session_id)
            from usage_record where (@u is null or user_id=@u) and ts >= @1 and ts < @2
            """;
        Uid(cmd, uid); Bind(cmd, Db.Iso(from), Db.Iso(to));
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return Task.FromResult(new Totals(0, 0, 0, 0));
        return Task.FromResult(new Totals((decimal)r.GetDouble(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3)));
    }

    public Task<List<ModelSpend>> ByModelAsync(DateTimeOffset from, DateTimeOffset to, string? uid = null, CancellationToken ct = default)
    {
        var list = new List<ModelSpend>();
        using var cn = _db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = $"""
            select coalesce(provider_id,'?'), coalesce(model_id,'?'), sum(cost), sum({TokenSum}), count(*)
            from usage_record where (@u is null or user_id=@u) and ts >= @1 and ts < @2
            group by 1,2 order by 3 desc
            """;
        Uid(cmd, uid); Bind(cmd, Db.Iso(from), Db.Iso(to));
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new ModelSpend(r.GetString(0), r.GetString(1), (decimal)r.GetDouble(2), r.GetInt64(3), r.GetInt64(4)));
        return Task.FromResult(list);
    }

    public Task<List<DaySpend>> SeriesAsync(DateTimeOffset from, string tz, string? uid = null, CancellationToken ct = default)
    {
        var tzi = ResolveTz(tz);
        var fromIso = Db.Iso(from);
        var grouped = new Dictionary<(DateOnly, string, string), (double cost, long tokens)>();
        using var cn = _db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = $"""
            select ts, coalesce(provider_id,'?'), coalesce(model_id,'?'), cost, {TokenSum}
            from usage_record where (@u is null or user_id=@u) and ts >= @1
            """;
        Uid(cmd, uid); Bind(cmd, fromIso);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var local = TimeZoneInfo.ConvertTime(Db.Parse(r.GetString(0)), tzi);
            var key = (DateOnly.FromDateTime(local.DateTime), r.GetString(1), r.GetString(2));
            var cur = grouped.TryGetValue(key, out var v) ? v : (0, 0L);
            grouped[key] = (cur.Item1 + r.GetDouble(3), cur.Item2 + r.GetInt64(4));
        }
        var list = grouped.Select(kv => new DaySpend(kv.Key.Item1, kv.Key.Item2, kv.Key.Item3,
            (decimal)kv.Value.cost, kv.Value.tokens)).OrderBy(x => x.Day).ToList();
        return Task.FromResult(list);
    }

    private static TimeZoneInfo ResolveTz(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch { return TimeZoneInfo.Utc; }
    }

    public Task<List<SessionSpend>> SessionsAsync(DateTimeOffset from, int limit, string? uid = null, CancellationToken ct = default)
    {
        var list = new List<SessionSpend>();
        using var cn = _db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = """
            select s.id, s.parent_id, s.agent, s.title, s.provider_id, s.model_id, s.updated, s.cost,
                   (s.input_tokens+s.output_tokens+s.reasoning_tokens+s.cache_read) as tokens,
                   (select count(*) from usage_record u where u.user_id = s.user_id and u.session_id = s.id) as msgs
            from session_meta s where (@u is null or s.user_id=@u) and s.updated >= @1
            order by s.cost desc limit @2
            """;
        Uid(cmd, uid); Bind(cmd, Db.Iso(from), limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new SessionSpend(r.GetString(0), Str(r, 1), Str(r, 2), Str(r, 3), Str(r, 4), Str(r, 5),
                Db.Parse(r.GetString(6)), (decimal)r.GetDouble(7), r.GetInt64(8), r.GetInt64(9)));
        return Task.FromResult(list);
    }

    private static string? Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    public Task<(SessionSpend? Root, List<LiveSub> Subs)> LiveAsync(string? uid = null, CancellationToken ct = default)
    {
        using var cn = _db.Open();
        string? root;
        using (var cmd = cn.CreateCommand())
        {
            cmd.CommandText = "select id from session_meta where (@u is null or user_id=@u) order by updated desc limit 1";
            Uid(cmd, uid);
            root = cmd.ExecuteScalar() as string;
        }
        if (root is null) return Task.FromResult<(SessionSpend?, List<LiveSub>)>((null, new()));

        for (var i = 0; i < 50; i++)
        {
            using var up = cn.CreateCommand();
            up.CommandText = "select parent_id from session_meta where id=@1 and (@u is null or user_id=@u)";
            Bind(up, root); Uid(up, uid);
            var parent = up.ExecuteScalar() as string;
            if (parent is null) break;
            root = parent;
        }

        SessionSpend? rootSpend = null;
        using (var cmd = cn.CreateCommand())
        {
            cmd.CommandText = """
                select s.id, s.parent_id, s.agent, s.title, s.provider_id, s.model_id, s.updated, s.cost,
                       (s.input_tokens+s.output_tokens+s.reasoning_tokens+s.cache_read) as tokens,
                       (select count(*) from usage_record u where u.user_id = s.user_id and u.session_id = s.id) as msgs
                from session_meta s where s.id = @1 and (@u is null or s.user_id=@u)
                """;
            Bind(cmd, root); Uid(cmd, uid);
            using var r = cmd.ExecuteReader();
            if (r.Read())
                rootSpend = new SessionSpend(r.GetString(0), Str(r, 1), Str(r, 2), Str(r, 3), Str(r, 4), Str(r, 5),
                    Db.Parse(r.GetString(6)), (decimal)r.GetDouble(7), r.GetInt64(8), r.GetInt64(9));
        }

        var subs = new List<LiveSub>();
        using (var cmd = cn.CreateCommand())
        {
            cmd.CommandText = """
                select s.id, s.agent, s.title, s.cost,
                       (s.input_tokens+s.output_tokens+s.reasoning_tokens+s.cache_read) as tokens,
                       (select count(*) from usage_record u where u.user_id = s.user_id and u.session_id = s.id) as msgs, s.updated
                from session_meta s where s.parent_id = @1 and (@u is null or s.user_id=@u) order by s.updated
                """;
            Bind(cmd, root); Uid(cmd, uid);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                subs.Add(new LiveSub(r.GetString(0), Str(r, 1), Str(r, 2), (decimal)r.GetDouble(3),
                    r.GetInt64(4), r.GetInt64(5), Db.Parse(r.GetString(6))));
        }
        return Task.FromResult((rootSpend, subs));
    }

    // ---------- пользователи ----------

    public Task<int> UserCountAsync(CancellationToken ct = default)
    {
        using var cn = _db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select count(*) from app_user";
        return Task.FromResult(Convert.ToInt32(cmd.ExecuteScalar()));
    }

    /// <summary>Пользователь = аккаунт Google или GitHub (ключ — email). Повторный вход обновляет профиль.</summary>
    public Task<User> UpsertUserAsync(string email, string? name, string? picture, string? provider = null, CancellationToken ct = default)
    {
        using var cn = _db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = """
            insert into app_user(id,email,name,picture,provider) values(@1,@2,@3,@4,@5)
            on conflict(email) do update set name=excluded.name, picture=excluded.picture,
                provider=coalesce(excluded.provider, app_user.provider)
            returning id,email,name,picture,provider,created
            """;
        Bind(cmd, "u_" + Guid.NewGuid().ToString("N"), email, name, picture, provider);
        using var r = cmd.ExecuteReader();
        r.Read();
        return Task.FromResult(new User(r.GetString(0), Str(r, 1), Str(r, 2), Str(r, 3), Str(r, 4), Db.Parse(r.GetString(5))));
    }

    private const string LoginAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const int LoginCodeLength = 20;

    private static string Sha256Hex(string s)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    private static string NormalizeLoginCode(string? code)
        => (code ?? "").Replace(" ", "").Replace("-", "").Trim().ToUpperInvariant();

    /// <summary>Новый код входа в аккаунт: сохраняем SHA-256, сам код показывается один раз.</summary>
    public Task<string> NewLoginCodeAsync(string userId, CancellationToken ct = default)
    {
        var chars = new char[LoginCodeLength];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = LoginAlphabet[System.Security.Cryptography.RandomNumberGenerator.GetInt32(LoginAlphabet.Length)];
        var code = new string(chars);
        using var cn = _db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "update app_user set login_code_hash=@1 where id=@2";
        Bind(cmd, Sha256Hex(code), userId);
        cmd.ExecuteNonQuery();
        return Task.FromResult(code);
    }

    /// <summary>Есть ли у аккаунта сохранённый код входа.</summary>
    public Task<bool> HasLoginCodeAsync(string userId, CancellationToken ct = default)
    {
        using var cn = _db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select login_code_hash is not null from app_user where id=@1";
        Bind(cmd, userId);
        return Task.FromResult(cmd.ExecuteScalar() is long l && l == 1);
    }

    /// <summary>Аккаунт по коду входа (пробелы и дефисы игнорируются). null — код не подошёл.</summary>
    public Task<User?> FindByLoginCodeAsync(string? code, CancellationToken ct = default)
    {
        var norm = NormalizeLoginCode(code);
        if (norm.Length == 0) return Task.FromResult<User?>(null);
        using var cn = _db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select id,email,name,picture,provider,created from app_user where login_code_hash=@1";
        Bind(cmd, Sha256Hex(norm));
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return Task.FromResult<User?>(null);
        return Task.FromResult<User?>(new User(r.GetString(0), Str(r, 1), Str(r, 2), Str(r, 3), Str(r, 4), Db.Parse(r.GetString(5))));
    }

    public Task<User?> GetUserAsync(string id, CancellationToken ct = default)
    {
        using var cn = _db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select id,email,name,picture,provider,created from app_user where id=@1";
        Bind(cmd, id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return Task.FromResult<User?>(null);
        return Task.FromResult<User?>(new User(r.GetString(0), Str(r, 1), Str(r, 2), Str(r, 3), Str(r, 4), Db.Parse(r.GetString(5))));
    }

    public Task<List<User>> UsersAsync(CancellationToken ct = default)
    {
        var list = new List<User>();
        using var cn = _db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select id,email,name,picture,provider,created from app_user order by created";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new User(r.GetString(0), Str(r, 1), Str(r, 2), Str(r, 3), Str(r, 4), Db.Parse(r.GetString(5))));
        return Task.FromResult(list);
    }

    /// <summary>Переносит локальные данные на первого пользователя (миграция однопользовательской установки).</summary>
    public Task AdoptLocalAsync(string uid, CancellationToken ct = default)
    {
        using var cn = _db.Open();
        string[] sql =
        {
            "update or ignore usage_record set user_id=@u where user_id='local'",
            "update or ignore session_meta set user_id=@u where user_id='local'",
            "update or ignore site_usage set user_id=@u where user_id='local'",
            "update or ignore site_payment set user_id=@u where user_id='local'",
            "update or ignore site_profile set user_id=@u where user_id='local'",
            "update or ignore sync_state set user_id=@u where user_id='local'",
            "update or ignore control set user_id=@u where user_id='local'",
            "update or ignore device set user_id=@u where user_id='local'",
        };
        foreach (var s in sql)
        {
            try
            {
                using var cmd = cn.CreateCommand();
                cmd.CommandText = s;
                cmd.Parameters.AddWithValue("@u", uid);
                cmd.ExecuteNonQuery();
            }
            catch (SqliteException) { /* конфликт ключей — строки уже принадлежат пользователю */ }
        }
        return Task.CompletedTask;
    }

    // ---------- устройства ----------

    public Task TouchDeviceAsync(string deviceId, string userId, string? name, CancellationToken ct = default)
    {
        using var cn = _db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = """
            insert into device(id,user_id,name,last_seen) values(@1,@2,@3,datetime('now'))
            on conflict(id) do update set name=coalesce(excluded.name,name), last_seen=datetime('now')
            """;
        Bind(cmd, deviceId, userId, name);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task<List<Device>> DevicesAsync(string? uid = null, CancellationToken ct = default)
    {
        var list = new List<Device>();
        using var cn = _db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select id,user_id,name,created,last_seen from device where (@u is null or user_id=@u) order by last_seen desc";
        Uid(cmd, uid);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new Device(r.GetString(0), r.GetString(1), Str(r, 2), Db.Parse(r.GetString(3)), Db.Parse(r.GetString(4))));
        return Task.FromResult(list);
    }
}
