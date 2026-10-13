using SpeedForge.Core;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

internal static class TimingAudit
{
    public static async Task<int> RunAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        using var stop = new CancellationTokenSource();
        var listener = new TcpListener(IPAddress.Loopback, 55664); listener.Start();
        var sink = Task.Run(async () =>
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync(stop.Token);
                await client.GetStream().CopyToAsync(Stream.Null, stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException) { }
        });
        var cases = new List<object>(); int failed = 0;
        async Task Check(string name, Func<Task> action)
        {
            try { await action(); cases.Add(new { Name = name, Passed = true }); Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failed++; cases.Add(new { Name = name, Passed = false, Error = ex.ToString() }); Console.WriteLine("FAIL " + name + ": " + ex.Message); }
        }
        void Require(bool condition, string why) { if (!condition) throw new InvalidOperationException(why); }
        RunCoordinator Coordinator(string name) => new(new TrustedTools(), new RunStore(Path.Combine(directory, name)));
        TestOptions Options(ServerEntry server, int duration = 3, bool latency = false) => new TestOptions(DurationSeconds: duration,
            BandwidthLimitBitsPerSecond: 1_000_000, MeasureLatency: latency, AllowPrivateTargets: true) with
            { TimeoutSeconds = RunTiming.RunSeconds(server, new TestOptions(DurationSeconds: duration, MeasureLatency: latency)) };
        try
        {
            await Check("Зависший control-порт: 4 секунды, затем рабочий порт; пинг только при нагрузке", async () =>
            {
                var server = ServerCatalog.Custom("127.0.0.1", 55664, EngineKind.Iperf3) with { Ports = [55664,55661] };
                var run = await Coordinator("tcp-control").RunAsync(server, Options(server, latency: true), null, null, CancellationToken.None);
                Require(run.Status == RunStatus.Measured && run.Port == 55661, JsonSerializer.Serialize(run));
                var connects = run.Stages.Where(x => x.Stage == RunStage.Connecting).ToArray();
                var transfer = run.Stages.First(x => x.Stage == RunStage.Transferring);
                Require(connects.Length == 2 && connects[1].OffsetSeconds - connects[0].OffsetSeconds is >= 3.8 and < 5.5,
                    "Control без ответа поглотил время передачи или не был остановлен за 4 секунды");
                Require(run.Latency.Where(x => x.Phase == ProbePhase.Upload).Any()
                    && run.Latency.Where(x => x.Phase == ProbePhase.Upload).All(x => x.Time >= transfer.At.AddMilliseconds(-50)),
                    "Пинг под нагрузкой измерял подготовку/зависший control");
            });
            await Check("iPerf2 reverse: 8 секунд реального приёма не обрезаются таймером запуска", async () =>
            {
                var server = ServerCatalog.Custom("127.0.0.1", 55662, EngineKind.Iperf2);
                var run = await Coordinator("reverse-long").RunAsync(server, Options(server, 8) with
                    { Direction = TestDirection.Download, Transport = TransportKind.Udp }, null, null, CancellationToken.None);
                Require(run.Status == RunStatus.Measured && run.ReceiverMbps > 0
                    && run.Stages.Any(x => x.Stage == RunStage.Transferring), JsonSerializer.Serialize(run));
            });
            await Check("iPerf2: реальный TX по интервалам, отчёт отдельно, версия проверяется один раз", async () =>
            {
                var server = ServerCatalog.Custom("127.0.0.1", 55663, EngineKind.Iperf2) with { Iperf2Legacy = true };
                var coordinator = Coordinator("legacy-phases");
                var options = Options(server) with { Transport = TransportKind.Udp };
                var first = await coordinator.RunAsync(server, options, null, null, CancellationToken.None);
                var second = await coordinator.RunAsync(server with { Port = 55665 }, options, null, null, CancellationToken.None);
                Require(first.Status == RunStatus.Measured && second.Status == RunStatus.Measured,
                    JsonSerializer.Serialize(new[] { first, second }));
                Require(first.Stages.Any(x => x.LocalSenderMbps > 0) && first.Stages.Any(x => x.Stage == RunStage.ReceiverReport)
                    && first.Stages.Any(x => x.Stage == RunStage.VersionCheck) && second.Stages.All(x => x.Stage != RunStage.VersionCheck),
                    "Потеряны интервалы TX/этап отчёта или версия запрашивается повторно");
            });
        }
        finally { stop.Cancel(); listener.Stop(); await sink; }
        await File.WriteAllTextAsync(Path.Combine(directory, "timing-tests.json"), JsonSerializer.Serialize(new { Failed = failed, Cases = cases }, new JsonSerializerOptions { WriteIndented = true }));
        return failed == 0 ? 0 : 1;
    }
}
