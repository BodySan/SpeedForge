using System.Text.Json;

namespace SpeedForge.Core;

/// <summary>Local address corrections; the published ports and protocol settings stay intact.</summary>
public sealed class ServerOverrides(string directory)
{
    private readonly string _file = Path.Combine(directory, "server-overrides.json");
    private readonly IReadOnlyList<ServerEntry> _published = ServerCatalog.LoadPublished();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public IReadOnlyList<ServerEntry> Load()
    {
        var changes = Read();
        return _published.Select(server => changes.TryGetValue(server.Id, out var host)
            ? WithHost(server, host) : server).ToArray();
    }

    public IReadOnlyList<ServerEntry> Save(string id, string? host)
    {
        var original = _published.SingleOrDefault(x => x.Id == id)
            ?? throw new InvalidDataException("Сервер отсутствует во встроенном каталоге");
        string? normalized = host?.Trim().ToLowerInvariant();
        if (normalized != null && !SecurityPolicy.ValidHost(normalized))
            throw new InvalidDataException("Введите имя сервера или IP без протокола, пути и порта");
        var changes = Read(); // Do not overwrite a malformed file or another instance's corrections.
        if (normalized == null || normalized.Equals(original.Host, StringComparison.OrdinalIgnoreCase)) changes.Remove(id);
        else changes[id] = normalized;
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        string temp = _file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(changes, Json));
            File.Move(temp, _file, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return _published.Select(server => changes.TryGetValue(server.Id, out var address)
            ? WithHost(server, address) : server).ToArray();
    }

    private Dictionary<string, string> Read()
    {
        if (!File.Exists(_file)) return new(StringComparer.Ordinal);
        if (new FileInfo(_file).Length > 64_000) throw new InvalidDataException("Файл адресов серверов слишком велик");
        try
        {
            var changes = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_file))
                ?? throw new InvalidDataException("Файл адресов серверов пуст");
            if (changes.Any(x => string.IsNullOrWhiteSpace(x.Key) || !SecurityPolicy.ValidHost(x.Value)))
                throw new InvalidDataException("Файл содержит неверный адрес сервера");
            return changes;
        }
        catch (JsonException ex) { throw new InvalidDataException("Не удалось прочитать server-overrides.json. Исправьте или переименуйте файл; исходные адреса доступны.", ex); }
    }

    private static ServerEntry WithHost(ServerEntry server, string host)
    {
        if (host.Equals(server.Host, StringComparison.OrdinalIgnoreCase)) return server;
        string Rewrite(string url)
        {
            var uri = new Uri(url);
            return new UriBuilder(uri) { Host = host, Port = uri.Port }.Uri.AbsoluteUri;
        }
        return server with { Host = host, HasLocalOverride = true, Http = server.Http is { } http
            ? http with { DownloadUrl = Rewrite(http.DownloadUrl), UploadUrl = http.UploadUrl == null ? null : Rewrite(http.UploadUrl) }
            : null };
    }
}
