using System.Security.Cryptography;
using System.Text.Json;

namespace SpeedForge.Core;

public sealed record ApprovedTool(string Path, string Sha256, EngineKind Engine, Dictionary<string, string> CompanionDlls);

public sealed class TrustedTools
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public TrustedTools(string? directory = null)
    {
        var root = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpeedForge");
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "trusted-tools.json");
    }

    private List<ApprovedTool> Read()
    {
        if (!File.Exists(_path)) return [];
        try { return JsonSerializer.Deserialize<List<ApprovedTool>>(File.ReadAllText(_path), _json) ?? []; }
        catch (JsonException) { throw new InvalidDataException("Реестр доверенных файлов повреждён"); }
    }

    public async Task<ApprovedTool> ApproveSelectedFileAsync(string path, EngineKind kind, CancellationToken ct = default)
    {
        if (kind == EngineKind.Http) throw new ArgumentException("HTTP работает без внешнего исполняемого файла");
        string full = CheckPath(path);
        string hash = await HashAsync(full, ct);
        await _gate.WaitAsync(ct);
        try
        {
            var entries = Read();
            entries.RemoveAll(x => x.Engine == kind);
            var companions = await SnapshotDllsAsync(full, ct);
            var record = new ApprovedTool(full, hash, kind, companions);
            entries.Add(record);
            var tmp = _path + ".tmp";
            await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(entries, _json), ct);
            File.Move(tmp, _path, true);
            return record;
        }
        finally { _gate.Release(); }
    }

    public async Task<ApprovedTool> RequireVerifiedAsync(EngineKind kind, CancellationToken ct, bool legacyIperf2 = false)
    {
        await _gate.WaitAsync(ct);
        ApprovedTool? rec;
        try { rec = Read().FirstOrDefault(x => x.Engine == kind); }
        finally { _gate.Release(); }
        if (rec == null && kind is EngineKind.Iperf3 or EngineKind.Iperf2) return await BundledIperf.RequireAsync(ct, kind, legacyIperf2);
        if (rec == null) throw new FileNotFoundException($"Исполняемый модуль {kind} ещё не выбран и не одобрен пользователем");
        var full = CheckPath(rec.Path);
        var hash = await HashAsync(full, ct);
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(hash), Convert.FromHexString(rec.Sha256)))
            throw new UnauthorizedAccessException($"Изменился SHA-256 модуля {kind}; повторно подтвердите файл");
        var present = await SnapshotDllsAsync(full, ct);
        var expected = rec.CompanionDlls ?? new Dictionary<string, string>();
        if (expected.Count != present.Count || expected.Any(pair =>
            !present.TryGetValue(pair.Key, out var now) ||
            !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(pair.Value), Convert.FromHexString(now))))
            throw new UnauthorizedAccessException("DLL в каталоге измерителя изменены или добавлены; требуется повторное подтверждение");
        return rec with { Path = full };
    }

    public static string CheckPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new UnauthorizedAccessException("Допускается только абсолютный локальный путь");
        var full = Path.GetFullPath(path);
        if (!OperatingSystem.IsWindows() || !full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Нужен локальный Windows .exe");
        if (full.StartsWith(@"\\", StringComparison.Ordinal) || full.StartsWith(@"\\?\", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Сетевые и служебные пути не допускаются");
        if (full.Length > 3 && full.AsSpan(2).Contains(':'))
            throw new UnauthorizedAccessException("Альтернативные файловые потоки запрещены");
        if (!File.Exists(full)) throw new FileNotFoundException("Не найден исполняемый файл", full);
        if (File.GetAttributes(full).HasFlag(FileAttributes.ReparsePoint))
            throw new UnauthorizedAccessException("Символические ссылки исполняемых файлов запрещены");
        return full;
    }
    private static async Task<Dictionary<string, string>> SnapshotDllsAsync(string executable, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(executable)!;
        var dllFiles = Directory.GetFiles(dir, "*.dll", SearchOption.TopDirectoryOnly);
        if (dllFiles.Length > 100) throw new UnauthorizedAccessException("Слишком много библиотек в каталоге измерителя");
        var results = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dll in dllFiles)
        {
            if (File.GetAttributes(dll).HasFlag(FileAttributes.ReparsePoint))
                throw new UnauthorizedAccessException("Символическая ссылка DLL запрещена");
            if (new FileInfo(dll).Length > 150_000_000)
                throw new UnauthorizedAccessException("Слишком большая DLL рядом с измерителем");
            results[Path.GetFileName(dll)] = await HashAsync(dll, ct);
        }
        return results;
    }
    public static async Task<string> HashAsync(string file, CancellationToken ct)
    {
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexStringLower(hash);
    }
}
