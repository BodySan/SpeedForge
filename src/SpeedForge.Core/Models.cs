using System.Text.Json.Serialization;

namespace SpeedForge.Core;

[JsonConverter(typeof(JsonStringEnumConverter<EngineKind>))]
public enum EngineKind { Iperf3, Iperf2, Http }
[JsonConverter(typeof(JsonStringEnumConverter<TestDirection>))]
public enum TestDirection { Upload, Download, Bidirectional }
[JsonConverter(typeof(JsonStringEnumConverter<TransportKind>))]
public enum TransportKind { Tcp, Udp }
[JsonConverter(typeof(JsonStringEnumConverter<IpFamily>))]
public enum IpFamily { Automatic, IPv4, IPv6 }
[JsonConverter(typeof(JsonStringEnumConverter<RunStatus>))]
public enum RunStatus { Measured, Partial, InsufficientData, NoData, Unsupported, Timeout, ServerBusy, NetworkError, EngineError, Cancelled }
[JsonConverter(typeof(JsonStringEnumConverter<ProbePhase>))]
public enum ProbePhase { Idle, Upload, Download, Recovery }

public sealed record ServerEntry(
    string Id, string Region, string Country, string Group, string City,
    string Host, int Port, EngineKind Engine, string Source, string Provenance,
    string[] PermittedDirections, string[] PermittedTransports, string Notes)
{
    public string Display => $"{Group} — {City} — {Host}:{Port} [{Engine}]";
    public int[] Ports { get; init; } = [];
    public HttpProfile? Http { get; init; }
    public Iperf2LoadProfile[] Iperf2Profiles { get; init; } = [];
    public string MethodLabel => Engine == EngineKind.Http ? (Http?.DownloadUrl.StartsWith("https:") == true ? "HTTPS" : "HTTP") : Engine.ToString();
    public string Observed => PublishedObservations.Describe(Id, Engine == EngineKind.Iperf2 ? TransportKind.Udp : TransportKind.Tcp);
}

public sealed record HttpProfile(string DownloadUrl, string? UploadUrl = null, string Protocol = "File", string UploadMethod = "POST");
public sealed record Iperf2LoadProfile(int Streams, long BitsPerSecondPerStream);
public sealed record MeasurementProgress(TestDirection Direction, double Seconds, double Mbps, long Bytes);

public sealed record TestOptions(
    TestDirection Direction = TestDirection.Upload,
    TransportKind Transport = TransportKind.Tcp,
    IpFamily IpFamily = IpFamily.Automatic,
    int DurationSeconds = 8,
    int ParallelStreams = 1,
    int IntervalSeconds = 1,
    long BandwidthLimitBitsPerSecond = 0,
    bool MeasureLatency = true,
    bool AllowPrivateTargets = false,
    bool AllowUnqualifiedDirection = false,
    int? TimeoutSeconds = null,
    long Iperf2OfferedBitsPerSecondPerStream = 0,
    bool UseIperf2Profiles = true)
{
    public void Validate()
    {
        if (DurationSeconds is < 3 or > 30) throw new ArgumentOutOfRangeException(nameof(DurationSeconds), "3–30 секунд");
        int maximumStreams = Iperf2OfferedBitsPerSecondPerStream > 0 ? 20 : 8;
        if (ParallelStreams < 1 || ParallelStreams > maximumStreams) throw new ArgumentOutOfRangeException(nameof(ParallelStreams), $"1–{maximumStreams} потоков");
        if (Iperf2OfferedBitsPerSecondPerStream != 0 && Iperf2OfferedBitsPerSecondPerStream is < 1_000_000 or > 1_000_000_000)
            throw new ArgumentOutOfRangeException(nameof(Iperf2OfferedBitsPerSecondPerStream));
        if (Iperf2OfferedBitsPerSecondPerStream > 0 && (Transport != TransportKind.Udp || Direction != TestDirection.Upload))
            throw new NotSupportedException("Профили нагрузки iPerf2 предназначены для отдачи UDP");
        if (IntervalSeconds is < 1 or > 5 || IntervalSeconds > DurationSeconds) throw new ArgumentOutOfRangeException(nameof(IntervalSeconds));
        if (TimeoutSeconds is { } timeout && (timeout < DurationSeconds + 2 || timeout > 120))
            throw new ArgumentOutOfRangeException(nameof(TimeoutSeconds), "Таймаут должен оставлять время измерению и не превышать 120 секунд");
        if (BandwidthLimitBitsPerSecond != 0 && BandwidthLimitBitsPerSecond is < 1_000_000 or > 2_000_000_000) throw new ArgumentOutOfRangeException(nameof(BandwidthLimitBitsPerSecond));
        // Estimate excludes protocol overhead and is explicitly displayed to the user.
        if (BandwidthLimitBitsPerSecond / 8.0 * DurationSeconds > 7_500_000_000)
            throw new InvalidOperationException("Превышен лимит планируемого трафика 7,5 ГБ");
        if (Transport == TransportKind.Udp && Direction == TestDirection.Bidirectional)
            throw new NotSupportedException("Двунаправленный UDP не поддерживается текущим профилем");
    }
}

public sealed record IntervalSample(double StartSeconds, double EndSeconds, long Bytes, double BitsPerSecond);
public sealed record LatencySample(ProbePhase Phase, DateTimeOffset Time, double? RttMilliseconds, string? Error);

public sealed class RunResult
{
    public int SchemaVersion { get; set; } = 1;
    public string RunId { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset Started { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset Finished { get; set; }
    public string ProfileId { get; set; } = "";
    public string Region { get; set; } = "";
    public string Country { get; set; } = "";
    public string Group { get; set; } = "";
    public string City { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string Source { get; set; } = "";
    public string PublishedStatus { get; set; } = "";
    public string? ResolvedIp { get; set; }
    public EngineKind Engine { get; set; }
    public string? EngineVersion { get; set; }
    public string? EngineSha256 { get; set; }
    public TestDirection Direction { get; set; }
    public TransportKind Transport { get; set; }
    public IpFamily IpFamily { get; set; }
    public TestOptions Options { get; set; } = new();
    public long? SenderBytes { get; set; }
    public long? ReceiverBytes { get; set; }
    public double? SenderMbps { get; set; }
    public double? ReceiverMbps { get; set; }
    public double? Mbps { get; set; }
    public double? UploadMbps { get; set; }
    public double? DownloadMbps { get; set; }
    public double? JitterMilliseconds { get; set; }
    public double? LostPercent { get; set; }
    public long? LostPackets { get; set; }
    public long? TotalPackets { get; set; }
    public long? TcpRetransmits { get; set; }
    public string MetricOrigin { get; set; } = "";
    public RunStatus Status { get; set; } = RunStatus.InsufficientData;
    public string? ErrorCode { get; set; }
    public string? ErrorText { get; set; }
    public string? ProcessExitCode { get; set; }
    public string? StderrExcerpt { get; set; }
    public List<IntervalSample> Intervals { get; set; } = [];
    public List<LatencySample> Latency { get; set; } = [];
    public List<string> Limitations { get; set; } = [];
    public string? DebugDirectory { get; set; }
    [JsonIgnore]
    public RunDiagnostics? Diagnostics { get; set; }
}

public static class Formatters
{
    public static string Speed(double? value) => value is >= 0 ? $"{value.Value:F2} Мбит/с" : "Н/Д";
    public static string Rtt(IEnumerable<LatencySample> probes, ProbePhase phase)
    {
        var values = probes.Where(x => x.Phase == phase && x.RttMilliseconds.HasValue)
            .Select(x => x.RttMilliseconds!.Value).Order().ToArray();
        if (values.Length == 0) return "Н/Д";
        return $"{(values.Length % 2 == 0 ? (values[values.Length / 2 - 1] + values[values.Length / 2]) / 2 : values[values.Length / 2]):F1} мс ({values.Length} измерений)";
    }
}
