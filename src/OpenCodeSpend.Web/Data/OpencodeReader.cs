using Microsoft.Data.Sqlite;

namespace OpenCodeSpend.Data;

/// <summary>Читает локальную opencode.db. Берём только идентификаторы и метрики, без тел сообщений.</summary>
public sealed class OpencodeReader(string dbPath)
{
    private readonly string _dbPath = dbPath;

    private SqliteConnection Open()
    {
        var ro = new SqliteConnectionStringBuilder { DataSource = _dbPath, Mode = SqliteOpenMode.ReadOnly }.ToString();
        try
        {
            var c = new SqliteConnection(ro);
            c.Open();
            return c;
        }
        catch
        {
            var c = new SqliteConnection($"Data Source={_dbPath}");
            c.Open();
            return c;
        }
    }

    public bool Available => File.Exists(_dbPath);

    private const string UsageSql = """
        select m.id                as id,
               m.session_id        as session_id,
               s.parent_id         as parent_id,
               s.project_id        as project_id,
               json_extract(m.data,'$.agent')              as agent,
               json_extract(m.data,'$.mode')               as mode,
               json_extract(m.data,'$.providerID')         as provider,
               json_extract(m.data,'$.modelID')            as model,
               json_extract(m.data,'$.variant')            as variant,
               json_extract(m.data,'$.time.created')       as created,
               coalesce(json_extract(m.data,'$.cost'),0)   as cost,
               coalesce(json_extract(m.data,'$.tokens.input'),0)       as tin,
               coalesce(json_extract(m.data,'$.tokens.output'),0)      as tout,
               coalesce(json_extract(m.data,'$.tokens.reasoning'),0)   as trea,
               coalesce(json_extract(m.data,'$.tokens.cache.read'),0)  as cr,
               coalesce(json_extract(m.data,'$.tokens.cache.write'),0) as cw
        from message m
        left join session s on s.id = m.session_id
        where json_extract(m.data,'$.role') = 'assistant'
          and json_extract(m.data,'$.time.created') > $since
        order by created
        """;

    public List<UsageRecord> ReadUsage(long sinceMs)
    {
        var list = new List<UsageRecord>();
        using var cn = Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = UsageSql;
        cmd.Parameters.AddWithValue("$since", sinceMs);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new UsageRecord(
                r.GetString(0),
                r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5),
                r.IsDBNull(6) ? null : r.GetString(6),
                r.IsDBNull(7) ? null : r.GetString(7),
                r.IsDBNull(8) ? null : r.GetString(8),
                DateTimeOffset.FromUnixTimeMilliseconds((long)r.GetDouble(9)),
                (decimal)r.GetDouble(10),
                (long)r.GetDouble(11),
                (long)r.GetDouble(12),
                (long)r.GetDouble(13),
                (long)r.GetDouble(14),
                (long)r.GetDouble(15)));
        }
        return list;
    }

    private const string SessionSql = """
        select id, parent_id, project_id, agent, title,
               json_extract(model,'$.providerID') as provider,
               json_extract(model,'$.id')         as model,
               time_created, time_updated, cost,
               tokens_input, tokens_output, tokens_reasoning, tokens_cache_read
        from session
        where time_updated > $since
        """;

    public List<SessionRecord> ReadSessions(long sinceMs)
    {
        var list = new List<SessionRecord>();
        using var cn = Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = SessionSql;
        cmd.Parameters.AddWithValue("$since", sinceMs);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new SessionRecord(
                r.GetString(0),
                r.IsDBNull(1) ? null : r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5),
                r.IsDBNull(6) ? null : r.GetString(6),
                DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(7)),
                DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(8)),
                (decimal)r.GetDouble(9),
                r.GetInt64(10), r.GetInt64(11), r.GetInt64(12), r.GetInt64(13)));
        }
        return list;
    }
}
