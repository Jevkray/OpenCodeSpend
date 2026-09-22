using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace OpenCodeSpend.Data;

/// <summary>Серверные сессии: в cookie только случайный токен, в базе — его хеш.
/// Сессию можно отозвать (выход), она продлевается при активности.</summary>
public sealed class SessionStore(Db db)
{
    private readonly Db _db = db;

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
    private static string NewToken() => "ocss_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace("+", "").Replace("/", "").Replace("=", "");

    private static void Bind(SqliteCommand cmd, params object?[] values)
    {
        for (var i = 0; i < values.Length; i++)
            cmd.Parameters.AddWithValue("@" + (i + 1), values[i] ?? DBNull.Value);
    }

    /// <summary>Создаёт сессию, возвращает токен (показывается один раз — кладём в cookie).</summary>
    public string Create(string userId, int days, string? ua, string? ip)
    {
        var token = NewToken();
        var now = DateTimeOffset.UtcNow;
        using var cn = _db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "insert into session(token_hash,user_id,created,expires,last_seen,ua,ip) values(@1,@2,@3,@4,@5,@6,@7)";
        Bind(cmd, Hash(token), userId, Db.Iso(now), Db.Iso(now.AddDays(days)), Db.Iso(now), ua, ip);
        cmd.ExecuteNonQuery();
        return token;
    }

    /// <summary>Ищет владельца токена и продлевает сессию. null — токена нет или истёк.</summary>
    public string? Resolve(string? token, int days)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var hash = Hash(token);
        using var cn = _db.Open();
        string? userId;
        using (var cmd = cn.CreateCommand())
        {
            cmd.CommandText = "select user_id from session where token_hash=@1 and expires > @2";
            Bind(cmd, hash, Db.Iso(DateTimeOffset.UtcNow));
            userId = cmd.ExecuteScalar() as string;
        }
        if (userId is null)
        {
            using var del = cn.CreateCommand();
            del.CommandText = "delete from session where token_hash=@1";
            Bind(del, hash);
            del.ExecuteNonQuery();
            return null;
        }
        var now = DateTimeOffset.UtcNow;
        using (var up = cn.CreateCommand())
        {
            up.CommandText = "update session set last_seen=@1, expires=@2 where token_hash=@3";
            Bind(up, Db.Iso(now), Db.Iso(now.AddDays(days)), hash);
            up.ExecuteNonQuery();
        }
        return userId;
    }

    public void Delete(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return;
        using var cn = _db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "delete from session where token_hash=@1";
        Bind(cmd, Hash(token));
        cmd.ExecuteNonQuery();
    }

    public void DeleteAll(string userId)
    {
        using var cn = _db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "delete from session where user_id=@1";
        Bind(cmd, userId);
        cmd.ExecuteNonQuery();
    }
}
