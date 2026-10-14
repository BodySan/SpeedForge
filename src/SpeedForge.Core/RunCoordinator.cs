using System.Net;
using System.Net.Sockets;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace SpeedForge.Core;

public sealed class RunCoordinator(TrustedTools trusted, RunStore store)
{
    private readonly SemaphoreSlim _singleActive = new(1, 1);
    private readonly IperfRunner _iperf = new(trusted);
    private readonly HttpMeasurement _http = new();

    public async Task<RunResult> RunAsync(ServerEntry server, TestOptions options, string? ephemeralKey,
        IProgress<string>? progress, CancellationToken ct, IProgress<MeasurementProgress>? live = null, IProgress<RunStageProgress>? stages = null,
        ClientNetworkSnapshot? clientNetwork = null)
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
            Direction = options.Direction, Transport = options.Transport, IpFamily = options.IpFamily,
            ClientExternalIp = clientNetwork?.ExternalIp, ClientHttpIp = clientNetwork?.HttpIp,
            ClientIpSource = clientNetwork?.Source, ClientIpObservedAt = clientNetwork?.ObservedAt
        };
        long started = Stopwatch.GetTimestamp();
        var events = new ConcurrentQueue<RunStageProgress>();
        var probes = new List<(CancellationTokenSource Stop, Task<List<LatencySample>> Task)>();
        var probeGate = new object();
        CancellationTokenSource? activeProbe = null;
        IPAddress? probeAddress = null;
        void StopProbe()
        {
            lock (probeGate) { activeProbe?.Cancel(); activeProbe = null; }
        }
        var stageProgress = new InlineProgress<RunStageProgress>(value =>
        {
            var item = value with { OffsetSeconds = Stopwatch.GetElapsedTime(started, value.Timestamp).TotalSeconds };
            events.Enqueue(item);
            if (options.MeasureLatency && probeAddress != null)
            {
                if (value.Stage is RunStage.Transferring or RunStage.EngineRunning)
                {
                    lock (probeGate)
                    {
                        if (activeProbe == null)
                        {
                            activeProbe = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            var phase = options.Direction == TestDirection.Download ? ProbePhase.Download : ProbePhase.Upload;
                            probes.Add((activeProbe, LatencyCollector.CollectAsync(probeAddress, phase,
                                TimeSpan.FromSeconds(Math.Max(.1, options.DurationSeconds - value.MeasuredSeconds)), activeProbe.Token)));
                        }
                    }
                }
                else StopProbe();
            }
            run.Diagnostics?.Event("stage", $"{item.Stage}; offset={item.OffsetSeconds:F3}; expected={item.ExpectedSeconds}; measured={item.MeasuredSeconds}; {item.Detail}");
            stages?.Report(item);
        });
        try
        {
            run.Diagnostics = new RunDiagnostics(store.DirectoryPath, run.RunId);
            run.DebugDirectory = run.Diagnostics.DirectoryPath;
            if (clientNetwork != null) run.Diagnostics.Event("client.network", $"external={clientNetwork.ExternalIp ?? "unknown"}; https={clientNetwork.HttpIp ?? "unknown"}; source={clientNetwork.Source}; observed={clientNetwork.ObservedAt:O}");
            run.Diagnostics.Event("run.start", $"{server.Engine} {options.Transport} {options.Direction}; host={server.Host}; ports={string.Join(',', server.Ports.Length > 0 ? server.Ports : [server.Port])}; duration={options.DurationSeconds}; streams={options.ParallelStreams}; cap={options.BandwidthLimitBitsPerSecond}; timeout={options.TimeoutSeconds ?? 45}; private={options.AllowPrivateTargets}");
            options.Validate();
            stageProgress.Report(new(RunStage.Preparing));
            run.Diagnostics.Event("catalog.hints", $"Directions={string.Join(',', server.PermittedDirections)}; transports={string.Join(',', server.PermittedTransports)}; hints do not block iPerf tests");
            progress?.Report("Разрешение адреса и проверка политики безопасности...");
            var ip = await SecurityPolicy.ResolveAsync(server, options, ct, run.Diagnostics);
            probeAddress = ip;
            run.ResolvedIp = ip.ToString();
            run.Diagnostics.Event("dns.selected", ip.ToString() + " (fixed for this run)");
            run.Limitations.Add("Успех одного запуска не доказывает постоянную доступность сервера");
            if (options.MeasureLatency && options.MeasureLatencyBefore)
            {
                progress?.Report("Измерение задержки до нагрузки...");
                stageProgress.Report(new(RunStage.LatencyBefore, 2));
                run.Latency.AddRange(await LatencyCollector.CollectAsync(ip, ProbePhase.Idle, TimeSpan.FromSeconds(2), ct));
            }
            progress?.Report("Запуск сетевой нагрузки: " + options.Direction);
            try
            {
                if (server.Engine == EngineKind.Http && server.Http is not null)
                {
                    stageProgress.Report(new(RunStage.EngineRunning, options.DurationSeconds, Detail: "HTTP: соединение и передача"));
                    await new PublicHttpMeasurement().ExecuteAsync(server, options, ip, run, ct, live);
                }
                else if (server.Engine == EngineKind.Http)
                {
                    stageProgress.Report(new(RunStage.EngineRunning, options.DurationSeconds, Detail: "HTTP: соединение и передача"));
                    await _http.ExecuteAsync(server, options, ip, run, ephemeralKey, ct, live);
                }
                else
                    await _iperf.ExecuteAsync(server, options, ip, run, ct, progress, live, stageProgress);
            }
            finally
            {
                StopProbe();
                (CancellationTokenSource Stop, Task<List<LatencySample>> Task)[] pending;
                lock (probeGate) pending = probes.ToArray();
                foreach (var probe in pending)
                {
                    try { run.Latency.AddRange(await probe.Task); }
                    catch (OperationCanceledException) { }
                    finally { probe.Stop.Dispose(); }
                }
            }
            if (options.MeasureLatency && options.MeasureLatencyAfter && !ct.IsCancellationRequested)
            {
                progress?.Report("Контроль восстановления задержки...");
                stageProgress.Report(new(RunStage.LatencyAfter, 2));
                run.Latency.AddRange(await LatencyCollector.CollectAsync(ip, ProbePhase.Recovery, TimeSpan.FromSeconds(2), ct));
            }
            if (run.Status == RunStatus.Measured && options.MeasureLatency && options.MeasureLatencyBefore
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
                if (userCancellation.IsCancellationRequested && run.Status == RunStatus.Cancelled)
                    run.ErrorCode = "USER_CANCELLED";
                if (deadline.IsCancellationRequested && !userCancellation.IsCancellationRequested)
                {
                    if (run.Engine == EngineKind.Iperf2 && run.Direction == TestDirection.Upload
                        && run.SenderBytes > 0 && run.ReceiverBytes == null && run.ErrorCode == "IPERF2_RECEIVER_UNCONFIRMED")
                    {
                        run.Status = RunStatus.Partial;
                        run.Limitations.Add("Общий таймаут истёк после завершённой UDP-отправки; отчёт сервера не получен.");
                    }
                    else
                    {
                        run.Status = run.ReceiverBytes > 0 ? RunStatus.Partial : RunStatus.Timeout;
                        run.ErrorCode = "RUN_DEADLINE"; run.ErrorText = "Сервер не завершил тест за отведённое время";
                    }
                }
                run.Finished = DateTimeOffset.Now;
                stageProgress.Report(new(RunStage.Saving));
                run.Stages.AddRange(events.ToArray());
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
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
}
