using SpeedForge.Core;
using System.Text.Json;

internal static class BeelineConsoleComparison
{
    public static async Task<int> RunAsync(string directory)
    {
        using var profiles = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "console-profiles.json")));
        var rows = new List<object>(); int failed = 0;
        foreach (var profile in profiles.RootElement.EnumerateArray())
        {
            string label = profile.GetProperty("Profile").GetString()!;
            var parts = label.TrimEnd(')').Split('(');
            var run = new RunResult { Engine = EngineKind.Iperf2, Transport = TransportKind.Udp, Direction = TestDirection.Upload,
                Options = new TestOptions(Transport: TransportKind.Udp, DurationSeconds: 10, ParallelStreams: int.Parse(parts[0]),
                    Iperf2OfferedBitsPerSecondPerStream: long.Parse(parts[1]) * 1_000_000) };
            Iperf2TextParser.Apply(await File.ReadAllTextAsync(Path.Combine(directory, $"console-{label}.stdout.txt")), run);
            bool serverReport = profile.GetProperty("ServerReport").GetBoolean();
            bool valid = run.SenderMbps > 0 && (serverReport || run.ReceiverMbps == null && run.UploadMbps == null
                && run.Status == RunStatus.Partial && run.JitterMilliseconds == null && run.LostPercent == null);
            if (!valid) failed++;
            rows.Add(new { Profile = label, Passed = valid, SenderMbps = run.SenderMbps, ReceiverMbps = run.ReceiverMbps,
                PythonFinalMbps = profile.GetProperty("PythonFinalMbps").Clone(), ServerReport = serverReport,
                Status = run.Status, ErrorCode = run.ErrorCode });
            Console.WriteLine($"{label}: {run.Status}/{run.ErrorCode}; TX={run.SenderMbps:F1}; RX={run.ReceiverMbps:F1}; {(valid ? "PASS" : "FAIL")}");
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "console-parser-comparison.json"), JsonSerializer.Serialize(new { Failed = failed, Cases = rows }, new JsonSerializerOptions { WriteIndented = true }));
        return failed == 0 ? 0 : 1;
    }
}
