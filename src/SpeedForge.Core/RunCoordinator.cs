using System.Net;
using System.Net.Sockets;

namespace SpeedForge.Core;

public sealed class RunCoordinator(TrustedTools trusted, RunStore store)
{
    private readonly SemaphoreSlim _singleActive = new(1, 1);
    private readonly IperfRunner _iperf = new(trusted);
    private readonly HttpMeasurement _http = new();

    public async Task<RunResult> RunAsync(ServerEntry server, TestOptions options, string? ephemeralKey,
        IProgress<string>? progress, CancellationToken ct, IProgress<MeasurementProgress>? live = null)
    {
        var userCancellation = ct;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSeconds ?? 45));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        ct = linked.Token;
        if (!await _singleActive.WaitAsync(0, ct))
            throw new InvalidOperationException("Уже выполняется нагрузочная проверка");
        var run = new RunResult
        {
            ProfileId = server.Id, Region = server.Region, Country = server.Country, Group = server.Group,
            City = server.City, Host = server.Host, Port = server.Port, Source = server.Source,
            PublishedStatus = server.Provenance, Engine = server.Engine, Options = options,
            Direction = options.Direction, Transport = options.Transport, IpFamily = options.IpFamily
        };
        try
        {
            run.Diagnostics = new RunDiagnostics(store.DirectoryPath, run.RunId);
            run.DebugDirectory = run.Diagnostics.DirectoryPath;
            run.Diagnostics.Event("run.start", $"{server.Engine} {options.Transport} {options.Direction}; host={server.Host}; ports={string.Join(',', server.Ports.Length > 0 ? server.Ports : [server.Port])}; duration={options.DurationSeconds}; streams={options.ParallelStreams}; cap={options.BandwidthLimitBitsPerSecond}; timeout={options.TimeoutSeconds ?? 45}; private={options.AllowPrivateTargets}");
            options.Validate();
            run.Diagnostics.Event("catalog.hints", $"Directions={string.Join(',', server.PermittedDirections)}; transports={string.Join(',', server.PermittedTransports)}; hints do not block iPerf tests");
            progress?.Report("Разрешение адреса и проверка политики безопасности...");
            var ip = await SecurityPolicy.ResolveAsync(server, options, ct, run.Diagnostics);
            run.ResolvedIp = ip.ToString();
            run.Diagnostics.Event("dns.selected", ip.ToString() + " (fixed for this run)");
            run.Limitations.Add("Успех одного запуска не доказывает постоянную доступность сервера");
            if (options.MeasureLatency)
            {
                progress?.Report("Измерение задержки до нагрузки...");
                run.Latency.AddRange(await LatencyCollector.CollectAsync(ip, ProbePhase.Idle, TimeSpan.FromSeconds(2), ct));
            }
            progress?.Report("Запуск сетевой нагрузки: " + options.Direction);
            using var probeStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var phase = options.Direction == TestDirection.Download ? ProbePhase.Download : ProbePhase.Upload;
            var concurrent = options.MeasureLatency
                ? LatencyCollector.CollectAsync(ip, phase, TimeSpan.FromSeconds(options.DurationSeconds), probeStop.Token)
                : Task.FromResult(new List<LatencySample>());
            try
            {
                if (server.Engine == EngineKind.Http && server.Http is not null)
                    await new PublicHttpMeasurement().ExecuteAsync(server, options, ip, run, ct, live);
                else if (server.Engine == EngineKind.Http)
                    await _http.ExecuteAsync(server, options, ip, run, ephemeralKey, ct);
                else
                    await _iperf.ExecuteAsync(server, options, ip, run, ct, progress, live);
            }
            finally
            {
                probeStop.Cancel();
                try { run.Latency.AddRange(await concurrent); }
                catch (OperationCanceledException) { }
            }
            if (options.MeasureLatency && !ct.IsCancellationRequested)
            {
                progress?.Report("Контроль восстановления задержки...");
                run.Latency.AddRange(await LatencyCollector.CollectAsync(ip, ProbePhase.Recovery, TimeSpan.FromSeconds(2), ct));
            }
            if (run.Status == RunStatus.Measured && options.MeasureLatency
                && run.Latency.Count(x => x.Phase == ProbePhase.Idle && x.RttMilliseconds.HasValue) < 3)
                run.Limitations.Add("Базовая RTT выборка слишком мала для уверенного диагноза задержек под нагрузкой");
            progress?.Report("Завершено: " + run.Status);
        }
        catch (OperationCanceledException)
        {
            run.Diagnostics?.Event("run.cancel", "Cancellation requested");
            run.Status = RunStatus.Cancelled;
            run.ErrorCode = "USER_CANCELLED";
        }
        catch (TargetRejectedException e)
        {
            run.Diagnostics?.Event("exception", e.ToString());
            run.Status = RunStatus.Unsupported;
            run.ErrorCode = "DESTINATION_REJECTED";
            run.ErrorText = e.Message;
        }
        catch (SocketException e)
        {
            run.Diagnostics?.Event("exception", e.ToString());
            run.Status = RunStatus.NetworkError;
            run.ErrorCode = e.SocketErrorCode.ToString();
        }
        catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException
            or ArgumentException or UnauthorizedAccessException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            run.Diagnostics?.Event("exception", e.ToString());
            run.Status = RunStatus.EngineError;
            run.ErrorCode = e.GetType().Name;
            run.ErrorText = e.Message.Length > 300 ? e.Message[..300] : e.Message;
        }
        finally
        {
            try
            {
                if (deadline.IsCancellationRequested && !userCancellation.IsCancellationRequested)
                { run.Status = RunStatus.Timeout; run.ErrorCode = "RUN_DEADLINE"; run.ErrorText = "Сервер не завершил тест за отведённое время"; }
                run.Finished = DateTimeOffset.Now;
                run.Diagnostics?.Event("run.finish", $"Status={run.Status}; ErrorCode={run.ErrorCode}; Error={run.ErrorText}; Origin={run.MetricOrigin}");
                try { run.Diagnostics?.Finish(run); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                { progress?.Report("Не удалось сохранить debug: " + e.GetType().Name); }
                if (run.Diagnostics is { DiskAvailable: false })
                {
                    run.DebugDirectory = null;
                    run.Limitations.Add("Измерение завершено, но debug не сохранён: каталог недоступен для записи");
                    progress?.Report("Debug не сохранён: каталог недоступен для записи");
                }
                try { await store.AppendAsync(run, CancellationToken.None); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                { progress?.Report("Не удалось сохранить историю: " + e.GetType().Name); }
            }
            finally { _singleActive.Release(); }
        }
        return run;
    }
}
