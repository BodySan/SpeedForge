using System.Diagnostics;
using System.Text.Json.Serialization;

namespace SpeedForge.Core;

[JsonConverter(typeof(JsonStringEnumConverter<RunStage>))]
public enum RunStage { Preparing, LatencyBefore, ToolCheck, VersionCheck, Connecting, Transferring, ReceiverReport, LatencyAfter, Saving, Finished, EngineRunning }

public sealed record RunStageProgress(RunStage Stage, double? ExpectedSeconds = null, double MeasuredSeconds = 0, string? Detail = null)
{
    public DateTimeOffset At { get; init; } = DateTimeOffset.Now;
    public double? OffsetSeconds { get; init; }
    public double? LocalSenderMbps { get; init; }
    [JsonIgnore] public long Timestamp { get; init; } = Stopwatch.GetTimestamp();
    [JsonIgnore] public double ElapsedSeconds => MeasuredSeconds + Stopwatch.GetElapsedTime(Timestamp).TotalSeconds;
}

public static class RunTiming
{
    public const int StartupSeconds = 4;
    public const int VersionSeconds = 5;
    public const int UdpReportSeconds = 5;
    public const int Iperf3ReportSeconds = 10;
    public const int TcpConnectMilliseconds = 2500;
    public static int ReportSeconds(EngineKind engine) => engine == EngineKind.Iperf2 ? UdpReportSeconds : Iperf3ReportSeconds;
    public static int ProcessSeconds(EngineKind engine, TestOptions options) => StartupSeconds + options.DurationSeconds + ReportSeconds(engine);
    public static int RunSeconds(ServerEntry server, TestOptions options)
    {
        int latency = options.MeasureLatency ? (options.MeasureLatencyBefore ? 3 : 0) + (options.MeasureLatencyAfter ? 3 : 0) : 0;
        int ports = Math.Clamp(server.Ports.Distinct().Count(), 1, 16);
        return Math.Min(120, 2 + latency + (server.Engine == EngineKind.Http ? StartupSeconds + options.DurationSeconds + 5
            : VersionSeconds + ports * StartupSeconds + options.DurationSeconds + ReportSeconds(server.Engine)));
    }
    public static int CompatibilitySeconds(TestOptions options) => Math.Min(120, 2 +
        (options.MeasureLatency ? (options.MeasureLatencyBefore ? 3 : 0) + (options.MeasureLatencyAfter ? 3 : 0) : 0)
        + 2 * (VersionSeconds + StartupSeconds + options.DurationSeconds + UdpReportSeconds));
}
