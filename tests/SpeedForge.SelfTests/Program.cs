using SpeedForge.Core;
using System.Net;

if (args.Contains("--emit-output")) { Console.Write(new string('x', 4_100_000)); Console.Error.Write("stderr-marker"); return 0; }
if (args.Length >= 2 && args[0] == "--audit-catalog") return await CatalogAudit.RunAsync(Path.GetFullPath(args[1]), args.Contains("--udp"));
if (args.Length >= 2 && args[0] == "--audit-beeline") return await BeelineAudit.RunAsync(Path.GetFullPath(args[1]));
if (args.Length >= 2 && args[0] == "--sandbox-serve") return await SandboxInfrastructure.ServeAsync(Path.GetFullPath(args[1]));
if (args.Length >= 2 && args[0] == "--sandbox-test") return await SandboxTests.RunAsync(Path.GetFullPath(args[1]), args.Contains("--iperf2-only"));
if (args.Length >= 2 && args[0] == "--tools-json")
{
    var iperf3 = await BundledIperf.RequireAsync(CancellationToken.None, EngineKind.Iperf3);
    var iperf2 = await BundledIperf.RequireAsync(CancellationToken.None, EngineKind.Iperf2);
    await File.WriteAllTextAsync(args[1], System.Text.Json.JsonSerializer.Serialize(new { Iperf3 = iperf3, Iperf2 = iperf2 }));
    return 0;
}

int passed = 0, failed = 0;
void Case(string name, Action action)
{
    try { action(); Console.WriteLine("PASS  " + name); passed++; }
    catch (Exception e) { Console.Error.WriteLine("FAIL  " + name + " — " + e); failed++; }
}
void Require(bool test, string info = "Assertion failed") { if (!test) throw new Exception(info); }
void Throw<T>(Action test) where T : Exception
{
    try { test(); } catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
RunResult Result(TransportKind protocol = TransportKind.Tcp, TestDirection direction = TestDirection.Upload) =>
    new() { Engine = EngineKind.Iperf3, Transport = protocol, Direction = direction };
const string Good = """
{"start":{"version":"iperf 3.22"},"intervals":[{"sum":{"start":0,"end":1,"bytes":1250000,"bits_per_second":10000000}}],"end":{"sum_sent":{"bytes":10000000,"bits_per_second":10000000,"retransmits":4},"sum_received":{"bytes":9000000,"bits_per_second":9000000}}}
""";
GeoIpTests.Register(Case);
const string NoData = """
{"end":{"sum_sent":{"bytes":0,"bits_per_second":0},"sum_received":{"bytes":0,"bits_per_second":0}}}
""";
const string SenderOnly = """
{"end":{"sum_sent":{"bytes":50000,"bits_per_second":6000000}}}
""";

Case("Каталог содержит расширенный пул iPerf3 и HTTP", () => Require(ServerCatalog.LoadPublished().Count >= 64 && ServerCatalog.LoadPublished().Count(x => x.Engine == EngineKind.Http) >= 4));
Case("Каждая точка имеет источник и статус", () => Require(ServerCatalog.LoadPublished().All(s => !string.IsNullOrWhiteSpace(s.Source) && !string.IsNullOrWhiteSpace(s.Provenance))));
Case("Билайн iPerf2 встроен в каталог", () => Require(ServerCatalog.LoadPublished().Any(x => x.Engine == EngineKind.Iperf2 && x.Host == "speedcheck.gldn.net" && x.Port == 5001)));
Case("Каждый сервер проверен, TCP и UDP имеют отдельные наблюдения", () =>
{
    Require(PublishedObservations.All.Count == 125);
    Require(ServerCatalog.LoadPublished().All(x => PublishedObservations.All.Any(y => y.ProfileId == x.Id)));
    Require(PublishedObservations.All.Count(x => x.Transport == TransportKind.Udp) == 61);
});
Case("Лимит iPerf2 разделён между потоками", () =>
{
    var arguments = IperfRunner.BuildIperf2Arguments(ServerCatalog.Custom("127.0.0.1", 5001, EngineKind.Iperf2),
        new TestOptions(Transport: TransportKind.Udp, ParallelStreams: 4, BandwidthLimitBitsPerSecond: 100_000_000), IPAddress.Loopback);
    Require(arguments[Array.IndexOf(arguments, "-b") + 1] == "25000000");
});
Case("iPerf2 reverse действительно запускается", () => Require(IperfRunner.BuildIperf2Arguments(ServerCatalog.Custom("127.0.0.1", 5001, EngineKind.Iperf2),
    new TestOptions(Direction: TestDirection.Download, Transport: TransportKind.Udp, BandwidthLimitBitsPerSecond: 1_000_000), IPAddress.Loopback).Contains("--reverse")));
Case("iPerf2 Server Report отделён от sender", () =>
{
    var result = new RunResult { Options = new TestOptions(DurationSeconds: 3), Direction = TestDirection.Upload };
    Iperf2TextParser.Apply("[ 1] 0.00-3.00 sec 375000 Bytes 1000000 bits/sec\n[ 1] Server Report:\n[ 1] 0.00-3.00 sec 300000 Bytes 800000 bits/sec 0.100 ms 2/100 (2%)", result);
    Require(result.Status == RunStatus.Measured && result.Mbps == 0.8 && result.SenderMbps == 1 && result.LostPercent == 2);
});
Case("iPerf2 без ACK даёт TX-only с частичным статусом", () =>
{
    var result = new RunResult { Options = new TestOptions(DurationSeconds: 3), Direction = TestDirection.Upload };
    Iperf2TextParser.Apply("[ 1] 0.00-3.00 sec 375000 Bytes 1000000 bits/sec\nWARNING: did not receive ack", result);
    Require(result.Status == RunStatus.Partial && result.ReceiverMbps == null && result.SenderMbps == 1
        && result.UploadMbps == null && result.Mbps == null && result.LostPercent == null);
});
Case("iPerf2: отправлено 1 Гбит/с, сервер принял 100 Мбит/с", () =>
{
    var result = new RunResult { Options = new TestOptions(DurationSeconds: 3), Direction = TestDirection.Upload };
    Iperf2TextParser.Apply("[ 1] 0.00-1.00 sec 125000000 Bytes 1000000000 bits/sec\n[ 1] 0.00-3.00 sec 375000000 Bytes 1000000000 bits/sec\n[ 1] Server Report:\n[ 1] 0.00-3.00 sec 37500000 Bytes 100000000 bits/sec 0.100 ms 900/1000 (90%)", result);
    Require(result.Status == RunStatus.Measured && result.UploadMbps == 100 && result.ReceiverMbps == 100
        && result.SenderMbps == 1000 && result.LostPercent == 90 && result.Intervals.Count == 0);
});
Case("Все 8 профилей Билайн сохраняют -b на поток и -P из архива", () =>
{
    var server = ServerCatalog.LoadPublished().Single(x => x.Id == "beeline-iperf2");
    Require(server.Iperf2Profiles.Length == 8);
    foreach (var profile in server.Iperf2Profiles)
    {
        var options = new TestOptions(Transport: TransportKind.Udp, ParallelStreams: profile.Streams,
            Iperf2OfferedBitsPerSecondPerStream: profile.BitsPerSecondPerStream);
        var arguments = IperfRunner.BuildIperf2Arguments(server, options, IPAddress.Loopback);
        Require(arguments[Array.IndexOf(arguments, "-b") + 1] == profile.BitsPerSecondPerStream.ToString());
        Require(arguments[Array.IndexOf(arguments, "-P") + 1] == profile.Streams.ToString());
        var capped = IperfRunner.BuildIperf2Arguments(server, options with { BandwidthLimitBitsPerSecond = 100_000_000 }, IPAddress.Loopback);
        Require(long.Parse(capped[Array.IndexOf(capped, "-b") + 1]) * profile.Streams <= 100_000_000);
    }
});
Case("Билайн: отмена первого профиля прекращает автоматический перебор", () =>
{
    var server = ServerCatalog.LoadPublished().Single(x => x.Id == "beeline-iperf2");
    using var stop = new CancellationTokenSource();
    int calls = 0;
    var runs = DirectionalTest.RunAsync(server, new TestOptions(Transport: TransportKind.Udp), true,
        next => { calls++; stop.Cancel(); return Task.FromResult(new RunResult { Status = RunStatus.Cancelled }); }, stop.Token).GetAwaiter().GetResult();
    Require(calls == 1 && runs.Count == 1);
});
Case("iPerf2 Windows reverse [ *1] распознаётся", () =>
{
    var result = new RunResult { Direction = TestDirection.Download, Options = new TestOptions(DurationSeconds: 3) };
    Iperf2TextParser.Apply("[ *1] 0.00-3.02 sec 377790 Bytes 999312 bits/sec 0.027 ms 0/257 (0%)", result);
    Require(result.Status == RunStatus.Measured && result.DownloadMbps > 0 && result.ReceiverBytes == 377790);
});
Case("Лицензии и исходники экспортируются из ресурсов exe", () =>
{
    string file = Path.Combine(Path.GetTempPath(), "speedforge-licenses-" + Guid.NewGuid().ToString("N") + ".zip");
    try
    {
        BundledIperf.ExportLicenses(file);
        using var zip = System.IO.Compression.ZipFile.OpenRead(file);
        Require(zip.Entries.Any(x => x.FullName.EndsWith("IPERF2-LICENSE.txt"))
            && zip.Entries.Any(x => x.FullName.EndsWith("cygwin-3.6.10-1-src.tar.xz")));
    }
    finally { if (File.Exists(file)) File.Delete(file); }
});
Case("Порты каталога допустимы", () => Require(ServerCatalog.LoadPublished().All(s => s.Port is > 0 and < 65536)));
Case("Названия серверов синтаксически допустимы", () => Require(ServerCatalog.LoadPublished().All(s => SecurityPolicy.ValidHost(s.Host))));
Case("Имя с переводом строки отклонено", () => Require(!SecurityPolicy.ValidHost("good.example\n--bad")));
Case("Имя с начальным дефисом отклонено", () => Require(!SecurityPolicy.ValidHost("-evil.host")));
Case("Имя с двойной точкой отклонено", () => Require(!SecurityPolicy.ValidHost("a..b")));
Case("Имя с пробелом отклонено", () => Require(!SecurityPolicy.ValidHost("good host")));
Case("Домен в пределах лимита принят", () => Require(SecurityPolicy.ValidHost("a.example")));
Case("Публичный IPv4 разрешён", () => Require(SecurityPolicy.IsPublicUnicast(IPAddress.Parse("8.8.8.8"))));
Case("IPv4 loopback закрыт", () => Require(!SecurityPolicy.IsPublicUnicast(IPAddress.Parse("127.0.0.1"))));
Case("IPv4 private закрыт", () => Require(!SecurityPolicy.IsPublicUnicast(IPAddress.Parse("192.168.2.5"))));
Case("IPv4 CGNAT закрыт", () => Require(!SecurityPolicy.IsPublicUnicast(IPAddress.Parse("100.64.1.2"))));
Case("IPv4 multicast закрыт", () => Require(!SecurityPolicy.IsPublicUnicast(IPAddress.Parse("224.0.0.1"))));
Case("IPv4 documentation закрыт", () => Require(!SecurityPolicy.IsPublicUnicast(IPAddress.Parse("203.0.113.8"))));
Case("IPv6 loopback закрыт", () => Require(!SecurityPolicy.IsPublicUnicast(IPAddress.IPv6Loopback)));
Case("IPv6 ULA закрыт", () => Require(!SecurityPolicy.IsPublicUnicast(IPAddress.Parse("fd01::1234"))));
Case("IPv6 link-local закрыт", () => Require(!SecurityPolicy.IsPublicUnicast(IPAddress.Parse("fe80::1"))));
Case("IPv6 documentation закрыт", () => Require(!SecurityPolicy.IsPublicUnicast(IPAddress.Parse("2001:db8::8"))));
Case("Нормальный профиль допускается", () => new TestOptions().Validate());
Case("TCP максимум не добавляет искусственный лимит iPerf3", () => Require(!IperfRunner.BuildIperf3Arguments(ServerCatalog.LoadPublished()[0], new TestOptions(), IPAddress.Parse("8.8.8.8")).Contains("-b")));
Case("HTTP URL другого сервера отклоняется", () => Throw<TargetRejectedException>(() => PublicHttpMeasurement.ValidateUri("https://other.example/test.bin", ServerCatalog.Custom("test.example", 443, EngineKind.Http))));
Case("HTTP URL с учётными данными отклоняется", () => Throw<TargetRejectedException>(() => PublicHttpMeasurement.ValidateUri("https://user:secret@test.example/test.bin", ServerCatalog.Custom("test.example", 443, EngineKind.Http))));
Case("Нулевое время отклонено", () => Throw<ArgumentOutOfRangeException>(() => new TestOptions(DurationSeconds: 0).Validate()));
Case("Огромная длительность отклонена", () => Throw<ArgumentOutOfRangeException>(() => new TestOptions(DurationSeconds: 90).Validate()));
Case("Слишком много потоков отклонено", () => Throw<ArgumentOutOfRangeException>(() => new TestOptions(ParallelStreams: 256).Validate()));
Case("UDP максимум разрешён", () => new TestOptions(Transport: TransportKind.Udp, BandwidthLimitBitsPerSecond: 0).Validate());
Case("UDP максимум iPerf3 явно отключает стандартный лимит", () =>
{
    var arguments = IperfRunner.BuildIperf3Arguments(ServerCatalog.LoadPublished()[0], new TestOptions(Transport: TransportKind.Udp, ParallelStreams: 4), IPAddress.Loopback);
    Require(arguments[Array.IndexOf(arguments, "-b") + 1] == "0");
});
Case("iPerf2 максимум: upload без лимита, reverse без отката к 1 Мбит/с", () =>
{
    var server = ServerCatalog.Custom("127.0.0.1", 5001, EngineKind.Iperf2);
    foreach (var direction in new[] { TestDirection.Upload, TestDirection.Download })
    {
        var arguments = IperfRunner.BuildIperf2Arguments(server, new TestOptions(Direction: direction, Transport: TransportKind.Udp, ParallelStreams: 4), IPAddress.Loopback);
        Require(arguments[Array.IndexOf(arguments, "-b") + 1] == (direction == TestDirection.Upload ? "0" : "100000000000"));
    }
});
Case("Bidir UDP запрещён", () => Throw<NotSupportedException>(() => new TestOptions(Direction: TestDirection.Bidirectional, Transport: TransportKind.Udp, BandwidthLimitBitsPerSecond: 100_000_000).Validate()));
Case("iPerf3 настоящий receiver -> Measured", () => { var r = Result(); Iperf3Parser.Apply(Good, r); Require(r.Status == RunStatus.Measured && r.Mbps == 9 && r.TcpRetransmits == 4); });
Case("iPerf3 интервал извлечён", () => { var r = Result(); Iperf3Parser.Apply(Good, r); Require(r.Intervals.Count == 1 && r.Intervals[0].Bytes == 1250000); });
Case("iPerf3 0 байт -> NoData", () => { var r = Result(); Iperf3Parser.Apply(NoData, r); Require(r.Status == RunStatus.NoData && r.Mbps == null); });
Case("iPerf3 sender без receiver -> Insufficient", () => { var r = Result(); Iperf3Parser.Apply(SenderOnly, r); Require(r.Status == RunStatus.InsufficientData && r.Mbps == null); });
Case("iPerf3 reverse не перепутан с upload", () => { var r = Result(direction: TestDirection.Download); Iperf3Parser.Apply(Good, r); Require(r.Status == RunStatus.Measured && r.DownloadMbps == 9 && r.UploadMbps == null); });
Case("iPerf3 error -> EngineError", () => { var r = Result(); Iperf3Parser.Apply("{\"error\":\"access denied\"}", r); Require(r.Status == RunStatus.EngineError); });
Case("iPerf3 malformed -> EngineError", () => { var r = Result(); Iperf3Parser.Apply("{\"end\":", r); Require(r.Status == RunStatus.EngineError); });
Case("iPerf3 empty -> EngineError", () => { var r = Result(); Iperf3Parser.Apply("", r); Require(r.Status == RunStatus.EngineError); });
Case("iPerf3 UDP sender без receiver не выдаёт loss=0", () => { var r = Result(TransportKind.Udp); Iperf3Parser.Apply(SenderOnly, r); Require(r.LostPercent == null && r.Status == RunStatus.InsufficientData); });
Case("iPerf3 UDP receiver даёт loss", () =>
{
    var r = Result(TransportKind.Udp);
    Iperf3Parser.Apply("{\"end\":{\"sum_sent\":{\"bytes\":123,\"bits_per_second\":1234},\"sum_received\":{\"bytes\":100,\"bits_per_second\":1000,\"lost_percent\":3.5,\"lost_packets\":2,\"packets\":100,\"jitter_ms\":1.6}}}",r);
    Require(r.Status == RunStatus.Measured && r.LostPercent == 3.5 && r.JitterMilliseconds == 1.6);
});
Case("iPerf3 bidir без второй суммы не выдаёт успех", () => { var r = Result(direction: TestDirection.Bidirectional); Iperf3Parser.Apply(Good, r); Require(r.Status == RunStatus.InsufficientData); });
Case("iPerf2 invalid CSV -> Insufficient", () => { var r = Result(); Iperf2Parser.Apply("hello world", r); Require(r.Status == RunStatus.InsufficientData); });
Case("iPerf2 sender-only TCP не приравнивает приёмник", () =>
{
    var r = Result(); Iperf2Parser.Apply("20260101010101,1.2.3.4,5151,5.6.7.8,5001,1,0.0-8.0,10000000,10000000", r);
    Require(r.Status == RunStatus.InsufficientData && r.Mbps == null);
});
Case("iPerf2 UDP с приёмной строкой", () =>
{
    var r = Result(TransportKind.Udp);
    Iperf2Parser.Apply("20260101010101,1.2.3.4,5151,5.6.7.8,5001,1,0.0-8.0,10000000,10000000,0.4,1,200,0.5", r);
    Require(r.Status == RunStatus.Measured && r.LostPercent == 0.5);
});
Case("Маскирование IPv4", () => Require(!Redaction.Mask("IP 192.168.0.1").Contains("192.168.0.1")));
Case("Маскирование IPv6", () => Require(!Redaction.Mask("2001:db8::1").Contains("2001:db8::1")));
Case("Удаление CRLF из внешних ошибок", () => Require(!Redaction.Mask("failed\nINJECT").Contains('\n')));
Case("Защита аргументов от shell injection", () =>
{
    var args = IperfRunner.BuildIperf3Arguments(ServerCatalog.LoadPublished()[0], new TestOptions(), IPAddress.Parse("8.8.8.8"));
    Require(args.Contains("-J") && !args.Any(x => x.Contains(';') || x.Contains("cmd.exe")));
});
Case("Суммарный лимит делится между потоками", () =>
{
    var args = IperfRunner.BuildIperf3Arguments(ServerCatalog.LoadPublished()[0], new TestOptions(ParallelStreams: 4, BandwidthLimitBitsPerSecond: 100_000_000), IPAddress.Parse("8.8.8.8"));
    Require(args[Array.IndexOf(args, "-b") + 1] == "25000000");
});
Case("Корректное направление аргумента reverse", () =>
{
    var args = IperfRunner.BuildIperf3Arguments(ServerCatalog.LoadPublished()[0], new TestOptions(Direction: TestDirection.Download), IPAddress.Parse("8.8.8.8"));
    Require(args.Contains("-R"));
});
Case("История и отчёт: round-trip", () =>
{
    var dir = Path.Combine(Path.GetTempPath(), "speedforge-tests-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new RunStore(dir);
        var run = Result(); run.Group = "Company"; run.ResolvedIp = "192.168.10.1";
        store.AppendAsync(run, CancellationToken.None).GetAwaiter().GetResult();
        var loaded = store.Recent(1).Single();
        Require(loaded.RunId == run.RunId && loaded.ResolvedIp == run.ResolvedIp);
        Require(ReportExport.TechnicalReport(loaded).Contains("192.168.10.1"));
    }
    finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
});
Case("iPerf3 JSON array отклонён без исключения", () => { var r = Result(); Iperf3Parser.Apply("[]", r); Require(r.Status == RunStatus.EngineError); });
Case("iPerf2 NaN в потерях отклонён", () =>
{
    var r = Result(TransportKind.Udp);
    Iperf2Parser.Apply("20260101010101,1.2.3.4,5151,5.6.7.8,5001,1,0.0-8.0,10000000,10000000,NaN,1,200,NaN", r);
    Require(r.Status == RunStatus.InsufficientData);
});
Case("Ошибка записи истории не блокирует следующий запуск", () =>
{
    var dir = Path.Combine(Path.GetTempPath(), "speedforge-tests-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new RunStore(dir);
        Directory.CreateDirectory(Path.Combine(dir, "runs.jsonl"));
        var coordinator = new RunCoordinator(new TrustedTools(dir), store);
        var server = ServerCatalog.Custom("127.0.0.1", 5001, EngineKind.Iperf2);
        for (int i = 0; i < 2; i++)
        {
            var run = coordinator.RunAsync(server, new TestOptions(Direction: TestDirection.Download), null, null, CancellationToken.None).GetAwaiter().GetResult();
            Require(run.Status == RunStatus.Unsupported);
        }
    }
    finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
});
if (args.Contains("--http"))
{
    foreach (var direction in new[] { TestDirection.Upload, TestDirection.Download })
        Case("HTTP максимум превышает 100 Мбит/с " + direction, () =>
        {
            var server = ServerCatalog.Custom("127.0.0.1", 5088, EngineKind.Http) with
            { Http = new HttpProfile("http://127.0.0.1:5088/download?bytes=1048576", "http://127.0.0.1:5088/upload", "LibreSpeed") };
            var run = new RunResult();
            new PublicHttpMeasurement().ExecuteAsync(server, new TestOptions(Direction: direction, DurationSeconds: 3, ParallelStreams: 2, AllowPrivateTargets: true),
                IPAddress.Loopback, run, CancellationToken.None).GetAwaiter().GetResult();
            Require(run.Status == RunStatus.Measured && run.Mbps > 100, run.ErrorText ?? $"{run.Status}: {run.Mbps} Мбит/с");
            Console.WriteLine($"      {run.Mbps:F1} Мбит/с");
        });
    foreach (var direction in new[] { TestDirection.Upload, TestDirection.Download })
        Case("Публичный HTTP движок: два потока и общий лимит " + direction, () =>
        {
            var server = ServerCatalog.Custom("127.0.0.1", 5088, EngineKind.Http) with
            { Http = new HttpProfile("http://127.0.0.1:5088/download?bytes=262144", "http://127.0.0.1:5088/upload", "LibreSpeed") };
            var run = new RunResult();
            var options = new TestOptions(Direction: direction, DurationSeconds: 3, ParallelStreams: 2,
                BandwidthLimitBitsPerSecond: 1_000_000, AllowPrivateTargets: true);
            new PublicHttpMeasurement().ExecuteAsync(server, options, IPAddress.Loopback, run, CancellationToken.None).GetAwaiter().GetResult();
            Require(run.Status == RunStatus.Measured, run.ErrorText ?? run.Status.ToString());
            Require(run.Mbps is > 0 and < 1.5 && run.Intervals.Count > 0, "Нет измерения/нарушен общий лимит");
        });
    foreach (var direction in new[] { TestDirection.Upload, TestDirection.Download })
        Case("HTTP loopback: лимит 1 Мбит/с " + direction, () =>
        {
            var run = new RunResult();
            var options = new TestOptions(Direction: direction, DurationSeconds: 3, BandwidthLimitBitsPerSecond: 1_000_000, AllowPrivateTargets: true);
            new HttpMeasurement().ExecuteAsync(ServerCatalog.Custom("127.0.0.1", 5088, EngineKind.Http), options, IPAddress.Loopback, run, null, CancellationToken.None).GetAwaiter().GetResult();
            Require(run.Status == RunStatus.Measured, run.ErrorCode ?? run.Status.ToString());
            Require(run.ReceiverBytes is > 0 and <= 375_000 && run.Mbps is > 0 and <= 1.05, "Лимит данных/скорости нарушен");
        });
    Case("HTTP отмена во время передачи", () =>
    {
        var run = new RunResult();
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        new HttpMeasurement().ExecuteAsync(ServerCatalog.Custom("127.0.0.1", 5088, EngineKind.Http), new TestOptions(AllowPrivateTargets: true, BandwidthLimitBitsPerSecond: 1_000_000), IPAddress.Loopback, run, null, stop.Token).GetAwaiter().GetResult();
        Require(run.Status == RunStatus.Cancelled && run.Mbps == null);
    });
    Case("HTTP upload 32 МиБ: сервер принимает блоки до лимита сессии", () =>
    {
        var run = new RunResult();
        new HttpMeasurement().ExecuteAsync(ServerCatalog.Custom("127.0.0.1", 5088, EngineKind.Http),
            new TestOptions(DurationSeconds: 3, AllowPrivateTargets: true, BandwidthLimitBitsPerSecond: 1_000_000_000),
            IPAddress.Loopback, run, null, CancellationToken.None).GetAwaiter().GetResult();
        Require(run.Status == RunStatus.Measured, run.ErrorCode ?? run.Status.ToString());
        Require(run.ReceiverBytes is >= 32 * 1_048_576 and <= 256L * 1_048_576);
    });
}
if (args.Contains("--iperf"))
{
    foreach (var transport in new[] { TransportKind.Tcp, TransportKind.Udp })
        foreach (var direction in new[] { TestDirection.Upload, TestDirection.Download })
            Case($"iPerf3 максимум превышает 100 Мбит/с {transport} {direction}", () =>
            {
                string directory = Path.Combine(Path.GetTempPath(), "speedforge-max3-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var coordinator = new RunCoordinator(new TrustedTools(directory), new RunStore(directory));
                    var run = coordinator.RunAsync(ServerCatalog.Custom("127.0.0.1", 55201, EngineKind.Iperf3),
                        // Cygwin's Windows loopback server cannot negotiate multiple UDP sockets reliably.
                        new TestOptions(Direction: direction, Transport: transport, DurationSeconds: 3, ParallelStreams: transport == TransportKind.Udp ? 1 : 2, MeasureLatency: false, AllowPrivateTargets: true),
                        null, null, CancellationToken.None).GetAwaiter().GetResult();
                    Require(run.Status == RunStatus.Measured && run.Mbps > 100, (run.ErrorText ?? $"{run.Status}: {run.Mbps}") + "\n" + RunDiagnostics.Read(run.DebugDirectory));
                    Console.WriteLine($"      {run.Mbps:F1} Мбит/с");
                }
                finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            });
    Case("Встроенный iPerf3: reverse, резервный порт, старый каталог не блокирует направление", () =>
    {
        var dir = Path.Combine(Path.GetTempPath(), "speedforge-iperf-" + Guid.NewGuid().ToString("N"));
        try
        {
            var server = ServerCatalog.Custom("127.0.0.1", 55200, EngineKind.Iperf3) with { Ports = [55200, 55201], PermittedDirections = ["Upload"] };
            var coordinator = new RunCoordinator(new TrustedTools(dir), new RunStore(dir));
            var run = coordinator.RunAsync(server, new TestOptions(Direction: TestDirection.Download, DurationSeconds: 3, ParallelStreams: 2,
                AllowPrivateTargets: true, MeasureLatency: false, BandwidthLimitBitsPerSecond: 1_000_000), null, null, CancellationToken.None).GetAwaiter().GetResult();
            Require(run.Status == RunStatus.Measured && run.Port == 55201 && run.Mbps is > 0 and <= 1.5,
                run.ErrorText ?? run.ErrorCode ?? run.Status.ToString());
            Require(run.EngineSha256 != null && run.Intervals.Count >= 3);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    });
}
Case("Диагностика сохраняет обрезку stdout и stderr", () =>
{
    var outcome = ExternalProcess.RunAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe"),
        [typeof(Program).Assembly.Location, "--emit-output"], TimeSpan.FromSeconds(10), CancellationToken.None).GetAwaiter().GetResult();
    Require(outcome.ExitCode == 0 && outcome.Stdout.Length == 4_000_000 && outcome.StdoutTruncated && outcome.Stderr.Contains("stderr-marker"));
});
if (args.Contains("--iperf2"))
{
    foreach (var direction in new[] { TestDirection.Upload, TestDirection.Download })
        Case("iPerf2 максимум превышает 100 Мбит/с " + direction, () =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "speedforge-max2-" + Guid.NewGuid().ToString("N"));
            try
            {
                var coordinator = new RunCoordinator(new TrustedTools(directory), new RunStore(directory));
                var run = coordinator.RunAsync(ServerCatalog.Custom("127.0.0.1", 55202, EngineKind.Iperf2),
                    new TestOptions(Direction: direction, Transport: TransportKind.Udp, DurationSeconds: 3, MeasureLatency: false, AllowPrivateTargets: true),
                    null, null, CancellationToken.None).GetAwaiter().GetResult();
                Require(run.Status == RunStatus.Measured && run.Mbps > 100, (run.ErrorText ?? $"{run.Status}: {run.Mbps}") + "\n" + RunDiagnostics.Read(run.DebugDirectory));
                Console.WriteLine($"      {run.Mbps:F1} Мбит/с");
                if (direction == TestDirection.Download) Require(run.Limitations.Any(x => x.Contains("100 Гбит/с")));
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        });
    foreach (var direction in new[] { TestDirection.Upload, TestDirection.Download })
        Case("Встроенный iPerf2 UDP " + direction, () =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "speedforge-iperf2-" + Guid.NewGuid().ToString("N"));
            try
            {
                var coordinator = new RunCoordinator(new TrustedTools(directory), new RunStore(directory));
                var server = ServerCatalog.Custom("127.0.0.1", 55202, EngineKind.Iperf2);
                var options = new TestOptions(Direction: direction, Transport: TransportKind.Udp, DurationSeconds: 3,
                    BandwidthLimitBitsPerSecond: 1_000_000, MeasureLatency: false, AllowPrivateTargets: true);
                var result = coordinator.RunAsync(server, options, null, null, CancellationToken.None).GetAwaiter().GetResult();
                Require(result.Status == RunStatus.Measured && result.ReceiverBytes > 0,
                    (result.ErrorText ?? result.Status.ToString()) + "\n" + RunDiagnostics.Read(result.DebugDirectory));
                Require(result.EngineSha256 == BundledIperf.Iperf2Sha256 && File.Exists(Path.Combine(result.DebugDirectory!, "trace.json")));
                Require(File.ReadAllText(Path.Combine(result.DebugDirectory!, "trace.json")).Contains("--version"));
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        });
    Case("Таймаут сохраняет частичный stdout процесса", () =>
    {
        var tool = BundledIperf.RequireAsync(CancellationToken.None, EngineKind.Iperf2).GetAwaiter().GetResult();
        var outcome = ExternalProcess.RunAsync(tool.Path, ["-c", "127.0.0.1", "-p", "55202", "-u", "-b", "1000000", "-t", "10", "-i", "1"],
            TimeSpan.FromMilliseconds(1500), CancellationToken.None).GetAwaiter().GetResult();
        Require(outcome.TimedOut && !outcome.Cancelled && outcome.Stdout.Contains("Client connecting"));
    });
}
Console.WriteLine($"Total: {passed + failed}; PASS: {passed}; FAIL: {failed}");
return failed == 0 ? 0 : 1;
