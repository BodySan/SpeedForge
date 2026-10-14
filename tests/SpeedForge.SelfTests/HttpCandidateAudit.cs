using System.Text.Json;
using SpeedForge.Core;

internal static class HttpCandidateAudit
{
    public static async Task<int> RunAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        string[] urls = ["http://mirror.corbina.net/100Mb",
            "https://mirror.corbina.net/100Mb",
            "https://mirror.truenetwork.ru/speedtest/1GiB", "https://mirror.truenetwork.ru/speedtest/10GiB",
            "https://speedtest.selectel.ru/100MB", "https://speedtest.selectel.ru/1GB",
            "https://speedtest.selectel.ru/10GB"];
        var results = new List<RunResult>();
        foreach (string url in urls)
        {
            var uri = new Uri(url);
            var server = ServerCatalog.Custom(uri.Host, uri.Port, EngineKind.Http) with
            { Http = new HttpProfile(url), Source = url, Provenance = "Кандидат из аудита; контрольный запуск" };
            var options = new TestOptions(Direction: TestDirection.Download, DurationSeconds: 8, ParallelStreams: 4,
                IpFamily: IpFamily.IPv4, MeasureLatency: false);
            var coordinator = new RunCoordinator(new TrustedTools(), new RunStore(Path.Combine(directory, "runs")));
            var run = await coordinator.RunAsync(server, options, null, null, CancellationToken.None);
            results.Add(run);
            Console.WriteLine($"{url}: {run.Status}; {run.Mbps:F2} Mbps; bytes={run.ReceiverBytes}; {run.ErrorCode}");
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "candidates.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}
