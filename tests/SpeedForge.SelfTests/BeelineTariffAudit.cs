using System.Diagnostics;
using System.Text.Json;
using SpeedForge.Core;

internal static class BeelineTariffAudit
{
    public static async Task<int> RunAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var server = ServerCatalog.LoadPublished().Single(x => x.Id == "beeline-iperf2");
        var coordinator = new RunCoordinator(new TrustedTools(), new RunStore(Path.Combine(directory, "runs")));
        var rows = new List<object>();
        int failed = 0;
        foreach (int tariff in new[] { 100, 500, 1000 })
        {
            var options = BeelineTest.Configure(new TestOptions(DurationSeconds: 10, MeasureLatency: false), tariff);
            var watch = Stopwatch.StartNew();
            var run = await coordinator.RunAsync(server, options, null, null, CancellationToken.None);
            watch.Stop();
            bool passed = run.Status == RunStatus.Measured && run.SenderMbps > 0 && run.DownloadMbps == null
                && watch.Elapsed.TotalSeconds < 14 && (run.ReceiverBytes > 0 || !DirectionObservation.Confirmed(run));
            if (!passed) failed++;
            rows.Add(new { TariffMbps = tariff, Seconds = watch.Elapsed.TotalSeconds, Passed = passed, Run = run });
            Console.WriteLine($"Тариф {tariff}: {run.Status}; TX={run.SenderMbps:F1}; RX={run.ReceiverMbps:F1}; {watch.Elapsed.TotalSeconds:F2}s; {(passed ? "PASS" : "FAIL")}");
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "tariffs.json"), JsonSerializer.Serialize(new { Failed = failed, Cases = rows }, new JsonSerializerOptions { WriteIndented = true }));
        return failed == 0 ? 0 : 1;
    }
}
