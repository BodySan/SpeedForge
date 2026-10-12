using SpeedForge.Core;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Encodings.Web;

internal static class CatalogAudit
{
    public static async Task<int> RunAsync(string directory, bool udp = false)
    {
        Directory.CreateDirectory(directory);
        var rows = new ConcurrentBag<AuditRow>();
        int completed = 0;
        var servers = ServerCatalog.LoadPublished().Where(x => !udp || x.Engine == EngineKind.Iperf3).ToArray();
        await Parallel.ForEachAsync(servers, new ParallelOptions { MaxDegreeOfParallelism = 2 }, async (server, ct) =>
        {
            var coordinator = new RunCoordinator(new TrustedTools(), new RunStore(Path.Combine(directory, server.Id)));
            var options = new TestOptions(DurationSeconds: 3, ParallelStreams: 1, MeasureLatency: false,
                Transport: udp || server.Engine == EngineKind.Iperf2 ? TransportKind.Udp : TransportKind.Tcp,
                BandwidthLimitBitsPerSecond: 10_000_000,
                TimeoutSeconds: 12 + Math.Max(0, Math.Min(16, server.Ports.Distinct().Count()) - 1) * 3);
            // HTTP profiles without an upload endpoint cannot exercise that direction.
            var results = await DirectionalTest.RunAsync(server, options, true,
                next => coordinator.RunAsync(server, next, null, null, ct), ct);
            var observation = DirectionObservation.From(results);
            var repeats = new List<RunResult>();
            if (server.Engine != EngineKind.Http && observation.Verdict is DirectionVerdict.DownloadObserved or DirectionVerdict.UploadObserved)
            {
                // A second attempt distinguishes a repeated asymmetric observation from a one-off failure.
                await Task.Delay(500, ct);
                foreach (var direction in new[] { TestDirection.Upload, TestDirection.Download })
                    repeats.Add(await coordinator.RunAsync(server, options with { Direction = direction }, null, null, ct));
            }
            var repeatObservation = repeats.Count == 0 ? null : DirectionObservation.From(repeats);
            var row = new AuditRow(server.Id, server.Host, server.Engine, options.Transport, DateTimeOffset.Now,
                observation, repeatObservation, results.ToArray(), repeats.ToArray());
            rows.Add(row);
            Console.WriteLine($"[{Interlocked.Increment(ref completed)}/{servers.Length}] {server.Host} {server.Engine} {options.Transport}: {observation.Verdict}; " +
                string.Join("; ", results.Select(x => $"{x.Direction}={x.Status}/{x.ErrorCode}/{x.Mbps:F2}")) +
                (repeatObservation == null ? "" : $"; повтор={repeatObservation.Verdict}"));
            await File.WriteAllTextAsync(Path.Combine(directory, server.Id + ".json"), JsonSerializer.Serialize(row, Json), ct);
        });
        var ordered = rows.OrderBy(x => x.ProfileId).ToArray();
        await File.WriteAllTextAsync(Path.Combine(directory, "catalog-results.json"), JsonSerializer.Serialize(ordered, Json));
        await File.WriteAllTextAsync(Path.Combine(directory, "catalog-summary.md"),
            "# Проверка направлений российских серверов\n\n" +
            "Дата: " + DateTimeOffset.Now.ToString("O") + ". 3 секунды, 1 поток, общий лимит 10 Мбит/с; два сервера одновременно. Это проверка передачи данных, а не измерение максимальной скорости.\n\n" +
            "«Подтверждено только одно направление» описывает наблюдение с этого ПК. Ошибка или занятость не доказывает постоянного запрета другого направления.\n\n" +
            "| Сервер | Метод | Первый запуск | Повтор | На ПК | С ПК |\n|---|---|---|---|---|---|\n" +
            string.Join('\n', ordered.Select(x => $"| {x.Host} | {x.Engine} | {x.Observation.Description} | {x.Repeat?.Description ?? "—"} | {Description(x.Results, TestDirection.Download)} | {Description(x.Results, TestDirection.Upload)} |")));
        Console.WriteLine("Всего проверено: " + ordered.Length);
        foreach (var group in ordered.GroupBy(x => x.Observation.Verdict)) Console.WriteLine(group.Key + ": " + group.Count());
        return ordered.Length == servers.Length ? 0 : 1;
    }
    private static string Description(RunResult[] results, TestDirection direction)
    {
        var run = results.FirstOrDefault(x => x.Direction == direction);
        return run == null ? "Нет опубликованного endpoint" : $"{run.Status}; {run.Mbps:F2} Мбит/с; {run.ErrorCode}";
    }
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private sealed record AuditRow(string ProfileId, string Host, EngineKind Engine, TransportKind Transport, DateTimeOffset Checked,
        DirectionObservation Observation, DirectionObservation? Repeat, RunResult[] Results, RunResult[] RepeatResults);
}
