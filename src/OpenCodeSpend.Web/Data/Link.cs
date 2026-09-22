using System.Text.Json;

namespace OpenCodeSpend.Data;

/// <summary>Привязка этого компьютера к серверу: адрес и персональный ключ устройства.
/// Единственное, что хранится локально.</summary>
public sealed class Link
{
    public string? ServerUrl { get; set; }
    public string? Token { get; set; }
    public string? DeviceId { get; set; }

    // данные аккаунта с сервера — чтобы показывать аватар и имя
    public string? Email { get; set; }
    public string? Name { get; set; }
    public string? Picture { get; set; }
}

public sealed class LinkStore(SpendConfig config)
{
    private readonly string _path = config.LinkPath;

    /// <summary>Стабильный идентификатор этой машины: создаётся один раз и хранится на диске.</summary>
    public static string MachineId()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenCodeSpend", "machine-id.txt");
        try
        {
            if (File.Exists(path))
            {
                var s = File.ReadAllText(path).Trim();
                if (s.Length > 0) return s;
            }
            var id = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, id);
            return id;
        }
        catch { return Environment.MachineName; }
    }

    public Link? Get()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var link = JsonSerializer.Deserialize<Link>(File.ReadAllText(_path));
            return string.IsNullOrWhiteSpace(link?.ServerUrl) || string.IsNullOrWhiteSpace(link?.Token) ? null : link;
        }
        catch { return null; }
    }

    public void Save(Link link)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(_path, JsonSerializer.Serialize(link, new JsonSerializerOptions { WriteIndented = true }));
    }

    public void Clear()
    {
        try { if (File.Exists(_path)) File.Delete(_path); } catch { }
    }
}
