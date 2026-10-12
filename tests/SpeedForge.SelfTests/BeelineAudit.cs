using SpeedForge.Core;
using System.Text.Json;
using System.Text.Encodings.Web;

internal static class BeelineAudit
{
    public static async Task<int> RunAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var server = ServerCatalog.LoadPublished().Single(x => x.Id == "beeline-iperf2");
        var coordinator = new RunCoordinator(new TrustedTools(), new RunStore(Path.Combine(directory, "data")));
        var options = new TestOptions(Transport: TransportKind.Udp, DurationSeconds: 3, MeasureLatency: false,
            BandwidthLimitBitsPerSecond: 10_000_000, TimeoutSeconds: 12);
        var runs = await DirectionalTest.RunAsync(server, options, true, async next =>
        {
            var run = await coordinator.RunAsync(server, next, null, null, CancellationToken.None);
            Console.WriteLine($"{next.Direction}; P={next.ParallelStreams}; offered/stream={next.Iperf2OfferedBitsPerSecondPerStream}; RX={run.ReceiverMbps:F2}; TX={run.SenderMbps:F2}; {run.Status}; {run.ErrorCode}");
            return run;
        }, CancellationToken.None);
        var json = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        await File.WriteAllTextAsync(Path.Combine(directory, "beeline-profiles.json"), JsonSerializer.Serialize(new
        { Server = server.Host, Options = options, Observation = DirectionObservation.From(runs), Runs = runs }, json));
        // Public availability may vary; this audit must not fabricate success for missing ACKs.
        bool valid = runs.Count == 9 && runs.All(x => x.EngineSha256 == BundledIperf.Iperf2Sha256
            && (x.ReceiverMbps != null || x.Mbps == null && x.UploadMbps == null && x.DownloadMbps == null));
        Console.WriteLine($"Completed {runs.Count}/9 runs. {DirectionObservation.From(runs).Description}");
        return valid ? 0 : 1;
    }
}
