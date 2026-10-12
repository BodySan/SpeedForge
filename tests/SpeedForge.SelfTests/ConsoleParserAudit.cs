using System.Text.Json;
using SpeedForge.Core;

internal static class ConsoleParserAudit
{
    public static async Task<int> RunAsync(string directory)
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "console-results.json")));
        var results = new List<object>();
        int failed = 0;
        foreach (var row in document.RootElement.EnumerateArray())
        {
            string client = row.GetProperty("Client").GetString()!;
            string profile = row.GetProperty("Profile").GetString()!;
            int streams = row.GetProperty("Streams").GetInt32();
            var options = new TestOptions(Direction: TestDirection.Upload, Transport: TransportKind.Udp, DurationSeconds: 10,
                ParallelStreams: streams, Iperf2OfferedBitsPerSecondPerStream: row.GetProperty("MbpsPerStream").GetInt64() * 1_000_000);
            var run = new RunResult { Engine = EngineKind.Iperf2, Direction = TestDirection.Upload, Transport = TransportKind.Udp, Options = options };
            Iperf2TextParser.Apply(await File.ReadAllTextAsync(Path.Combine(directory, client + "-" + profile + ".stdout.txt")), run);
            int reports = row.GetProperty("ReceiverReports").GetInt32();
            bool invalidLegacyDecode = row.TryGetProperty("ExpectedInvalidReport", out var flag) && flag.GetBoolean();
            bool valid = run.ProfileLabel == profile && (invalidLegacyDecode
                ? run.Status == RunStatus.EngineError && run.ErrorCode == "IPERF2_RECEIVER_REPORT_INVALID" && run.ReceiverMbps == null
                : reports == 0 ? run.ReceiverMbps == null && run.UploadMbps == null
                : run.ReceiverStreams == reports && run.ReceiverMbps > 0 && run.Status == (reports < streams ? RunStatus.Partial : RunStatus.Measured));
            if (!valid) failed++;
            results.Add(new { Client = client, Profile = profile, Valid = valid, run.Status, run.ErrorCode,
                run.SenderMbps, run.ReceiverMbps, run.ReceiverStreams, ExpectedReports = reports,
                ConsoleTimedOut = row.GetProperty("TimedOut").GetBoolean() });
            Console.WriteLine($"{(valid ? "PASS" : "FAIL")} {client} {profile}: TX={run.SenderMbps:F1}; RX={run.ReceiverMbps:F1}; reports={run.ReceiverStreams}/{streams}; {run.Status}");
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "parser-results.json"), JsonSerializer.Serialize(new { Failed = failed, Cases = results }, new JsonSerializerOptions { WriteIndented = true }));
        return failed == 0 ? 0 : 1;
    }
}
