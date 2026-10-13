using SpeedForge.Core;
using System.Text.Json;
using System.Text.Encodings.Web;

internal static class SandboxTests
{
    public static async Task<int> RunAsync(string directory, bool iperf2Only = false, bool workingPortOnly = false)
    {
        Directory.CreateDirectory(directory);
        var cases = new List<object>();
        int passed = 0, failed = 0;
        async Task Check(string name, Func<Task> action)
        {
            if (workingPortOnly && name != "Подтверждённый резервный порт используется следующим направлением") return;
            if (iperf2Only && !name.Contains("iPerf2", StringComparison.Ordinal)) return;
            try { await action(); passed++; cases.Add(new { Name = name, Passed = true }); Console.WriteLine("PASS  " + name); }
            catch (Exception e) { failed++; cases.Add(new { Name = name, Passed = false, Error = e.ToString() }); Console.WriteLine("FAIL  " + name + " — " + e); }
        }
        void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
        ServerEntry Http(string scenario, bool declaredUpload = true) => ServerCatalog.Custom("127.0.0.1", 5090, EngineKind.Http) with
        { Id = "http-" + scenario, Http = new("http://127.0.0.1:5090/sandbox/" + scenario + "/download", declaredUpload ? "http://127.0.0.1:5090/sandbox/" + scenario + "/upload" : null, "LibreSpeed") };
        TestOptions options = new(DurationSeconds: 3, BandwidthLimitBitsPerSecond: 10_000_000, MeasureLatency: false, AllowPrivateTargets: true, TimeoutSeconds: 8);
        RunCoordinator Coordinator(string name) => new(new TrustedTools(), new RunStore(Path.Combine(directory, name)));
        async Task<IReadOnlyList<RunResult>> Pair(ServerEntry server, string name, TestOptions? custom = null)
        {
            var coordinator = Coordinator(name);
            var runs = await DirectionalTest.RunAsync(server, custom ?? options, true,
                next => coordinator.RunAsync(server, next, null, null, CancellationToken.None), CancellationToken.None);
            await File.WriteAllTextAsync(Path.Combine(directory, name + ".json"), JsonSerializer.Serialize(runs, Json));
            return runs;
        }
        foreach (var item in new[] { ("both", DirectionVerdict.Both), ("download-only", DirectionVerdict.DownloadObserved), ("upload-only", DirectionVerdict.UploadObserved) })
            await Check("HTTP автоматически: " + item.Item1, async () =>
            {
                var runs = await Pair(Http(item.Item1), item.Item1);
                Require(runs.Count == 2 && DirectionObservation.From(runs).Verdict == item.Item2, JsonSerializer.Serialize(runs, Json));
                Require(runs.All(x => File.Exists(Path.Combine(x.DebugDirectory!, "trace.json"))), "Отсутствует trace");
            });
        await Check("HTTP файл без upload: один реальный тест загрузки", async () =>
        {
            var runs = await Pair(Http("download-only", false), "declared-download-only");
            Require(runs.Count == 1 && runs[0].Direction == TestDirection.Download && runs[0].Status == RunStatus.Measured, "План HTTP файла неверен");
        });
        foreach (var scenario in new[] { "html", "json", "compressed", "redirect", "empty", "busy" })
            await Check("HTTP не выдаёт ложную скорость: " + scenario, async () =>
            {
                var run = await Coordinator("reject-" + scenario).RunAsync(Http(scenario), options with { Direction = TestDirection.Download }, null, null, CancellationToken.None);
                Require(!DirectionObservation.Confirmed(run) && run.Mbps == null && run.ReceiverBytes == null, JsonSerializer.Serialize(run, Json));
                if (scenario == "redirect") Require(RunDiagnostics.Read(run.DebugDirectory).Contains("302"), "Нет исходного 302 в журнале");
            });
        await Check("HTTP обрыв после данных: Partial, не полный успех", async () =>
        {
            var run = await Coordinator("partial").RunAsync(Http("partial"), options with { Direction = TestDirection.Download }, null, null, CancellationToken.None);
            Require(run.Status == RunStatus.Partial && run.ReceiverBytes > 0 && !DirectionObservation.Confirmed(run), JsonSerializer.Serialize(run, Json));
        });
        await Check("HTTPS не отключает проверку сертификата", async () =>
        {
            var server = ServerCatalog.Custom("127.0.0.1", 5091, EngineKind.Http) with { Http = new("https://127.0.0.1:5091/download") };
            var run = await Coordinator("bad-tls").RunAsync(server, options with { Direction = TestDirection.Download }, null, null, CancellationToken.None);
            Require(run.Status == RunStatus.NetworkError && run.Mbps == null && run.ReceiverBytes == null, JsonSerializer.Serialize(run, Json));
            Require(RunDiagnostics.Read(run.DebugDirectory).Contains("SSL", StringComparison.OrdinalIgnoreCase), "Нет TLS-ошибки в полном журнале");
        });
        await Check("HTTP зависание: Timeout, не пользовательская отмена", async () =>
        {
            var run = await Coordinator("deadline").RunAsync(Http("stall"), options with { Direction = TestDirection.Download, TimeoutSeconds = 5 }, null, null, CancellationToken.None);
            Require(run.Status == RunStatus.Timeout && run.ErrorCode == "HTTP_TEST_TIMEOUT", JsonSerializer.Serialize(run, Json));
        });
        await Check("iPerf2 ожидание reverse: общий срок, не пользовательская отмена", async () =>
        {
            var run = await Coordinator("native-deadline").RunAsync(ServerCatalog.Custom("127.0.0.1", 55208, EngineKind.Iperf2),
                options with { Direction = TestDirection.Download, Transport = TransportKind.Udp, TimeoutSeconds = 5 }, null, null, CancellationToken.None);
            Require(run.Status == RunStatus.Timeout && run.ErrorCode == "START_TIMEOUT"
                && run.Stages.All(x => x.Stage != RunStage.Transferring), JsonSerializer.Serialize(run, Json));
        });
        await Check("Отмена первого направления не запускает второе", async () =>
        {
            var coordinator = Coordinator("cancel");
            using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            var runs = await DirectionalTest.RunAsync(Http("stall"), options, true,
                next => coordinator.RunAsync(Http("stall"), next, null, null, stop.Token), stop.Token);
            Require(runs.Count == 1 && runs[0].Status == RunStatus.Cancelled, "После отмены запустилось второе направление");
        });
        foreach (var item in new[] { (55201, "iperf3-both", DirectionVerdict.Both), (55203, "iperf3-upload-only", DirectionVerdict.UploadObserved), (55204, "iperf3-download-only", DirectionVerdict.DownloadObserved), (55205, "iperf3-neither", DirectionVerdict.Unconfirmed) })
            await Check("Настоящий iPerf3: " + item.Item2, async () =>
            {
                var runs = await Pair(ServerCatalog.Custom("127.0.0.1", item.Item1, EngineKind.Iperf3), item.Item2);
                Require(runs.Count == 2 && DirectionObservation.From(runs).Verdict == item.Item3, JsonSerializer.Serialize(runs, Json));
                Require(runs.All(x => x.EngineSha256 == BundledIperf.ExecutableSha256), "Использован иной измеритель");
            });
        await Check("Сброс control-соединения перебирает резервный порт", async () =>
        {
            var server = ServerCatalog.Custom("127.0.0.1", 55203, EngineKind.Iperf3) with { Ports = [55203, 55201] };
            var run = await Coordinator("reset-fallback").RunAsync(server, options with { Direction = TestDirection.Download }, null, null, CancellationToken.None);
            Require(run.Status == RunStatus.Measured && run.Port == 55201, JsonSerializer.Serialize(run, Json));
            Require(File.Exists(Path.Combine(run.DebugDirectory!, "process-03-stdout.txt")), "Не записаны обе попытки");
        });
        await Check("iPerf3 ручной TCP bidir: обе приёмные скорости", async () =>
        {
            var run = await Coordinator("bidir").RunAsync(ServerCatalog.Custom("127.0.0.1", 55201, EngineKind.Iperf3), options with { Direction = TestDirection.Bidirectional }, null, null, CancellationToken.None);
            Require(run.Status == RunStatus.Measured && run.UploadMbps > 0 && run.DownloadMbps > 0, JsonSerializer.Serialize(run, Json));
        });
        await Check("iPerf3 UDP: до отчёта сервера не публикуется локальная скорость отправки", async () =>
        {
            var samples = new System.Collections.Concurrent.ConcurrentBag<MeasurementProgress>();
            var run = await Coordinator("udp-upload-provenance").RunAsync(ServerCatalog.Custom("127.0.0.1", 55201, EngineKind.Iperf3),
                options with { Direction = TestDirection.Upload, Transport = TransportKind.Udp }, null, null, CancellationToken.None,
                new InlineProgress<MeasurementProgress>(samples.Add));
            Require(run.Status == RunStatus.Measured && run.UploadMbps > 0 && samples.IsEmpty && run.Intervals.Count == 0,
                "В карточку или график переданы локальные UDP-интервалы отправителя");
        });
        await Check("iPerf2 без ACK сохраняет TX, не подтверждает приём", async () =>
        {
            var run = await Coordinator("no-ack").RunAsync(ServerCatalog.Custom("127.0.0.1", 55208, EngineKind.Iperf2),
                options with { Direction = TestDirection.Upload, Transport = TransportKind.Udp, TimeoutSeconds = 12, BandwidthLimitBitsPerSecond = 1_000_000 }, null, null, CancellationToken.None);
            Require(run.Status == RunStatus.Partial && run.SenderMbps > 0 && run.ReceiverBytes == null
                && run.UploadMbps == null && run.Mbps == null && !DirectionObservation.Confirmed(run), JsonSerializer.Serialize(run, Json));
        });
        await Check("Настоящий iPerf2: канал 100 Мбит/с не выдаёт отправленный гигабит", async () =>
        {
            var run = await Coordinator("iperf2-100mbit").RunAsync(ServerCatalog.Custom("127.0.0.1", 55209, EngineKind.Iperf2),
                options with { Direction = TestDirection.Upload, Transport = TransportKind.Udp, BandwidthLimitBitsPerSecond = 1_000_000_000 }, null, null, CancellationToken.None);
            Require(run.Status == RunStatus.Measured && run.SenderMbps > 500 && run.UploadMbps is > 0 and < 110
                && run.UploadMbps == run.ReceiverMbps && run.LostPercent > 50, JsonSerializer.Serialize(run, Json));
            await File.WriteAllTextAsync(Path.Combine(directory, "iperf2-100mbit.json"), JsonSerializer.Serialize(run, Json));
            Console.WriteLine($"      TX={run.SenderMbps:F1}; приём={run.ReceiverMbps:F1} Мбит/с; потери={run.LostPercent:F1}%");
        });
        await Check("Настоящий iPerf2: исходные 12 профилей без reverse", async () =>
        {
            var server = ServerCatalog.LoadPublished().Single(x => x.Id == "beeline-iperf2") with { Host = "127.0.0.1", Port = 55214, Ports = [55214] };
            var runs = await Pair(server, "beeline-profiles", options with { Transport = TransportKind.Udp });
            Require(runs.Count == 12 && runs.Take(12).All(x => x.Status == RunStatus.Measured && x.ReceiverMbps > 0)
                && runs.All(x => x.Direction == TestDirection.Upload), JsonSerializer.Serialize(runs, Json));
            Require(runs.Take(12).All(x => x.EngineSha256 == BundledIperf.Iperf2LegacySha256)
                && DirectionObservation.From(runs).Verdict == DirectionVerdict.UploadObserved, "Использован несовместимый клиент или выдумана загрузка старого сервера");
            Require(runs.Take(12).Select(x => x.Options.ParallelStreams).SequenceEqual(new[] {1,5,10,20,1,5,10,20,1,5,10,20}), "Потоки профилей не совпадают с архивом");
        });
        await Check("Билайн: совместимый клиент на канале 250 Мбит/с не ограничен до 100", async () =>
        {
            var server = ServerCatalog.Custom("127.0.0.1", 55215, EngineKind.Iperf2) with { Iperf2Legacy = true };
            var run = await Coordinator("beeline-legacy-capacity").RunAsync(server,
                options with { Direction = TestDirection.Upload, Transport = TransportKind.Udp, BandwidthLimitBitsPerSecond = 1_000_000_000 },
                null, null, CancellationToken.None);
            Require(run.Status == RunStatus.Measured && run.EngineSha256 == BundledIperf.Iperf2LegacySha256
                && run.ReceiverMbps > 100 && run.ReceiverMbps < 275 && run.SenderMbps > 500, JsonSerializer.Serialize(run, Json));
        });
        await Check("Билайн: обновлённый сервер автоматически выбирает современный клиент", async () =>
        {
            var server = ServerCatalog.LoadPublished().Single(x => x.Id == "beeline-iperf2") with { Host = "127.0.0.1", Port = 55210, Ports = [55210] };
            var coordinator = Coordinator("beeline-modern-compatibility");
            var first = await coordinator.RunAsync(server, options with { Direction = TestDirection.Upload, Transport = TransportKind.Udp,
                Iperf2OfferedBitsPerSecondPerStream = 100_000_000, ProbeIperf2Client = true, TimeoutSeconds = 25 }, null, null, CancellationToken.None);
            var second = await coordinator.RunAsync(server, options with { Direction = TestDirection.Upload, Transport = TransportKind.Udp,
                Iperf2OfferedBitsPerSecondPerStream = 100_000_000, ParallelStreams = 5 }, null, null, CancellationToken.None);
            Require(first.Status == RunStatus.Measured && second.Status == RunStatus.Measured
                && first.EngineSha256 == BundledIperf.Iperf2Sha256 && second.EngineSha256 == BundledIperf.Iperf2Sha256
                && second.ReceiverStreams == 5 && !first.Limitations.Any(x => x.StartsWith("TX-only:")), JsonSerializer.Serialize(new[] { first, second }, Json));
            Require(Directory.GetFiles(first.DebugDirectory!, "process-*-stdout.txt").Length == 4
                && Directory.GetFiles(second.DebugDirectory!, "process-*-stdout.txt").Length == 1, "Выбор клиента или запрос версии повторяется перед каждым профилем");
        });
        foreach (bool userCancelled in new[] { false, true })
            await Check("iPerf2: завершённый TX при " + (userCancelled ? "отмене совместимости" : "общем таймауте совместимости"), async () =>
            {
                var server = ServerCatalog.Custom("127.0.0.1", 55208, EngineKind.Iperf2) with { Iperf2Legacy = true };
                using var stop = new CancellationTokenSource();
                if (userCancelled) stop.CancelAfter(TimeSpan.FromSeconds(9));
                var run = await Coordinator("iperf2-compatibility-stop-" + userCancelled).RunAsync(server,
                    options with { Direction = TestDirection.Upload, Transport = TransportKind.Udp,
                        BandwidthLimitBitsPerSecond = 1_000_000, ProbeIperf2Client = true, TimeoutSeconds = userCancelled ? 25 : 9 },
                    null, null, stop.Token);
                Require(run.SenderMbps > 0 && run.ReceiverMbps == null
                    && run.Status == (userCancelled ? RunStatus.Cancelled : RunStatus.Partial)
                    && run.ErrorCode == (userCancelled ? "USER_CANCELLED" : "IPERF2_RECEIVER_UNCONFIRMED"), JsonSerializer.Serialize(run, Json));
                Require(Directory.GetFiles(run.DebugDirectory!, "process-*-stdout.txt").Length == 4,
                    "Таймаут не пришёл на повторную проверку клиента");
            });
        foreach (var capacity in new[] { (Port: 55212, Mbps: 40), (Port: 55213, Mbps: 300) })
            await Check($"iPerf2: приём на канале {capacity.Mbps} Мбит/с без привязки к тарифу 100", async () =>
            {
                var run = await Coordinator("iperf2-capacity-" + capacity.Mbps).RunAsync(
                    ServerCatalog.Custom("127.0.0.1", capacity.Port, EngineKind.Iperf2),
                    options with { Direction = TestDirection.Upload, Transport = TransportKind.Udp, BandwidthLimitBitsPerSecond = 1_000_000_000 },
                    null, null, CancellationToken.None);
                Require(run.Status == RunStatus.Measured && run.ReceiverMbps > capacity.Mbps * 0.4
                    && run.ReceiverMbps < capacity.Mbps * 1.1 && run.SenderMbps > run.ReceiverMbps * 1.5,
                    JsonSerializer.Serialize(run, Json));
                Console.WriteLine($"      TX={run.SenderMbps:F1}; RX={run.ReceiverMbps:F1}; заданная ёмкость={capacity.Mbps} Мбит/с");
            });
        await Check("Подтверждённый резервный порт используется следующим направлением", async () =>
        {
            var coordinator = Coordinator("working-port-cache");
            // A closed first port isolates caching from asynchronous teardown of rejected proxy sessions.
            // Control-reset fallback is exercised by the separate test above.
            var server = ServerCatalog.Custom("127.0.0.1", 55206, EngineKind.Iperf3) with { Ports = [55206, 55201] };
            var first = await coordinator.RunAsync(server, options with { Direction = TestDirection.Download }, null, null, CancellationToken.None);
            var second = await coordinator.RunAsync(server, options with { Direction = TestDirection.Upload }, null, null, CancellationToken.None);
            Require(first.Status == RunStatus.Measured && first.Port == 55201 && second.Status == RunStatus.Measured && second.Port == 55201,
                JsonSerializer.Serialize(new[] { first, second }, Json));
            Require(Directory.GetFiles(second.DebugDirectory!, "process-*-stdout.txt").Length == 1, "Повторяется перебор уже проверенных портов или запрос версии");
        });
        await Check("Сбой создания debug не блокирует следующий запуск", async () =>
        {
            string root = Path.Combine(directory, "debug-failure");
            var store = new RunStore(root);
            var coordinator = new RunCoordinator(new TrustedTools(), store);
            File.WriteAllText(Path.Combine(root, "debug"), "occupied");
            var first = await coordinator.RunAsync(Http("both"), options with { Direction = TestDirection.Download }, null, null, CancellationToken.None);
            Require(first.Status == RunStatus.Measured && first.DebugDirectory == null && first.Limitations.Any(x => x.Contains("debug не сохранён")),
                "Измерение прервано или ошибка записи debug скрыта");
            File.Delete(Path.Combine(root, "debug"));
            var second = await coordinator.RunAsync(Http("both"), options with { Direction = TestDirection.Download }, null, null, CancellationToken.None);
            Require(second.Status == RunStatus.Measured, "Семафор остался заблокированным");
        });
        await File.WriteAllTextAsync(Path.Combine(directory, "sandbox-tests.json"), JsonSerializer.Serialize(new { passed, failed, cases }, Json));
        Console.WriteLine($"Sandbox total: {passed + failed}; PASS: {passed}; FAIL: {failed}");
        return failed == 0 ? 0 : 1;
    }
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private sealed class InlineProgress<T>(Action<T> receive) : IProgress<T> { public void Report(T value) => receive(value); }
}
