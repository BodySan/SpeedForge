using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;

namespace SpeedForge.Core;

public sealed class RunDiagnostics
{
    private readonly object _gate = new();
    private readonly List<object> _events = [];
    private readonly List<object> _processes = [];
    private int _omitted;
    private bool _diskFailed;
    public string DirectoryPath { get; }
    public bool DiskAvailable => !_diskFailed;
    public RunDiagnostics(string root, string runId)
    {
        DirectoryPath = Path.Combine(root, "debug", runId);
        try { Directory.CreateDirectory(DirectoryPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _diskFailed = true; }
    }
    public void Event(string category, string message)
    {
        lock (_gate)
        {
            if (_events.Count >= 2000) { _omitted++; return; }
            _events.Add(new { Time = DateTimeOffset.Now, Category = category, Message = message.Length > 8000 ? message[..8000] : message });
        }
    }
    public void Process(string executable, string[] args, DateTimeOffset started, ProcessOutcome outcome)
    {
        lock (_gate)
        {
            int index = _processes.Count + 1;
            string stdout = $"process-{index:D2}-stdout.txt", stderr = $"process-{index:D2}-stderr.txt";
            _processes.Add(new { Started = started, Finished = DateTimeOffset.Now, Executable = executable,
                Arguments = args, outcome.ExitCode, outcome.TimedOut, outcome.Cancelled,
                outcome.StdoutTruncated, outcome.StderrTruncated, Stdout = stdout, Stderr = stderr });
            Write(stdout, outcome.Stdout); Write(stderr, outcome.Stderr);
        }
    }
    public void Finish(RunResult run)
    {
        lock (_gate)
        {
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            Write("result.json", JsonSerializer.Serialize(run, options));
            Write("trace.json", JsonSerializer.Serialize(new { Schema = 1, AppVersion = typeof(RunDiagnostics).Assembly.GetName().Version?.ToString(),
                OperatingSystem = RuntimeInformation.OSDescription, Runtime = RuntimeInformation.FrameworkDescription,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString(), RunId = run.RunId,
                Events = _events, Processes = _processes, OmittedEvents = _omitted }, options));
            Write("README.txt", "SpeedForge debug: trace.json — события и команды; result.json — итог; process-*-stdout/stderr.txt — сырой вывод.\nСодержит точные адреса теста. HTTP session key и заголовки авторизации не записываются.\nОграничения: 2000 событий; stdout процесса до 4 млн символов, stderr до 256 тыс. Обрезка помечается в trace.json.\n");
        }
    }
    private void Write(string name, string text)
    {
        if (_diskFailed) return;
        try { File.WriteAllText(Path.Combine(DirectoryPath, name), text, new UTF8Encoding(false)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _diskFailed = true; }
    }
    public static string Read(string? directory)
    {
        if (directory == null || !Directory.Exists(directory)) return "Для этого запуска отладочный журнал отсутствует.";
        var builder = new StringBuilder();
        foreach (string file in Directory.GetFiles(directory).OrderBy(x => x, StringComparer.Ordinal))
        {
            var info = new FileInfo(file);
            if (info.Length > 20_000_000) continue;
            builder.AppendLine("========== " + Path.GetFileName(file) + " ==========");
            using var reader = new StreamReader(file);
            char[] chars = new char[150_000];
            int count = reader.ReadBlock(chars, 0, chars.Length);
            builder.Append(chars, 0, count);
            if (!reader.EndOfStream) builder.AppendLine("\n[В окне показан фрагмент. Полный вывод находится в ZIP и файлах журнала.]");
            builder.AppendLine();
            if (builder.Length > 600_000) break;
        }
        return builder.ToString();
    }
    public static void Export(string[] directories, string destination)
    {
        using var stream = new FileStream(destination, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (string directory in directories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(directory)) continue;
            foreach (string file in Directory.GetFiles(directory))
            {
                if (new FileInfo(file).Length > 20_000_000) continue;
                zip.CreateEntryFromFile(file, Path.GetFileName(directory) + "/" + Path.GetFileName(file));
            }
        }
    }
}
