using System.Reflection;
using System.Text.Json;

namespace SpeedForge.Core;

public static class ServerCatalog
{
    public const string CatalogSource = "https://github.com/itdoginfo/russian-iperf3-servers";
    public static IReadOnlyList<ServerEntry> LoadPublished()
    {
        using var s = typeof(ServerCatalog).Assembly.GetManifestResourceStream("SpeedForge.Core.servers.json")
            ?? throw new InvalidDataException("Отсутствует встроенный каталог серверов");
        var servers = JsonSerializer.Deserialize<List<ServerEntry>>(s, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
        if (servers.Any(s => s.Port < 1 || s.Port > 65535 || string.IsNullOrWhiteSpace(s.Host)))
            throw new InvalidDataException("Каталог повреждён");
        return servers;
    }
    public static ServerEntry Custom(string host, int port, EngineKind engine, string region = "Пользовательская цель") =>
        new("custom-" + Guid.NewGuid().ToString("N"), region, "Пользовательская", "Пользовательский сервер", "Не указан",
            host.Trim(), port, engine, "Введён пользователем", "Наличие и права использования не подтверждены", [], [],
            "Пользователь несёт ответственность за право создавать нагрузку");
}
