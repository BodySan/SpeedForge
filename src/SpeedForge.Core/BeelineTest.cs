namespace SpeedForge.Core;

public static class BeelineTest
{
    public static IReadOnlyList<int> Tariffs { get; } = Array.AsReadOnly(Enumerable.Range(1, 10).Select(x => x * 100).ToArray());
    public static bool Applies(ServerEntry server) => server.Id == "beeline-iperf2" && server.Engine == EngineKind.Iperf2;
    public static TestOptions Configure(TestOptions options, int tariffMbps)
    {
        if (tariffMbps is < 1 or > 2000) throw new ArgumentOutOfRangeException(nameof(tariffMbps), "Укажите скорость тарифа от 1 до 2000 Мбит/с");
        return options with { Direction = TestDirection.Upload, Transport = TransportKind.Udp,
            ParallelStreams = tariffMbps <= 100 ? 1 : 5, BandwidthLimitBitsPerSecond = (long)tariffMbps * 1_000_000,
            Iperf2OfferedBitsPerSecondPerStream = 0, UseIperf2Profiles = false, ProbeIperf2Client = false, Iperf2SenderOnly = true };
    }
}
