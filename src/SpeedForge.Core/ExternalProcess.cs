using System.Diagnostics;
using System.Text;

namespace SpeedForge.Core;

public sealed record ProcessOutcome(string Stdout, string Stderr, int ExitCode, bool TimedOut, bool Cancelled,
    bool StdoutTruncated = false, bool StderrTruncated = false);

public static class ExternalProcess
{
    public static async Task<ProcessOutcome> RunAsync(string executable, IReadOnlyList<string> args, TimeSpan timeout,
        CancellationToken ct, Action<string>? stdoutLine = null)
    {
        ct.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
            RedirectStandardOutput = true, RedirectStandardInput = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        info.Environment.Remove("PYTHONPATH"); info.Environment.Remove("LD_PRELOAD"); info.Environment.Remove("DOTNET_STARTUP_HOOKS");
        using var process = new Process { StartInfo = info };
        if (!process.Start()) throw new InvalidOperationException("Не удалось запустить измерительный модуль");
        process.StandardInput.Close();
        using var deadline = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        var stdout = new Capture(4_000_000); var stderr = new Capture(256_000);
        var output = DrainAsync(process.StandardOutput, stdout, stdoutLine);
        var error = DrainAsync(process.StandardError, stderr, null);
        bool timedOut = false, cancelled = false;
        try { await process.WaitForExitAsync(linked.Token); }
        catch (OperationCanceledException)
        {
            cancelled = ct.IsCancellationRequested; timedOut = !cancelled;
            TryTerminate(process);
        }
        finally { if (!process.HasExited) TryTerminate(process); }
        try { await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (Exception ex) when (ex is IOException or TimeoutException or ObjectDisposedException)
        { stderr.Append("\n[Ошибка чтения вывода: " + ex.GetType().Name + "]\n"); }
        return new(stdout.Text, stderr.Text, process.HasExited ? process.ExitCode : -1, timedOut, cancelled,
            stdout.Truncated, stderr.Truncated);
    }
    private sealed class Capture(int maximum)
    {
        private readonly object _gate = new();
        private readonly StringBuilder _text = new();
        public bool Truncated { get; private set; }
        public string Text { get { lock (_gate) return _text.ToString(); } }
        public void Append(string text)
        {
            lock (_gate)
            {
                int available = Math.Max(0, maximum - _text.Length);
                if (text.Length > available) Truncated = true;
                _text.Append(text.AsSpan(0, Math.Min(text.Length, available)));
            }
        }
    }
    private static async Task DrainAsync(StreamReader reader, Capture capture, Action<string>? onLine)
    {
        char[] chunk = new char[4096];
        var pending = new StringBuilder(); bool lineTooLong = false;
        while (true)
        {
            int read = await reader.ReadAsync(chunk.AsMemory());
            if (read == 0) break;
            capture.Append(new string(chunk, 0, read));
            if (onLine == null) continue;
            for (int i = 0; i < read; i++)
            {
                if (chunk[i] == '\n')
                {
                    if (!lineTooLong) onLine(pending.ToString());
                    pending.Clear(); lineTooLong = false;
                }
                else if (pending.Length < 500_000) pending.Append(chunk[i]);
                else lineTooLong = true;
            }
        }
        if (onLine != null && pending.Length > 0 && !lineTooLong) onLine(pending.ToString());
    }
    private static void TryTerminate(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
