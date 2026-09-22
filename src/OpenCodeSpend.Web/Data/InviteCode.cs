using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenCodeSpend.Data;

/// <summary>
/// Пригласительный код для подключения компьютера.
/// Внутри зашифрованы адрес сервера и одноразовый секрет — снаружи адрес не читается.
/// Ключ одинаков у сервера и у приложения (общий код), это маскировка, а не защита секрета.
/// </summary>
public static class InviteCode
{
    private const string Prefix = "OCSP1.";
    private static readonly byte[] Key = SHA256.HashData(Encoding.UTF8.GetBytes("opencode-spend/invite/v1"));

    private sealed record Payload(string u, string c);

    public static string Encode(string serverUrl, string secret)
    {
        var plain = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Payload(serverUrl.TrimEnd('/'), secret)));
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(Key, 16)) aes.Encrypt(nonce, plain, cipher, tag);

        var all = new byte[nonce.Length + cipher.Length + tag.Length];
        Buffer.BlockCopy(nonce, 0, all, 0, nonce.Length);
        Buffer.BlockCopy(cipher, 0, all, nonce.Length, cipher.Length);
        Buffer.BlockCopy(tag, 0, all, nonce.Length + cipher.Length, tag.Length);
        return Prefix + Convert.ToBase64String(all).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static (string Url, string Secret)? Decode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var raw = code.Trim().Replace(" ", "").Replace("\r", "").Replace("\n", "");
        if (!raw.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var body = raw[Prefix.Length..].Replace('-', '+').Replace('_', '/');
        switch (body.Length % 4)
        {
            case 2: body += "=="; break;
            case 3: body += "="; break;
        }
        try
        {
            var all = Convert.FromBase64String(body);
            if (all.Length < 12 + 16 + 1) return null;
            var nonce = all.AsSpan(0, 12);
            var tag = all.AsSpan(all.Length - 16, 16);
            var cipher = all.AsSpan(12, all.Length - 12 - 16);
            var plain = new byte[cipher.Length];
            using (var aes = new AesGcm(Key, 16)) aes.Decrypt(nonce, cipher, tag, plain);
            var payload = JsonSerializer.Deserialize<Payload>(Encoding.UTF8.GetString(plain));
            if (payload is null || string.IsNullOrWhiteSpace(payload.u) || string.IsNullOrWhiteSpace(payload.c)) return null;
            return (payload.u, payload.c);
        }
        catch { return null; }
    }
}
