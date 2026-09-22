using Microsoft.Data.Sqlite;

namespace OpenCodeSpend.Data;

/// <summary>Хранилище — один файл SQLite. Никаких серверов БД, портов и паролей.
/// Схема v2: все данные принадлежат пользователю (user_id), изоляция на уровне запросов.</summary>
public sealed class Db(string path)
{
    private readonly string _path = path;

    public string Path => _path;

    /// <summary>Владелец локальных (не привязанных к аккаунту) данных — машина-сборщик.</summary>
    public const string LocalUser = "local";

    public const int SchemaVersion = 2;

    public SqliteConnection Open()
    {
        var dir = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var cn = new SqliteConnection($"Data Source={_path}");
        cn.Open();
        using (var pragma = cn.CreateCommand())
        {
            pragma.CommandText = "pragma journal_mode=WAL; pragma synchronous=NORMAL; pragma busy_timeout=5000; pragma foreign_keys=ON;";
            pragma.ExecuteNonQuery();
        }
        return cn;
    }

    public const string Schema = """
        create table if not exists meta(key text primary key, value text);

        create table if not exists usage_record(
          user_id text not null, id text not null, session_id text not null, parent_session_id text, project_id text,
          agent text, mode text, provider_id text, model_id text, variant text,
          ts text not null, cost real not null default 0,
          input_tokens integer not null default 0, output_tokens integer not null default 0,
          reasoning_tokens integer not null default 0, cache_read integer not null default 0,
          cache_write integer not null default 0, primary key(user_id, id));
        create index if not exists ix_usage_ts on usage_record(user_id, ts);
        create index if not exists ix_usage_session on usage_record(user_id, session_id);

        create table if not exists session_meta(
          user_id text not null, id text not null, parent_id text, project_id text, agent text, title text,
          provider_id text, model_id text, created text, updated text,
          cost real not null default 0, input_tokens integer not null default 0,
          output_tokens integer not null default 0, reasoning_tokens integer not null default 0,
          cache_read integer not null default 0, primary key(user_id, id));
        create index if not exists ix_sess_updated on session_meta(user_id, updated);
        create index if not exists ix_sess_parent on session_meta(user_id, parent_id);

        create table if not exists sync_state(
          user_id text not null, key text not null, value text, updated text default (datetime('now')),
          primary key(user_id, key));

        create table if not exists app_user(
          id text primary key, email text unique, name text, picture text, provider text,
          created text default (datetime('now')));

        create table if not exists device(
          id text primary key, user_id text not null, name text, token_hash text unique,
          created text default (datetime('now')), last_seen text default (datetime('now')));
        create index if not exists ix_device_user on device(user_id);

        create table if not exists pairing(
          code text primary key, user_id text not null, name text,
          created text default (datetime('now')), expires text not null);

        create table if not exists session(
          token_hash text primary key, user_id text not null,
          created text default (datetime('now')), expires text not null,
          last_seen text default (datetime('now')), ua text, ip text);
        create index if not exists ix_session_user on session(user_id);

        create table if not exists site_profile(
          user_id text primary key, fetched_at text,
          email text, plan text, region text,
          rolling_usage real, rolling_limit real, rolling_pct real, rolling_reset_sec integer,
          weekly_usage real, weekly_limit real, weekly_pct real, weekly_reset_sec integer,
          monthly_usage real, monthly_limit real, monthly_pct real, monthly_reset_sec integer,
          balance real, use_balance integer, monthly_spend_limit real, lite_subscription_id text);

        create table if not exists site_usage(
          user_id text not null, id text not null, time_created text not null, model text, provider text, plan text,
          input_tokens integer not null default 0, output_tokens integer not null default 0,
          reasoning_tokens integer not null default 0, cache_read integer not null default 0,
          cost real not null default 0, session_id text, key_id text,
          synced_at text default (datetime('now')), primary key(user_id, id));
        create index if not exists ix_site_usage_time on site_usage(user_id, time_created);

        create table if not exists site_payment(
          user_id text not null, id text not null, paid_at text, amount real not null default 0,
          refunded integer not null default 0, receipt_url text,
          synced_at text default (datetime('now')), primary key(user_id, id));

        create table if not exists control(
          user_id text not null, key text not null, value text, updated text default (datetime('now')),
          primary key(user_id, key));

        create table if not exists zen_usage(
          user_id text not null, period text not null, scope text not null,
          fetched_at text default (datetime('now')), row text not null);
        create index if not exists ix_zen on zen_usage(user_id, period, fetched_at);
        """;

    public void EnsureSchema()
    {
        using var cn = Open();
        Exec(cn, "create table if not exists meta(key text primary key, value text);");
        if (ReadVersion(cn) < 2) MigrateToV2(cn);
        Exec(cn, Schema);
        EnsureColumn(cn, "app_user", "provider", "alter table app_user add column provider text");
        EnsureColumn(cn, "app_user", "login_code_hash", "alter table app_user add column login_code_hash text");
        EnsureColumn(cn, "device", "machine_id", "alter table device add column machine_id text");
        WriteVersion(cn, SchemaVersion);
    }

    /// <summary>Добавляет колонку, если её ещё нет (для баз, созданных более ранней версией).</summary>
    private static void EnsureColumn(SqliteConnection cn, string table, string column, string ddl)
    {
        try
        {
            using var check = cn.CreateCommand();
            check.CommandText = $"select count(*) from pragma_table_info('{table}') where name=@1";
            check.Parameters.AddWithValue("@1", column);
            if (Convert.ToInt32(check.ExecuteScalar()) > 0) return;
            Exec(cn, ddl);
        }
        catch (SqliteException) { }
    }

    // ---------- миграция v1 (однопользовательская) -> v2 ----------

    private static readonly string[] LegacyTables =
    {
        "usage_record", "session_meta", "sync_state", "site_profile", "site_usage", "site_payment", "control", "device",
    };

    private static void MigrateToV2(SqliteConnection cn)
    {
        var moved = new List<string>();
        foreach (var t in LegacyTables)
        {
            if (!TableExists(cn, t) || TableExists(cn, t + "_v1")) continue;
            Exec(cn, $"alter table {t} rename to {t}_v1");
            moved.Add(t);
        }
        // zen_usage — только кэш, пересоздаём
        Exec(cn, "drop table if exists zen_usage");

        if (moved.Count == 0) return;

        Exec(cn, Schema);
        foreach (var t in moved) CopyLegacy(cn, t);
        foreach (var t in moved) Exec(cn, $"drop table if exists {t}_v1");
    }

    private static void CopyLegacy(SqliteConnection cn, string t)
    {
        const string owner = "coalesce(nullif(user_id,''),'local')";
        var sql = t switch
        {
            "usage_record" => $"""
                insert or ignore into usage_record(user_id,id,session_id,parent_session_id,project_id,agent,mode,provider_id,model_id,variant,ts,cost,input_tokens,output_tokens,reasoning_tokens,cache_read,cache_write)
                select {owner},id,session_id,parent_session_id,project_id,agent,mode,provider_id,model_id,variant,ts,cost,input_tokens,output_tokens,reasoning_tokens,cache_read,cache_write from usage_record_v1
                """,
            "session_meta" => $"""
                insert or ignore into session_meta(user_id,id,parent_id,project_id,agent,title,provider_id,model_id,created,updated,cost,input_tokens,output_tokens,reasoning_tokens,cache_read)
                select {owner},id,parent_id,project_id,agent,title,provider_id,model_id,created,updated,cost,input_tokens,output_tokens,reasoning_tokens,cache_read from session_meta_v1
                """,
            "sync_state" => $"""
                insert or ignore into sync_state(user_id,key,value,updated)
                select {owner},key,value,updated from sync_state_v1
                """,
            "control" => """
                insert or ignore into control(user_id,key,value,updated)
                select 'local',key,value,updated from control_v1
                """,
            "site_profile" => """
                insert or ignore into site_profile(user_id,fetched_at,email,plan,region,rolling_usage,rolling_limit,rolling_pct,rolling_reset_sec,
                    weekly_usage,weekly_limit,weekly_pct,weekly_reset_sec,monthly_usage,monthly_limit,monthly_pct,monthly_reset_sec,
                    balance,use_balance,monthly_spend_limit,lite_subscription_id)
                select 'local',fetched_at,email,plan,region,rolling_usage,rolling_limit,rolling_pct,rolling_reset_sec,
                    weekly_usage,weekly_limit,weekly_pct,weekly_reset_sec,monthly_usage,monthly_limit,monthly_pct,monthly_reset_sec,
                    balance,use_balance,monthly_spend_limit,lite_subscription_id from site_profile_v1
                """,
            "site_usage" => $"""
                insert or ignore into site_usage(user_id,id,time_created,model,provider,plan,input_tokens,output_tokens,reasoning_tokens,cache_read,cost,session_id,key_id,synced_at)
                select {owner},id,time_created,model,provider,plan,input_tokens,output_tokens,reasoning_tokens,cache_read,cost,session_id,key_id,synced_at from site_usage_v1
                """,
            "site_payment" => $"""
                insert or ignore into site_payment(user_id,id,paid_at,amount,refunded,receipt_url,synced_at)
                select {owner},id,paid_at,amount,refunded,receipt_url,synced_at from site_payment_v1
                """,
            "device" => $"""
                insert or ignore into device(id,user_id,name,created,last_seen)
                select id,{owner},name,created,last_seen from device_v1
                """,
            _ => "",
        };
        if (sql.Length == 0) return;
        try { Exec(cn, sql); } catch (SqliteException) { /* несовместимая старая таблица — пропускаем */ }
    }

    private static bool TableExists(SqliteConnection cn, string name)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select 1 from sqlite_master where type='table' and name=@1";
        cmd.Parameters.AddWithValue("@1", name);
        return cmd.ExecuteScalar() is not null;
    }

    private static int ReadVersion(SqliteConnection cn)
    {
        try
        {
            using var cmd = cn.CreateCommand();
            cmd.CommandText = "select value from meta where key='schema_version'";
            return int.TryParse(cmd.ExecuteScalar() as string, out var v) ? v : 0;
        }
        catch (SqliteException) { return 0; }
    }

    private static void WriteVersion(SqliteConnection cn, int v) => Exec(cn,
        $"insert into meta(key,value) values('schema_version','{v}') on conflict(key) do update set value='{v}'");

    private static void Exec(SqliteConnection cn, string sql)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    public static DateTimeOffset Parse(string? s)
        => DateTimeOffset.TryParse(s, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var t)
            ? t : DateTimeOffset.UnixEpoch;
}
