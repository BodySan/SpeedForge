using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SpeedForge.Core;

public static partial class Redaction
{
    [GeneratedRegex(@"(?<![\d.])(?:\d{1,3}\.){3}\d{1,3}(?![\d.])")]
    private static partial Regex IPv4();
    [GeneratedRegex(@"(?<![\w:])(?:[0-9a-fA-F]{0,4}:){2,}[0-9a-fA-F:%.]*(?![\w:])")]
    private static partial Regex IPv6();
    public static string Mask(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var cleaned = IPv4().Replace(value, "X.***.***.***");
        cleaned = IPv6().Replace(cleaned, "[IPv6 скрыт]");
        return cleaned.Replace('\r', ' ').Replace('\n', ' ');
    }
}

public sealed class RunStore
{
    private readonly string _file;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };
    public string DirectoryPath { get; }

    public RunStore(string? directory = null)
    {
        DirectoryPath = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpeedForge");
        Directory.CreateDirectory(DirectoryPath);
        _file = Path.Combine(DirectoryPath, "runs.jsonl");
    }

    public async Task AppendAsync(RunResult item, CancellationToken ct)
    {
        var line = JsonSerializer.Serialize(item, Json);
        if (line.Length > 250_000) throw new InvalidDataException("Запись измерения превышает максимальный размер");
        await _gate.WaitAsync(ct);
        try
        {
            // Rotate oversized history rather than indefinitely consuming disk space.
            if (File.Exists(_file) && new FileInfo(_file).Length > 20_000_000)
                File.Move(_file, _file + ".previous", true);
            await using var s = new FileStream(_file, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous);
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            await s.WriteAsync(bytes, ct);
            await s.FlushAsync(ct);
        }
        finally { _gate.Release(); }
    }
    public IReadOnlyList<RunResult> Recent(int take = 100)
    {
        take = Math.Clamp(take, 1, 500);
        if (!File.Exists(_file)) return [];
        if (new FileInfo(_file).Length > 25_000_000)
            throw new InvalidDataException("Файл истории слишком велик; требуется разбор и восстановление");
        // Streaming read; avoids materializing an unbounded history file.
        var tail = new Queue<RunResult>(take);
        using var stream = new FileStream(_file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length > 250_000) continue;
            try
            {
                var item = JsonSerializer.Deserialize<RunResult>(line, Json);
                if (item == null) continue;
                if (tail.Count >= take) tail.Dequeue();
                tail.Enqueue(item);
            }
            catch (JsonException) { /* A crash can leave an incomplete last line: skip. */ }
        }
        return tail.Reverse().ToArray();
    }
}

public static class ReportExport
{
    public static string TechnicalReport(RunResult r)
    {
        // Full report may contain IP addresses. Export only by explicit selection.
        return JsonSerializer.Serialize(r, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true, Converters = { new JsonStringEnumConverter() } });
    }
}
