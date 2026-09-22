using Microsoft.Data.Sqlite;

namespace OpenCodeSpend.Data;

/// <summary>Данные профиля opencode.ai (лимиты, история трат, платежи). uid = null — локальная область.</summary>
public sealed class ProfileStore(Db db)
{
    private readonly Db _db = db;

    private static void Bind(SqliteCommand cmd, params object?[] values)
    {
        for (var i = 0; i < values.Length; i++)
            cmd.Parameters.AddWithValue("@" + (i + 1), values[i] ?? DBNull.Value);
    }

    private static void Uid(SqliteCommand cmd, string? uid)
        => cmd.Parameters.AddWithValue("@u", (object?)uid ?? DBNull.Value);

    private static void Owner(SqliteCommand cmd, string? uid)
        => cmd.Parameters.AddWithValue("@u", string.IsNullOrWhiteSpace(uid) ? Db.LocalUser : uid);

    private static string? Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    public Task SaveProfileAsync(SiteLimits p, string? uid = null, CancellationToken ct = default)
    {
        using var cn = _db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = """
            insert into site_profile(user_id,fetched_at,email,plan,region,
              rolling_usage,rolling_limit,rolling_pct,rolling_reset_sec,
              weekly_usage,weekly_limit,weekly_pct,weekly_reset_sec,
              monthly_usage,monthly_limit,monthly_pct,monthly_reset_sec,
              balance,use_balance,monthly_spend_limit)
            values(@u,@1,@2,@3,@4,@5,@6,@7,@8,@9,@10,@11,@12,@13,@14,@15,@16,@17,@18,@19)
            on conflict(user_id) do update set fetched_at=excluded.fetched_at, email=excluded.email, plan=excluded.plan,
              region=excluded.region, rolling_usage=excluded.rolling_usage, rolling_limit=excluded.rolling_limit,
              rolling_pct=excluded.rolling_pct, rolling_reset_sec=excluded.rolling_reset_sec,
              weekly_usage=excluded.weekly_usage, weekly_limit=excluded.weekly_limit, weekly_pct=excluded.weekly_pct,
              weekly_reset_sec=excluded.weekly_reset_sec, monthly_usage=excluded.monthly_usage,
              monthly_limit=excluded.monthly_limit, monthly_pct=excluded.monthly_pct,
              monthly_reset_sec=excluded.monthly_reset_sec, balance=excluded.balance,
              use_balance=excluded.use_balance, monthly_spend_limit=excluded.monthly_spend_limit
            """;
        Owner(cmd, uid);
        Bind(cmd, Db.Iso(DateTimeOffset.UtcNow), p.Email, p.Plan, p.Region,
            (double)p.RollingUsage, (double)p.RollingLimit, p.RollingPct, p.RollingResetSec,
            (double)p.WeeklyUsage, (double)p.WeeklyLimit, p.WeeklyPct, p.WeeklyResetSec,
            (double)p.MonthlyUsage, (double)p.MonthlyLimit, p.MonthlyPct, p.MonthlyResetSec,
            (double)p.Balance, p.UseBalance ? 1 : 0, (double)p.MonthlySpendLimit);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task<SiteLimits?> GetProfileAsync(string? uid = null, CancellationToken ct = default)
    {
        using var cn = _db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select * from site_profile where (@u is null or user_id=@u)";
        Uid(cmd, uid);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return Task.FromResult<SiteLimits?>(null);
        double D(string c) { var i = r.GetOrdinal(c); return r.IsDBNull(i) ? 0 : r.GetDouble(i); }
        int I(string c) { var i = r.GetOrdinal(c); return r.IsDBNull(i) ? 0 : (int)r.GetInt64(i); }
        string? S(string c) { var i = r.GetOrdinal(c); return r.IsDBNull(i) ? null : r.GetString(i); }
        var limits = new SiteLimits(
            S("email"), S("plan"), S("region"), I("use_balance") == 1, (decimal)D("balance"), (decimal)D("monthly_spend_limit"),
            (decimal)D("rolling_usage"), (decimal)D("rolling_limit"), (decimal)D("rolling_pct"), I("rolling_reset_sec"),
            (decimal)D("weekly_usage"), (decimal)D("weekly_limit"), (decimal)D("weekly_pct"), I("weekly_reset_sec"),
            (decimal)D("monthly_usage"), (decimal)D("monthly_limit"), (decimal)D("monthly_pct"), I("monthly_reset_sec"),
            Db.Parse(S("fetched_at")));
        return Task.FromResult<SiteLimits?>(limits);
    }

    public Task<int> UpsertUsageAsync(List<SiteUsage> rows, string? uid = null, CancellationToken ct = default)
    {
        if (rows.Count == 0) return Task.FromResult(0);
        using var cn = _db.Open();
        using var tx = cn.BeginTransaction();
        using var cmd = cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            insert into site_usage(user_id,id,time_created,model,provider,plan,input_tokens,output_tokens,reasoning_tokens,cache_read,cost,session_id,key_id)
            values(@u,@1,@2,@3,@4,@5,@6,@7,@8,@9,@10,@11,@12)
            on conflict(user_id,id) do update set cost=excluded.cost, synced_at=datetime('now')
            """;
        for (var i = 0; i < 12; i++) cmd.Parameters.Add(new SqliteParameter { ParameterName = "@" + (i + 1) });
        Owner(cmd, uid);
        foreach (var r in rows)
        {
            cmd.Parameters[0].Value = r.Id;
            cmd.Parameters[1].Value = Db.Iso(r.TimeCreated);
            cmd.Parameters[2].Value = (object?)r.Model ?? DBNull.Value;
            cmd.Parameters[3].Value = (object?)r.Provider ?? DBNull.Value;
            cmd.Parameters[4].Value = (object?)r.Plan ?? DBNull.Value;
            cmd.Parameters[5].Value = r.InputTokens;
            cmd.Parameters[6].Value = r.OutputTokens;
            cmd.Parameters[7].Value = r.ReasoningTokens;
            cmd.Parameters[8].Value = r.CacheRead;
            cmd.Parameters[9].Value = (double)r.Cost;
            cmd.Parameters[10].Value = (object?)r.SessionId ?? DBNull.Value;
            cmd.Parameters[11].Value = (object?)r.KeyId ?? DBNull.Value;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return Task.FromResult(rows.Count);
    }

    /// <summary>Свод: итоги, по дням (в нужном часовом поясе), по моделям, последние записи.</summary>
    public Task<Dictionary<string, object>> SummaryAsync(string tz, string? uid = null, CancellationToken ct = default)
    {
        var tzi = ResolveTz(tz);
        var res = new Dictionary<string, object>();
        using var cn = _db.Open();

        long count = 0, tokens = 0; decimal cost = 0; string? min = null, max = null;
        using (var cmd = cn.CreateCommand())
        {
            cmd.CommandText = """
                select count(*), coalesce(sum(cost),0),
                       coalesce(sum(input_tokens+output_tokens+reasoning_tokens+cache_read),0),
                       min(time_created), max(time_created) from site_usage where (@u is null or user_id=@u)
                """;
            Uid(cmd, uid);
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                count = r.GetInt64(0); cost = (decimal)r.GetDouble(1); tokens = r.GetInt64(2);
                min = Str(r, 3); max = Str(r, 4);
            }
        }
        res["totals"] = new { count, cost, tokens, from = min is null ? (DateTime?)null : Db.Parse(min).UtcDateTime, to = max is null ? (DateTime?)null : Db.Parse(max).UtcDateTime };

        var days = new Dictionary<string, (double cost, long n, long tok)>();
        using (var cmd = cn.CreateCommand())
        {
            cmd.CommandText = "select time_created, cost, input_tokens+output_tokens+reasoning_tokens+cache_read from site_usage where (@u is null or user_id=@u)";
            Uid(cmd, uid);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var local = TimeZoneInfo.ConvertTime(Db.Parse(r.GetString(0)), tzi);
                var d = local.ToString("yyyy-MM-dd");
                var cur = days.TryGetValue(d, out var v) ? v : (0, 0L, 0L);
                days[d] = (cur.Item1 + r.GetDouble(1), cur.Item2 + 1, cur.Item3 + r.GetInt64(2));
            }
        }
        res["byDay"] = days.OrderByDescending(k => k.Key)
            .Select(kv => new { day = kv.Key, cost = (decimal)kv.Value.cost, count = kv.Value.n, tokens = kv.Value.tok }).ToList();

        var models = new List<object>();
        using (var cmd = cn.CreateCommand())
        {
            cmd.CommandText = """
                select coalesce(provider,'?'), coalesce(model,'?'), sum(cost), count(*)
                from site_usage where (@u is null or user_id=@u) group by 1,2 order by 3 desc
                """;
            Uid(cmd, uid);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                models.Add(new { provider = r.GetString(0), model = r.GetString(1), cost = (decimal)r.GetDouble(2), count = r.GetInt64(3) });
        }
        res["byModel"] = models;

        var recent = new List<object>();
        using (var cmd = cn.CreateCommand())
        {
            cmd.CommandText = """
                select id, time_created, model, provider, input_tokens, output_tokens, reasoning_tokens, cache_read, cost, session_id
                from site_usage where (@u is null or user_id=@u) order by time_created desc limit 100
                """;
            Uid(cmd, uid);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                recent.Add(new
                {
                    id = r.GetString(0),
                    time = Db.Parse(r.GetString(1)).UtcDateTime,
                    model = Str(r, 2),
                    provider = Str(r, 3),
                    input = r.GetInt64(4),
                    output = r.GetInt64(5),
                    reasoning = r.GetInt64(6),
                    cacheRead = r.GetInt64(7),
                    cost = (decimal)r.GetDouble(8),
                    sessionId = Str(r, 9),
                });
        }
        res["recent"] = recent;
        return Task.FromResult(res);
    }

    private static TimeZoneInfo ResolveTz(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch { return TimeZoneInfo.Utc; }
    }

    /// <summary>Итоги по периодам: сегодня / неделя / месяц.</summary>
    public Task<(decimal today, decimal week, decimal month, long todayCount, long totalCount)> PeriodAsync(
        DateTimeOffset dayStart, DateTimeOffset weekStart, DateTimeOffset monthStart, string? uid = null, CancellationToken ct = default)
    {
        using var cn = _db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = """
            select
              coalesce(sum(case when time_created >= @1 then cost end),0),
              coalesce(sum(case when time_created >= @2 then cost end),0),
              coalesce(sum(case when time_created >= @3 then cost end),0),
              count(case when time_created >= @1 then 1 end),
              count(*)
            from site_usage where (@u is null or user_id=@u)
            """;
        Uid(cmd, uid); Bind(cmd, Db.Iso(dayStart), Db.Iso(weekStart), Db.Iso(monthStart));
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return Task.FromResult((0m, 0m, 0m, 0L, 0L));
        return Task.FromResult(((decimal)r.GetDouble(0), (decimal)r.GetDouble(1), (decimal)r.GetDouble(2), r.GetInt64(3), r.GetInt64(4)));
    }

    /// <summary>Чаты: реальная стоимость с их стороны, субагенты свёрнуты в родителя.</summary>
    public Task<List<object>> BySessionAsync(int limit = 100, string? uid = null, CancellationToken ct = default)
    {
        var list = new List<object>();
        using var cn = _db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = """
            with recursive tree(id, root) as (
              select id, id from session_meta where parent_id is null and (@u is null or user_id=@u)
              union all
              select s.id, t.root from session_meta s join tree t on s.parent_id = t.id
                where (@u is null or s.user_id=@u)
            ),
            mapped as (
              select u.cost,
                     (u.input_tokens+u.output_tokens+u.reasoning_tokens+u.cache_read) as tok,
                     u.time_created,
                     t.root as root_id,
                     s.parent_id as own_parent,
                     (case when s.parent_id is not null or lower(coalesce(s.title,'')) like '%subagent%' then 1 else 0 end) as is_sub,
                     coalesce(s.id, u.session_id) as sid
              from site_usage u
              left join session_meta s on (s.id = u.session_id or s.id like '%' || u.session_id) and s.user_id = u.user_id
              left join tree t on t.id = s.id
              where u.session_id is not null and (@u is null or u.user_id=@u)
            )
            select coalesce(m.root_id, m.sid) as session_key,
                   coalesce(max(sm.title), '') as title,
                   coalesce(max(sm.agent), '') as agent,
                   sum(m.cost) as cost,
                   sum(m.tok) as tokens,
                   count(*) as steps,
                   max(m.time_created) as last,
                   max(case when m.is_sub = 1 and coalesce(m.root_id,'') = m.sid then 1 else 0 end) as orphan
            from mapped m
            left join session_meta sm on sm.id = m.root_id and (@u is null or sm.user_id=@u)
            group by coalesce(m.root_id, m.sid)
            order by cost desc
            limit @1
            """;
        Uid(cmd, uid); Bind(cmd, limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new
            {
                sessionId = r.GetString(0),
                title = r.GetString(1),
                agent = r.GetString(2),
                cost = (decimal)r.GetDouble(3),
                tokens = r.GetInt64(4),
                steps = r.GetInt64(5),
                last = Db.Parse(r.GetString(6)).UtcDateTime,
                known = r.GetString(1).Length > 0,
                orphan = r.GetInt64(7) == 1,
            });
        return Task.FromResult(list);
    }

    public Task<List<SiteUsageDto>> ExportSinceAsync(DateTimeOffset from, string? uid = null, CancellationToken ct = default)
    {
        var list = new List<SiteUsageDto>();
        using var cn = _db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = """
            select id, time_created, model, provider, input_tokens, output_tokens, reasoning_tokens, cache_read, cost, session_id
            from site_usage where (@u is null or user_id=@u) and time_created >= @1 order by time_created
            """;
        Uid(cmd, uid); Bind(cmd, Db.Iso(from));
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new SiteUsageDto
            {
                Id = r.GetString(0),
                Time = Db.Parse(r.GetString(1)),
                Model = Str(r, 2),
                Provider = Str(r, 3),
                Input = r.GetInt64(4),
                Output = r.GetInt64(5),
                Reasoning = r.GetInt64(6),
                CacheRead = r.GetInt64(7),
                Cost = (decimal)r.GetDouble(8),
                SessionId = Str(r, 9),
            });
        return Task.FromResult(list);
    }

    public Task SavePaymentsAsync(List<SitePayment> rows, string? liteSubId, string? uid = null, CancellationToken ct = default)
    {
        using var cn = _db.Open();
        using var tx = cn.BeginTransaction();
        if (rows.Count > 0)
        {
            using var cmd = cn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                insert into site_payment(user_id,id,paid_at,amount,refunded,receipt_url) values(@u,@1,@2,@3,@4,@5)
                on conflict(user_id,id) do update set paid_at=excluded.paid_at, amount=excluded.amount,
                    refunded=excluded.refunded, synced_at=datetime('now')
                """;
            for (var i = 0; i < 5; i++) cmd.Parameters.Add(new SqliteParameter { ParameterName = "@" + (i + 1) });
            Owner(cmd, uid);
            foreach (var p in rows)
            {
                cmd.Parameters[0].Value = p.Id;
                cmd.Parameters[1].Value = p.PaidAt is null ? DBNull.Value : Db.Iso(p.PaidAt.Value);
                cmd.Parameters[2].Value = (double)p.Amount;
                cmd.Parameters[3].Value = p.Refunded ? 1 : 0;
                cmd.Parameters[4].Value = (object?)p.ReceiptUrl ?? DBNull.Value;
                cmd.ExecuteNonQuery();
            }
        }
        if (!string.IsNullOrWhiteSpace(liteSubId))
        {
            using var upd = cn.CreateCommand();
            upd.Transaction = tx;
            upd.CommandText = "update site_profile set lite_subscription_id=@1 where user_id=@u";
            Owner(upd, uid); Bind(upd, liteSubId);
            upd.ExecuteNonQuery();
        }
        tx.Commit();
        return Task.CompletedTask;
    }

    /// <summary>Платежи в исходном виде — для отправки на сервер.</summary>
    public Task<List<SitePayment>> PaymentRecordsAsync(string? uid = null, CancellationToken ct = default)
    {
        var list = new List<SitePayment>();
        using var cn = _db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select id, paid_at, amount, refunded, receipt_url from site_payment where (@u is null or user_id=@u) order by paid_at desc";
        Uid(cmd, uid);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new SitePayment(r.GetString(0), r.IsDBNull(1) ? null : Db.Parse(r.GetString(1)),
                (decimal)r.GetDouble(2), r.GetInt64(3) == 1, Str(r, 4)));
        return Task.FromResult(list);
    }

    public Task<List<object>> PaymentsAsync(string? uid = null, CancellationToken ct = default)
    {
        var list = new List<object>();
        using var cn = _db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select id, paid_at, amount, refunded from site_payment where (@u is null or user_id=@u) order by paid_at desc";
        Uid(cmd, uid);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new
            {
                id = r.GetString(0),
                paidAt = r.IsDBNull(1) ? (DateTime?)null : Db.Parse(r.GetString(1)).UtcDateTime,
                amount = (decimal)r.GetDouble(2),
                refunded = r.GetInt64(3) == 1,
            });
        return Task.FromResult(list);
    }

    public Task<string?> LiteSubscriptionIdAsync(string? uid = null, CancellationToken ct = default)
    {
        using var cn = _db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select lite_subscription_id from site_profile where (@u is null or user_id=@u)";
        Uid(cmd, uid);
        return Task.FromResult(cmd.ExecuteScalar() as string);
    }
}
