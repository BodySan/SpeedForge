namespace SpeedForge.Core;

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<DirectionVerdict>))]
public enum DirectionVerdict { Both, DownloadObserved, UploadObserved, Unconfirmed, Cancelled, Sent }

public sealed record DirectionObservation(DirectionVerdict Verdict, string Description)
{
    public static DirectionObservation From(IEnumerable<RunResult> results)
    {
        var runs = results.ToArray();
        bool download = runs.Any(x => Confirmed(x) && x.DownloadMbps > 0);
        bool upload = runs.Any(x => Confirmed(x) && x.UploadMbps > 0);
        if (download && upload) return new(DirectionVerdict.Both, "Загрузка и отдача подтверждены");
        if (runs.Any(x => x.Status == RunStatus.Cancelled)) return new(DirectionVerdict.Cancelled, "Проверка остановлена");
        if (runs.Any(x => x.Options.Iperf2SenderOnly && x.Status == RunStatus.Measured && x.SenderMbps > 0 && !Confirmed(x)))
            return new(DirectionVerdict.Sent, "UDP-отправка завершена");
        if (download) return new(DirectionVerdict.DownloadObserved, "Подтверждена загрузка на ПК; отдача не подтверждена");
        if (upload) return new(DirectionVerdict.UploadObserved, "Подтверждена отдача с ПК; загрузка не подтверждена");
        return new(DirectionVerdict.Unconfirmed, "Измерение не подтверждено — подробности в журнале");
    }

    public static bool Confirmed(RunResult run) => (!run.Options.Iperf2SenderOnly || run.ReceiverBytes > 0 && run.ReceiverStreams == run.Options.ParallelStreams) && run.Status == RunStatus.Measured
        && (run.Mbps > 0 || run.DownloadMbps > 0 && run.UploadMbps > 0);
}

public static class DirectionalTest
{
    public static TestDirection[] Plan(ServerEntry server, TestOptions options, bool automatic) =>
        automatic ? BeelineTest.Applies(server) ? [TestDirection.Upload]
            : server.Engine == EngineKind.Http && server.Http is { UploadUrl: null }
            ? [TestDirection.Download] : server.Engine == EngineKind.Iperf2
                ? [TestDirection.Upload, TestDirection.Download] : [TestDirection.Download, TestDirection.Upload]
            : [options.Direction];

    public static async Task<IReadOnlyList<RunResult>> RunAsync(ServerEntry server, TestOptions options, bool automatic,
        Func<TestOptions, Task<RunResult>> measure, CancellationToken ct)
    {
        var results = new List<RunResult>();
        if (server.Engine == EngineKind.Iperf2 && server.Iperf2Profiles.Length > 0 && options.UseIperf2Profiles
            && (automatic || options.Direction == TestDirection.Upload))
        {
            for (int index = 0; index < server.Iperf2Profiles.Length; index++)
            {
                if (ct.IsCancellationRequested) return results;
                var profile = server.Iperf2Profiles[index];
                var next = options with { Direction = TestDirection.Upload, Transport = TransportKind.Udp,
                    ParallelStreams = profile.Streams, Iperf2OfferedBitsPerSecondPerStream = profile.BitsPerSecondPerStream,
                    MeasureLatencyBefore = index == 0 && options.MeasureLatencyBefore,
                    MeasureLatencyAfter = (!automatic || BeelineTest.Applies(server)) && index == server.Iperf2Profiles.Length - 1 && options.MeasureLatencyAfter,
                    ProbeIperf2Client = index == 0 && server.Iperf2Legacy,
                    TimeoutSeconds = index == 0 && server.Iperf2Legacy ? Math.Min(120, Math.Max(options.TimeoutSeconds ?? 45,
                        RunTiming.CompatibilitySeconds(options with { MeasureLatencyAfter = !automatic && index == server.Iperf2Profiles.Length - 1 && options.MeasureLatencyAfter }))) : options.TimeoutSeconds };
                next.Validate();
                results.Add(await measure(next));
                if (ct.IsCancellationRequested) return results;
                if (index < server.Iperf2Profiles.Length - 1 || automatic) await Task.Delay(350, ct);
            }
            // The supplied archive contains upload profiles. Probe reverse separately with one stream.
            if (automatic && !BeelineTest.Applies(server) && !ct.IsCancellationRequested)
                results.Add(await measure(options with { Direction = TestDirection.Download, ParallelStreams = 1,
                    Iperf2OfferedBitsPerSecondPerStream = 0, MeasureLatencyBefore = false }));
            return results;
        }
        var plan = Plan(server, options, automatic);
        for (int index = 0; index < plan.Length; index++)
        {
            if (ct.IsCancellationRequested) break;
            results.Add(await measure(options with { Direction = plan[index],
                MeasureLatencyBefore = index == 0 && options.MeasureLatencyBefore,
                MeasureLatencyAfter = index == plan.Length - 1 && options.MeasureLatencyAfter,
                ProbeIperf2Client = !options.Iperf2SenderOnly && server.Iperf2Legacy && plan[index] == TestDirection.Upload,
                TimeoutSeconds = server.Iperf2Legacy && plan[index] == TestDirection.Upload ? Math.Min(120,
                    Math.Max(options.TimeoutSeconds ?? 45, RunTiming.CompatibilitySeconds(options))) : options.TimeoutSeconds }));
            if (ct.IsCancellationRequested) break;
            // Give public single-client servers time to finish their previous session.
            if (index < plan.Length - 1) await Task.Delay(350, ct);
        }
        return results;
    }
}
