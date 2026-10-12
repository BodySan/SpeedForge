using System.IO.Compression;
using System.Text.Json;

namespace SpeedForge.Core;

public static class BundledIperf
{
    public static void ExportLicenses(string path)
    {
        var assembly = typeof(BundledIperf).Assembly;
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        const string prefix = "SpeedForge.Licenses.";
        foreach (string resource in assembly.GetManifestResourceNames().Where(x => x.StartsWith(prefix, StringComparison.Ordinal)))
        {
            string name = resource[prefix.Length..].Replace('\\', '/');
            var entry = archive.CreateEntry(name);
            using var source = assembly.GetManifestResourceStream(resource)!;
            using var destination = entry.Open(); source.CopyTo(destination);
        }
    }
    public const string ExecutableSha256 = "bcafba4aff825fa94685b6b3d049f8b1338c69e6dc1d06e6a00ac046191e4999";
    public const string Iperf2Sha256 = "1030be63b604147a2a27f783e162237606059de0e7ddb125834328a58e8dc2aa";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public static async Task<ApprovedTool> RequireAsync(CancellationToken ct, EngineKind kind = EngineKind.Iperf3)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var assembly = typeof(BundledIperf).Assembly;
            string name = kind == EngineKind.Iperf2 ? "iperf2" : "iperf3";
            string version = kind == EngineKind.Iperf2 ? "2.2.2" : "3.22";
            using var hashStream = assembly.GetManifestResourceStream($"SpeedForge.Core.Bundled.{name}-hashes.json")
                ?? throw new FileNotFoundException("Отсутствует встроенный iPerf3");
            var hashes = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(hashStream, cancellationToken: ct)
                ?? throw new InvalidDataException("Повреждён манифест iPerf3");
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpeedForge", "tools", name + "-" + version);
            Directory.CreateDirectory(root);
            if (File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint)) throw new UnauthorizedAccessException("Каталог iPerf3 является ссылкой");
            using var zipStream = assembly.GetManifestResourceStream($"SpeedForge.Core.Bundled.{name}.zip")
                ?? throw new FileNotFoundException("Отсутствует архив iPerf3");
            using var zip = new ZipArchive(zipStream);
            foreach (var pair in hashes)
            {
                if (Path.GetFileName(pair.Key) != pair.Key) throw new InvalidDataException("Недопустимое имя компонента iPerf3");
                string path = Path.Combine(root, pair.Key);
                if (!File.Exists(path))
                {
                    var entry = zip.GetEntry(pair.Key) ?? throw new InvalidDataException("Отсутствует компонент iPerf3");
                    await using var input = entry.Open();
                    await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    await input.CopyToAsync(output, ct);
                }
                if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)
                    || !pair.Value.Equals(await TrustedTools.HashAsync(path, ct), StringComparison.OrdinalIgnoreCase))
                    throw new UnauthorizedAccessException($"Встроенный {name} изменён. Повреждённый каталог: {root}");
            }
            var dlls = Directory.GetFiles(root, "*.dll");
            if (dlls.Length != hashes.Count(x => x.Key.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
                throw new UnauthorizedAccessException("В каталоге встроенного iPerf3 появились посторонние DLL");
            return new ApprovedTool(Path.Combine(root, name + ".exe"), hashes[name + ".exe"], kind,
                hashes.Where(x => x.Key.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).ToDictionary(x => x.Key, x => x.Value));
        }
        finally { Gate.Release(); }
    }
}
