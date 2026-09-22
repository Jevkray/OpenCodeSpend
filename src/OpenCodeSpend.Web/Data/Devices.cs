using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace OpenCodeSpend.Data;

/// <summary>
/// Устройства-сборщики. Каждое принадлежит пользователю; в базе хранится только SHA-256 токена.
/// Подключение нового компьютера — по короткому одноразовому коду из веб-панели.
/// </summary>
public sealed class DeviceService(Db db, SpendConfig config)
{
    private readonly Db _db = db;

    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    private TimeSpan PairingTtl => TimeSpan.FromMinutes(Math.Max(1, config.PairingTtlMinutes));

    private static string Hash(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static string NewToken()
        => "ocsd_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace("+", "").Replace("/", "").Replace("=", "");

    private static string NewCode()
    {
        var sb = new StringBuilder(8);
        for (var i = 0; i < 8; i++) sb.Append(Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]);
        return sb.ToString();
    }

    private static void Bind(SqliteCommand cmd, params object?[] values)
    {
        for (var i = 0; i < values.Length; i++)
            cmd.Parameters.AddWithValue("@" + (i + 1), values[i] ?? DBNull.Value);
    }

    private static string? Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    /// <summary>Выдаёт новый токен устройству пользователя (показывается один раз).</summary>
    public (string Token, Device Device) Create(string userId, string? name, string? machineId = null)
    {
        var token = NewToken();
        var id = "dev_" + Guid.NewGuid().ToString("N");
        using var cn = _db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "insert into device(id,user_id,name,token_hash,machine_id) values(@1,@2,@3,@4,@5)";
        Bind(cmd, id, userId, string.IsNullOrWhiteSpace(name) ? Environment.MachineName : name!.Trim(), Hash(token), machineId);
        cmd.ExecuteNonQuery();
        return (token, new Device(id, userId, name, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
    }

    /// <summary>Кто владелец токена. null — токен неизвестен.</summary>
    public (string UserId, string DeviceId)? Resolve(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        // legacy: общий токен из конфигурации — владелец = первый зарегистрированный пользователь
        if (!string.IsNullOrWhiteSpace(config.DeviceToken) && token == config.DeviceToken)
        {
            var owner = FirstUser();
            return (owner, "legacy");
        }

        using var cn = _db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select id, user_id from device where token_hash=@1";
        Bind(cmd, Hash(token));
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return (r.GetString(1), r.GetString(0));
    }

    private string FirstUser()
    {
        using var cn = _db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select id from app_user order by created limit 1";
        return cmd.ExecuteScalar() as string ?? Db.LocalUser;
    }

    public void Touch(string deviceId)
    {
        if (deviceId == "legacy") return;
        using var cn = _db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "update device set last_seen=datetime('now') where id=@1";
        Bind(cmd, deviceId);
        cmd.ExecuteNonQuery();
    }

    public List<Device> List(string userId)
    {
        var list = new List<Device>();
        using var cn = _db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "select id,user_id,name,created,last_seen from device where user_id=@1 order by last_seen desc";
        Bind(cmd, userId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new Device(r.GetString(0), r.GetString(1), Str(r, 2), Db.Parse(r.GetString(3)), Db.Parse(r.GetString(4))));
        return list;
    }

    public bool Revoke(string userId, string deviceId)
    {
        using var cn = _db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "delete from device where id=@1 and user_id=@2";
        Bind(cmd, deviceId, userId);
        return cmd.ExecuteNonQuery() > 0;
    }

    // ---------- спаривание ----------

    /// <summary>Пригласительный код для подключения нового компьютера (живёт 5 минут, одноразовый).
    /// Внутри зашифрован адрес сервера, чтобы приложение знало, куда обращаться.</summary>
    public (string Code, DateTimeOffset Expires) CreatePairing(string userId, string serverUrl)
    {
        using var cn = _db.Open();
        using (var clean = cn.CreateCommand())
        {
            clean.CommandText = "delete from pairing where expires < @1";
            Bind(clean, Db.Iso(DateTimeOffset.UtcNow));
            clean.ExecuteNonQuery();
        }
        var secret = NewCode();
        var expires = DateTimeOffset.UtcNow.Add(PairingTtl);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "insert into pairing(code,user_id,expires) values(@1,@2,@3)";
        Bind(cmd, secret, userId, Db.Iso(expires));
        cmd.ExecuteNonQuery();
        return (InviteCode.Encode(serverUrl, secret), expires);
    }

    /// <summary>Обменивает пригласительный код на токен устройства. Код сгорает.</summary>
    public (string Token, string DeviceId, string UserId)? Claim(string inviteCode, string? name, string? machineId = null)
    {
        var decoded = InviteCode.Decode(inviteCode);
        if (decoded is null) return null;
        var secret = decoded.Value.Secret;

        using var cn = _db.Open();
        string? userId;
        using (var cmd = cn.CreateCommand())
        {
            cmd.CommandText = "select user_id from pairing where code=@1 and expires > @2";
            Bind(cmd, secret, Db.Iso(DateTimeOffset.UtcNow));
            userId = cmd.ExecuteScalar() as string;
        }
        if (userId is null) return null;

        using (var del = cn.CreateCommand())
        {
            del.CommandText = "delete from pairing where code=@1";
            Bind(del, secret);
            del.ExecuteNonQuery();
        }
        // этот же ПК уходит из любого другого аккаунта
        if (!string.IsNullOrWhiteSpace(machineId))
        {
            using var delMachine = cn.CreateCommand();
            delMachine.CommandText = "delete from device where machine_id=@1";
            Bind(delMachine, machineId);
            delMachine.ExecuteNonQuery();
        }
        // один аккаунт — один компьютер: новый ПК заменяет прежний
        using (var delAcc = cn.CreateCommand())
        {
            delAcc.CommandText = "delete from device where user_id=@1";
            Bind(delAcc, userId);
            delAcc.ExecuteNonQuery();
        }
        var (token, device) = Create(userId, name, machineId);
        return (token, device.Id, userId);
    }
}
