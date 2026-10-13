using System.Globalization;
using System.Net;
using System.Text.Json;

namespace SpeedForge.Core;

public sealed class IperfRunner(TrustedTools trusted)
{
    // A confirmed port is tried first for the next direction in this coordinator.
    // Other published ports remain available if it becomes busy or stops working.
    private readonly Dictionary<(string Host, string Ip, EngineKind Engine, TransportKind Transport), int> _workingPorts = new();
    private readonly Dictionary<(string Host, string Ip, int Port), bool> _legacyClients = new();
    private readonly Dictionary<string, string> _versions = new(StringComparer.OrdinalIgnoreCase);
    // iPerf2 reverse treats -b 0 as its 1 Mbit/s default on the server.
    // Request 100 Gbit/s per stream instead; retain the exact request in diagnostics.
    public const long Iperf2MaximumReverseRate = 100_000_000_000;
    public async Task ExecuteAsync(ServerEntry server, TestOptions opt, IPAddress ip, RunResult result, CancellationToken ct, IProgress<string>? progress = null, IProgress<MeasurementProgress>? live = null, IProgress<RunStageProgress>? stages = null)
    {
        if (opt.Iperf2SenderOnly && server.Engine != EngineKind.Iperf2)
            throw new NotSupportedException("Исходящий UDP-режим Билайна предназначен для iPerf2");
        if (BeelineTest.Applies(server) && opt.Direction != TestDirection.Upload)
        { result.Status = RunStatus.Unsupported; result.ErrorCode = "BEELINE_UPLOAD_ONLY"; result.ErrorText = "Сервер Билайна используется только для исходящей UDP-проверки."; return; }
        if (opt.Iperf2OfferedBitsPerSecondPerStream > 0 && server.Engine != EngineKind.Iperf2)
            throw new NotSupportedException("Профиль нагрузки предназначен для iPerf2");
        if (server.Engine == EngineKind.Iperf2 && opt.Direction == TestDirection.Bidirectional)
        {
            result.Status = RunStatus.Unsupported;
            result.ErrorCode = "IPERF2_USE_SEQUENTIAL_DIRECTIONS";
            return;
        }
        if (server.Engine == EngineKind.Iperf2 && opt.Transport != TransportKind.Udp)
        {
            result.Status = RunStatus.Unsupported;
            result.ErrorCode = "IPERF2_TCP_RATE_LIMIT_NOT_QUALIFIED";
            result.Limitations.Add("iPerf2 TCP закрыт до квалификации версии и ограничения нагрузки");
            return;
        }
        ApprovedTool tool;
        stages?.Report(new(RunStage.ToolCheck));
        bool legacyProfile = server.Engine == EngineKind.Iperf2 && server.Iperf2Legacy && opt.Direction == TestDirection.Upload;
        var clientKey = (server.Host, ip.ToString(), server.Port);
        bool useLegacy = opt.Iperf2SenderOnly || !_legacyClients.TryGetValue(clientKey, out bool selectedLegacy) || selectedLegacy;
        try
        {
            // These supplied profiles require the original wire format. A generic
            // custom modern client must not silently replace the compatible one.
            tool = legacyProfile ? await BundledIperf.RequireAsync(ct, EngineKind.Iperf2, useLegacy)
                : await trusted.RequireVerifiedAsync(server.Engine, ct);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or FormatException)
        {
            result.Status = RunStatus.EngineError;
            result.ErrorCode = "UNTRUSTED_OR_MISSING_BINARY";
            result.ErrorText = e.Message;
            return;
        }
        await ExecuteToolAsync(server, opt, ip, result, tool, ct, progress, live, stages);
        bool Received(RunResult run) => run.ReceiverBytes > 0 && run.Status is RunStatus.Measured or RunStatus.Partial;
        if (!legacyProfile || opt.Iperf2SenderOnly || ct.IsCancellationRequested) return;
        if (Received(result)) { _legacyClients[clientKey] = useLegacy; return; }
        if (!opt.ProbeIperf2Client || result.Status is RunStatus.Cancelled or RunStatus.Unsupported) return;
        progress?.Report("Проверяем совместимость с другой версией iPerf2…");
        stages?.Report(new(RunStage.ToolCheck, Detail: "Подбор совместимого iPerf2"));
        var alternative = await BundledIperf.RequireAsync(ct, EngineKind.Iperf2, !useLegacy);
        var trial = new RunResult { Engine = server.Engine, Options = opt, Direction = opt.Direction, Transport = opt.Transport,
            Diagnostics = result.Diagnostics, Port = result.Port };
        await ExecuteToolAsync(server, opt, ip, trial, alternative, ct, progress, live, stages);
        if (ct.IsCancellationRequested || trial.Status == RunStatus.Cancelled)
        { result.Status = RunStatus.Cancelled; return; }
        result.Diagnostics?.Event("iperf2.compatibility", $"primary={tool.Sha256}/{result.Status}; alternative={alternative.Sha256}/{trial.Status}; receiver={trial.ReceiverMbps}");
        if (!Received(trial)) return;
        _legacyClients[clientKey] = !useLegacy;
        result.EngineSha256 = trial.EngineSha256; result.EngineVersion = trial.EngineVersion; result.Port = trial.Port;
        result.Status = trial.Status; result.ErrorCode = trial.ErrorCode; result.ErrorText = trial.ErrorText;
        result.ProcessExitCode = trial.ProcessExitCode; result.StderrExcerpt = trial.StderrExcerpt;
        result.MetricOrigin = trial.MetricOrigin; result.SenderMbps = trial.SenderMbps; result.SenderBytes = trial.SenderBytes;
        result.ReceiverMbps = trial.ReceiverMbps; result.ReceiverBytes = trial.ReceiverBytes; result.ReceiverStreams = trial.ReceiverStreams;
        result.Mbps = trial.Mbps; result.UploadMbps = trial.UploadMbps; result.DownloadMbps = trial.DownloadMbps;
        result.JitterMilliseconds = trial.JitterMilliseconds; result.LostPercent = trial.LostPercent;
        result.LostPackets = trial.LostPackets; result.TotalPackets = trial.TotalPackets;
        result.Intervals.Clear(); result.Intervals.AddRange(trial.Intervals);
        result.Limitations.RemoveAll(x => x.StartsWith("TX-only:", StringComparison.Ordinal));
        result.Limitations.AddRange(trial.Limitations);
    }

    private async Task ExecuteToolAsync(ServerEntry server, TestOptions opt, IPAddress ip, RunResult result, ApprovedTool tool,
        CancellationToken ct, IProgress<string>? progress, IProgress<MeasurementProgress>? live, IProgress<RunStageProgress>? stages)
    {
        result.EngineSha256 = tool.Sha256;
        if (server.Engine == EngineKind.Iperf2 && opt.Direction == TestDirection.Download && opt.BandwidthLimitBitsPerSecond == 0)
            result.Limitations.Add("iPerf2 reverse: для режима максимум запрошено 100 Гбит/с на поток; -b 0 на сервере возвращает стандартный лимит 1 Мбит/с");
        result.Diagnostics?.Event("binary.verified", $"Engine={server.Engine}; Path={tool.Path}; SHA256={tool.Sha256}; DLLs={string.Join(',', tool.CompanionDlls.Select(x => x.Key + '=' + x.Value))}");
        if (_versions.TryGetValue(tool.Sha256, out var cachedVersion))
        {
            result.EngineVersion = cachedVersion;
            result.Diagnostics?.Event("binary.version.cached", cachedVersion);
        }
        else
        {
            stages?.Report(new(RunStage.VersionCheck, RunTiming.VersionSeconds));
            var versionStarted = DateTimeOffset.Now;
            var version = await ExternalProcess.RunAsync(tool.Path, ["--version"], TimeSpan.FromSeconds(RunTiming.VersionSeconds), ct);
            result.Diagnostics?.Process(tool.Path, ["--version"], versionStarted, version);
            result.EngineVersion = (version.Stdout + version.Stderr).Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
            // iPerf2 prints its normal --version banner and exits with code 1.
            bool normalVersionExit = version.ExitCode == 0 || server.Engine == EngineKind.Iperf2 && version.ExitCode == 1
                && result.EngineVersion?.StartsWith("iperf version ", StringComparison.OrdinalIgnoreCase) == true;
            if (normalVersionExit && !version.TimedOut && !version.Cancelled
                && result.EngineVersion?.Contains("iperf", StringComparison.OrdinalIgnoreCase) == true)
                _versions[tool.Sha256] = result.EngineVersion;
        }
        var ports = (server.Ports.Length > 0 ? server.Ports : [server.Port]).Distinct().Take(16).ToArray();
        var portKey = (server.Host, ip.ToString(), server.Engine, opt.Transport);
        if (_workingPorts.TryGetValue(portKey, out int workingPort) && ports.Contains(workingPort))
            ports = ports.OrderBy(x => x == workingPort ? 0 : 1).ToArray();
        for (int index = 0; index < ports.Length; index++)
        {
            ct.ThrowIfCancellationRequested();
            result.Port = ports[index];
            var target = server with { Port = ports[index] };
            progress?.Report($"iPerf: {server.Host}:{target.Port}, попытка {index + 1}/{ports.Length}");
            await AttemptAsync(target, opt, ip, result, tool, ct, live, stages);
            if (result.Status == RunStatus.Measured) _workingPorts[portKey] = target.Port;
            if (result.Status is RunStatus.Measured or RunStatus.Partial or RunStatus.Cancelled
                || index == ports.Length - 1) return;
            string error = (result.ErrorText + " " + result.StderrExcerpt).ToLowerInvariant();
            bool retry = result.Status is RunStatus.Timeout or RunStatus.ServerBusy or RunStatus.NetworkError
                || error.Contains("busy") || error.Contains("unable to connect") || error.Contains("refused") || error.Contains("timed out");
            if (!retry) return;
            progress?.Report("Порт недоступен или занят. Проверяем следующий порт этого же сервера…");
            result.Intervals.Clear(); result.ErrorCode = null; result.ErrorText = null;
            result.Status = RunStatus.InsufficientData;
            result.Mbps = result.DownloadMbps = result.UploadMbps = result.ReceiverMbps = result.SenderMbps = null;
            result.ReceiverBytes = result.SenderBytes = null;
            result.UploadReceiverBytes = result.DownloadReceiverBytes = null;
        }
    }

    private static async Task AttemptAsync(ServerEntry server, TestOptions opt, IPAddress ip, RunResult result, ApprovedTool tool, CancellationToken ct, IProgress<MeasurementProgress>? live, IProgress<RunStageProgress>? stages)
    {
        var args = server.Engine == EngineKind.Iperf3 ? BuildIperf3Arguments(server, opt, ip) : BuildIperf2Arguments(server, opt, ip);
        bool streaming = server.Engine == EngineKind.Iperf3 && tool.Sha256.Equals(BundledIperf.ExecutableSha256, StringComparison.OrdinalIgnoreCase);
        if (streaming) args = [.. args, "--json-stream", "--forceflush"];
        result.Diagnostics?.Event("process.start", string.Join(' ', new[] { tool.Path }.Concat(args)));
        var started = DateTimeOffset.Now;
        bool observable = streaming || server.Engine == EngineKind.Iperf2;
        using var stageStop = new CancellationTokenSource();
        if (observable) stageStop.CancelAfter(TimeSpan.FromSeconds(RunTiming.StartupSeconds));
        using var attemptStop = CancellationTokenSource.CreateLinkedTokenSource(ct, stageStop.Token);
        RunStage stage = observable ? RunStage.Connecting : RunStage.EngineRunning;
        bool senderCompleted = false;
        stages?.Report(new(stage, observable ? RunTiming.StartupSeconds : null, Detail: $"Порт {server.Port}"));
        void Transfer(double elapsed = 0)
        {
            if (stage != RunStage.Connecting) return;
            stage = RunStage.Transferring;
            stageStop.CancelAfter(TimeSpan.FromSeconds(Math.Max(.1, opt.DurationSeconds - elapsed + RunTiming.ReportSeconds(server.Engine))));
            stages?.Report(new(stage, opt.DurationSeconds, elapsed));
        }
        void Report()
        {
            if (stage == RunStage.ReceiverReport) return;
            stage = RunStage.ReceiverReport;
            stageStop.CancelAfter(TimeSpan.FromSeconds(RunTiming.ReportSeconds(server.Engine)));
            stages?.Report(new(stage, opt.Iperf2SenderOnly ? .2 : RunTiming.ReportSeconds(server.Engine)));
        }
        void Line(string line)
        {
            if (streaming)
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("event", out var kind))
                    {
                        if (kind.GetString() == "start") Transfer();
                        else if (kind.GetString() == "end") Report();
                        else if (kind.GetString() == "interval")
                        {
                            Transfer();
                            if (doc.RootElement.GetProperty("data").TryGetProperty("sum", out var sum)
                                && sum.TryGetProperty("end", out var end) && end.GetDouble() >= opt.DurationSeconds) Report();
                        }
                    }
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException) { }
                ReportInterval(line, opt, live);
            }
            else if (server.Engine == EngineKind.Iperf2)
            {
                if (opt.Direction == TestDirection.Upload && line.Contains("Sending ", StringComparison.Ordinal)
                    && line.Contains("datagrams", StringComparison.Ordinal)) Transfer();
                var sample = Iperf2TextParser.Interval(line, opt.Direction, opt.ParallelStreams);
                if (sample is { Bytes: > 0 })
                {
                    Transfer(sample.Seconds);
                    if (opt.Direction == TestDirection.Download || opt.Iperf2SenderOnly) live?.Report(sample);
                    else if (stage == RunStage.Transferring)
                        stages?.Report(new(RunStage.Transferring, opt.DurationSeconds, sample.Seconds) { LocalSenderMbps = sample.Mbps });
                }
                bool summary = Iperf2TextParser.IsSummary(line, opt.DurationSeconds, opt.ParallelStreams);
                if (line.Contains("Server Report:", StringComparison.Ordinal) || summary) Report();
                if (opt.Iperf2SenderOnly && summary)
                {
                    senderCompleted = true;
                    // Leave time for the native client to send its UDP FIN, then
                    // stop its optional ACK retries. A full sender summary exists.
                    stageStop.CancelAfter(TimeSpan.FromMilliseconds(200));
                }
            }
        }
        var outcome = await ExternalProcess.RunAsync(tool.Path, args, TimeSpan.FromSeconds(RunTiming.ProcessSeconds(server.Engine, opt)), attemptStop.Token, observable ? Line : null);
        bool stageTimedOut = stageStop.IsCancellationRequested && !ct.IsCancellationRequested;
        if (stageTimedOut) outcome = outcome with { TimedOut = true, Cancelled = false };
        result.Diagnostics?.Process(tool.Path, args, started, outcome);
        result.ProcessExitCode = outcome.ExitCode.ToString(CultureInfo.InvariantCulture);
        result.StderrExcerpt = Redaction.Mask(outcome.Stderr.Length > 512 ? outcome.Stderr[..512] : outcome.Stderr);
        if (streaming) Iperf3Parser.ApplyStreaming(outcome.Stdout, result);
        else if (server.Engine == EngineKind.Iperf3) Iperf3Parser.Apply(outcome.Stdout, result);
        else Iperf2TextParser.Apply(outcome.Stdout, result);
        if (outcome.Cancelled) { result.Status = RunStatus.Cancelled; return; }
        if (opt.Iperf2SenderOnly && senderCompleted && result.SenderBytes == 0)
        { result.Status = RunStatus.NoData; result.ErrorCode = "ZERO_PAYLOAD"; return; }
        if (opt.Iperf2SenderOnly && senderCompleted && result.SenderBytes > 0 && result.SenderMbps > 0)
        {
            if (result.Status == RunStatus.Measured && result.ReceiverStreams == opt.ParallelStreams && result.ReceiverBytes > 0)
                return;
            result.Status = RunStatus.Measured; result.Mbps = result.UploadMbps = result.SenderMbps;
            result.ErrorCode = null; result.ErrorText = null;
            result.MetricOrigin = "iPerf2: измеренная локальная UDP-отправка; приём сервером не измерялся";
            result.Diagnostics?.Event("iperf2.sender.complete", "Full sender summary; optional ACK retries stopped after 200 ms");
            return;
        }
        if (outcome.TimedOut)
        {
            // A completed UDP sender summary is still useful when the tool
            // hangs waiting for its final server report. Keep it as local TX;
            // process lifetime is not evidence of a failed data transfer.
            if (server.Engine == EngineKind.Iperf2 && result.Direction == TestDirection.Upload
                && result.SenderBytes > 0 && result.ReceiverBytes == null
                && result.ErrorCode == "IPERF2_RECEIVER_UNCONFIRMED")
            {
                result.Status = RunStatus.Partial;
                result.Limitations.Add("После завершения отправки процесс остановлен по таймауту ожидания отчёта UDP. Локальный TX не подтверждает скорость до сервера.");
                return;
            }
            result.Status = result.ReceiverBytes > 0 ? RunStatus.Partial : RunStatus.Timeout;
            result.ErrorCode = stageTimedOut && stage == RunStage.Connecting ? "START_TIMEOUT" : "PROCESS_TIMEOUT";
            result.ErrorText = result.ErrorCode == "START_TIMEOUT" ? "Передача данных не началась за 4 секунды. Проверяем доступность направления или следующий порт."
                : "Измеритель не завершился вовремя; процесс остановлен. Сохранили доступную статистику.";
            return;
        }
        if (result.Status is RunStatus.EngineError or RunStatus.InsufficientData or RunStatus.NoData)
        {
            string text = (result.ErrorText + " " + outcome.Stderr).ToLowerInvariant();
            if (text.Contains("busy")) { result.Status = RunStatus.ServerBusy; result.ErrorCode = "SERVER_BUSY"; }
            else if (text.Contains("unable to connect") || text.Contains("refused") || text.Contains("unreachable")
                || text.Contains("reset by peer") || text.Contains("control socket has closed"))
            { result.Status = RunStatus.NetworkError; result.ErrorCode = "CONNECTION_FAILED"; }
            else if (text.Contains("timed out")) { result.Status = RunStatus.Timeout; result.ErrorCode = "CONNECTION_TIMEOUT"; }
            if (!string.IsNullOrWhiteSpace(outcome.Stderr)) result.ErrorText = (result.ErrorText + " " + outcome.Stderr.Trim())[..Math.Min(1000, (result.ErrorText + " " + outcome.Stderr.Trim()).Length)];
        }
        if (outcome.ExitCode != 0)
        {
            if (result.Status == RunStatus.Measured) { result.Status = RunStatus.Partial; result.Limitations.Add("Измерительный процесс завершился с ненулевым кодом"); }
            else if (result.Status is RunStatus.InsufficientData or RunStatus.EngineError or RunStatus.NoData)
                result.ErrorCode ??= "PROCESS_FAILURE";
        }
    }

    private static void ReportInterval(string line, TestOptions options, IProgress<MeasurementProgress>? live)
    {
        if (live == null) return;
        // Upload intervals are local UDP send rates. Only the final remote
        // receiver report can tell us how much crossed the customer's link.
        if (options.Transport == TransportKind.Udp && options.Direction == TestDirection.Upload) return;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("event", out var kind) || kind.GetString() != "interval") return;
            var data = root.GetProperty("data");
            if (!data.TryGetProperty("sum", out var sum)) return;
            double seconds = sum.GetProperty("end").GetDouble();
            double bps = sum.GetProperty("bits_per_second").GetDouble();
            long bytes = sum.GetProperty("bytes").GetInt64();
            if (double.IsFinite(bps) && bps >= 0 && seconds >= 0) live.Report(new(options.Direction, seconds, bps / 1_000_000, bytes));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException) { }
    }

    public static string[] BuildIperf3Arguments(ServerEntry server, TestOptions opt, IPAddress ip)
    {
        opt.Validate();
        if (opt.Iperf2OfferedBitsPerSecondPerStream > 0) throw new NotSupportedException("Профиль нагрузки предназначен для iPerf2");
        // iPerf3 -b is per stream. Share the selected total cap across streams and directions.
        long perStreamCap = Math.Max(1, opt.BandwidthLimitBitsPerSecond / opt.ParallelStreams
            / (opt.Direction == TestDirection.Bidirectional ? 2 : 1));
        var args = new List<string> { "-c", ip.ToString(), "-p", server.Port.ToString(CultureInfo.InvariantCulture),
            "-J", "-t", opt.DurationSeconds.ToString(CultureInfo.InvariantCulture),
            "-i", opt.IntervalSeconds.ToString(CultureInfo.InvariantCulture),
            "-P", opt.ParallelStreams.ToString(CultureInfo.InvariantCulture), "--connect-timeout", RunTiming.TcpConnectMilliseconds.ToString(CultureInfo.InvariantCulture) };
        if (opt.BandwidthLimitBitsPerSecond > 0 || opt.Transport == TransportKind.Udp)
        {
            args.Add("-b");
            args.Add((opt.BandwidthLimitBitsPerSecond == 0 ? 0 : perStreamCap).ToString(CultureInfo.InvariantCulture));
        }
        if (opt.IpFamily == IpFamily.IPv4) args.Add("-4");
        if (opt.IpFamily == IpFamily.IPv6) args.Add("-6");
        if (opt.Transport == TransportKind.Udp) args.Add("-u");
        if (opt.Direction == TestDirection.Download) args.Add("-R");
        if (opt.Direction == TestDirection.Bidirectional) args.Add("--bidir");
        return args.ToArray();
    }
    public static string[] BuildIperf2Arguments(ServerEntry server, TestOptions opt, IPAddress ip)
    {
        opt.Validate();
        if (opt.Transport != TransportKind.Udp || opt.Direction == TestDirection.Bidirectional)
            throw new NotSupportedException("iPerf2: UDP, каждое направление запускается отдельно");
        var args = new List<string> { "-c", ip.ToString(), "-p", server.Port.ToString(CultureInfo.InvariantCulture),
            "-t", opt.DurationSeconds.ToString(CultureInfo.InvariantCulture),
            "-i", opt.IntervalSeconds.ToString(CultureInfo.InvariantCulture), "-f", "b",
            "-P", opt.ParallelStreams.ToString(CultureInfo.InvariantCulture) };
        long rate = opt.BandwidthLimitBitsPerSecond == 0 && opt.Direction == TestDirection.Download
            ? Iperf2MaximumReverseRate : opt.BandwidthLimitBitsPerSecond / opt.ParallelStreams;
        if (opt.Iperf2OfferedBitsPerSecondPerStream > 0)
            rate = opt.BandwidthLimitBitsPerSecond == 0 ? opt.Iperf2OfferedBitsPerSecondPerStream
                : Math.Min(opt.Iperf2OfferedBitsPerSecondPerStream, Math.Max(1, rate));
        args.AddRange(["-u", "-b", rate.ToString(CultureInfo.InvariantCulture)]);
        if (opt.Direction == TestDirection.Download) args.Add("--reverse");
        if (opt.IpFamily == IpFamily.IPv6) args.Add("-V");
        return args.ToArray();
    }
}
