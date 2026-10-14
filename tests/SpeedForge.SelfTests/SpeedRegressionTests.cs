using SpeedForge.Core;

internal static class SpeedRegressionTests
{
    public static void Register(Action<string, Action> test)
    {
        void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        test("Билайн: тариф делится между потоками, а reverse отсутствует", () =>
        {
            var server = ServerCatalog.LoadPublished().Single(x => x.Id == "beeline-iperf2");
            foreach (int tariff in new[] { 50,100,300,350,500,800,1000,2000 })
            {
                var options = BeelineTest.Configure(new TestOptions(DurationSeconds: 10), tariff); options.Validate();
                var args = IperfRunner.BuildIperf2Arguments(server, options, System.Net.IPAddress.Loopback);
                Require(long.Parse(args[Array.IndexOf(args, "-b") + 1]) * options.ParallelStreams == (long)tariff * 1_000_000
                    && !options.UseIperf2Profiles && !options.ProbeIperf2Client && options.Iperf2SenderOnly, "Тариф применён к каждому потоку или включён перебор");
                Require(DirectionalTest.Plan(server, options, true).SequenceEqual(new[] { TestDirection.Upload }), "Запускается входящая проверка");
            }
        });
        test("Билайн: отправка без приёмника не становится подтверждённой скоростью", () =>
        {
            var run = new RunResult { Options = BeelineTest.Configure(new TestOptions(), 100), Status = RunStatus.Measured,
                SenderMbps = 99, Mbps = 99, UploadMbps = 99 };
            Require(!DirectionObservation.Confirmed(run) && DirectionObservation.From(new[] { run }).Verdict == DirectionVerdict.Sent,
                "Локальная отправка выдана за подтверждение линии");
        });
        test("iPerf3: реальная скорость 0,026 Мбит/с сохраняется", () =>
        {
            var run = new RunResult { Direction = TestDirection.Download };
            Iperf3Parser.Apply("""{"end":{"sum_received":{"bytes":32768,"bits_per_second":26000}}}""", run);
            Require(run.Status == RunStatus.Measured && run.DownloadMbps == .026 && run.DownloadReceiverBytes == 32768,
                "Низкая ненулевая передача ошибочно отброшена");
        });
        test("iPerf3 bidir: скорость без байтов второго получателя не является успехом", () =>
        {
            var run = new RunResult { Direction = TestDirection.Bidirectional };
            Iperf3Parser.Apply("""{"end":{"sum_received":{"bytes":32768,"bits_per_second":26000},"sum_received_bidir_reverse":{"bits_per_second":26000}}}""", run);
            Require(run.Status == RunStatus.InsufficientData && run.ErrorCode == "BIDIR_RECEIVER_MISSING", "Нет доказательства второй передачи");
        });
        test("iPerf3 bidir: ноль байт с ненулевой скоростью отвергается", () =>
        {
            var run = new RunResult { Direction = TestDirection.Bidirectional };
            Iperf3Parser.Apply("""{"end":{"sum_received":{"bytes":32768,"bits_per_second":26000},"sum_received_bidir_reverse":{"bytes":0,"bits_per_second":26000}}}""", run);
            Require(run.Status == RunStatus.NoData && run.ErrorCode == "ZERO_PAYLOAD" && run.DownloadMbps == null && run.UploadMbps == null,
                "Нулевая обратная передача объявлена успешной или попала в показатели");
        });
        test("iPerf3 bidir: две медленные передачи сохраняют свои байты", () =>
        {
            var run = new RunResult { Direction = TestDirection.Bidirectional };
            Iperf3Parser.Apply("""{"end":{"sum_received":{"bytes":32768,"bits_per_second":26000},"sum_received_bidir_reverse":{"bytes":16384,"bits_per_second":13000}}}""", run);
            Require(run.Status == RunStatus.Measured && run.UploadMbps == .026 && run.DownloadMbps == .013
                && run.UploadReceiverBytes == 32768 && run.DownloadReceiverBytes == 16384, "Данные двух направлений потеряны");
        });
        test("Бюджет времени оставляет передачу и отчёт даже после перебора 16 портов", () =>
        {
            var server = ServerCatalog.Custom("127.0.0.1", 5201, EngineKind.Iperf3) with { Ports = Enumerable.Range(5201,16).ToArray() };
            var options = new TestOptions(DurationSeconds: 30, MeasureLatency: true);
            Require(RunTiming.RunSeconds(server, options) == 120,
                "Не учтены подготовка, все порты, передача и итоговый отчёт");
            Require(RunTiming.CompatibilitySeconds(new TestOptions(DurationSeconds: 30)) <= 120, "Подбор клиента превышает общий предел");
        });
        test("iPerf2: интервал и полный итог различаются; один поток не подменяет SUM", () =>
        {
            Require(!Iperf2TextParser.IsSummary("[SUM] 1.0-2.0 sec 119 MBytes 1000 Mbits/sec", 3, 10)
                && Iperf2TextParser.IsSummary("[SUM] 0.0-3.0 sec 357 MBytes 1000 Mbits/sec", 3, 10)
                && Iperf2TextParser.Interval("[1] 0.0-1.0 sec 119 MBytes 1000 Mbits/sec", TestDirection.Download, 10) == null,
                "Интервал принят за завершение или график показывает один поток вместо общей скорости");
        });
        test("ICMP: медиана чётной выборки и джиттер соседних ответов", () =>
        {
            var now = DateTimeOffset.Now;
            LatencySample[] samples = [new(ProbePhase.Idle, now, 10, null), new(ProbePhase.Idle, now.AddSeconds(1), 14, null),
                new(ProbePhase.Idle, now.AddSeconds(2), 20, null), new(ProbePhase.Idle, now.AddSeconds(3), 16, null),
                new(ProbePhase.Upload, now.AddSeconds(4), 500, null)];
            Require(LatencyStatistics.Median(samples, ProbePhase.Idle) == 15
                && Math.Abs(LatencyStatistics.Jitter(samples, ProbePhase.Idle)!.Value - 14.0 / 3) < .001,
                "Смешаны фазы или джиттер рассчитан по отсортированным RTT");
        });
        test("ICMP: потеря разрывает пару, отсутствие выборки не становится нулём", () =>
        {
            var now = DateTimeOffset.Now;
            LatencySample[] samples = [new(ProbePhase.Idle, now, 10, null), new(ProbePhase.Idle, now.AddSeconds(1), null, "Timeout"),
                new(ProbePhase.Idle, now.AddSeconds(2), 50, null)];
            Require(LatencyStatistics.Jitter(samples, ProbePhase.Idle) == null && LatencyStatistics.Median([], ProbePhase.Idle) == null,
                "Потеря или пустая выборка превращены в достоверный джиттер");
        });
        test("Цветовые ориентиры различают ноль, плохие значения и отсутствие данных", () =>
        {
            Require(LatencyStatistics.LossQuality(0) == MetricQuality.Good
                && LatencyStatistics.LossQuality(2) == MetricQuality.Poor
                && LatencyStatistics.JitterQuality(10) == MetricQuality.Moderate
                && LatencyStatistics.PingQuality(null) == MetricQuality.Unknown
                && LatencyStatistics.JitterQuality(double.NaN) == MetricQuality.Unknown, "Неверные цветовые ориентиры");
        });
        test("История различает все 12 профилей Билайна и направление", () =>
        {
            var server = ServerCatalog.LoadPublished().Single(x => x.Id == "beeline-iperf2");
            var names = server.Iperf2Profiles.Select(x => new RunResult { Direction = TestDirection.Upload,
                Options = new TestOptions(Transport: TransportKind.Udp, ParallelStreams: x.Streams,
                    Iperf2OfferedBitsPerSecondPerStream: x.BitsPerSecondPerStream) }.ProfileLabel).ToArray();
            Require(names.SequenceEqual(new[] { "1(100)", "5(100)", "10(100)", "20(100)", "1(500)", "5(500)", "10(500)", "20(500)", "1(1000)", "5(1000)", "10(1000)", "20(1000)" }), "Профили не совпадают с архивом и Python");
        });
        test("Реальный вывод 10(100) пользователя: 954 Мбит/с SUM остаётся локальным TX", () =>
        {
            var run = new RunResult { Engine = EngineKind.Iperf2, Direction = TestDirection.Upload, Transport = TransportKind.Udp,
                Options = new TestOptions(DurationSeconds: 10, ParallelStreams: 10, Transport: TransportKind.Udp,
                    Iperf2OfferedBitsPerSecondPerStream: 100_000_000) };
            Iperf2TextParser.Apply("[916]  0.0-10.0 sec   114 MBytes  95.4 Mbits/sec\n[916] Sent 81145 datagrams\n"
                + "[904]  0.0-10.0 sec   114 MBytes  95.4 Mbits/sec\n[SUM]  0.0-10.0 sec  1.11 GBytes   954 Mbits/sec   0.000 ms    0/811479 (0%)\n[SUM] Sent 811479 datagrams", run);
            Require(run.SenderMbps == 954 && run.ReceiverMbps == null && run.JitterMilliseconds == null
                && run.LostPercent == null && run.StatusLabel == "Нет отчёта сервера", "Локальная строка SUM стала скоростью канала или нулевыми потерями");
        });
        test("Старый iPerf2: SUM с нулевыми потерями без Server Report остаётся неподтверждённым", () =>
        {
            // The archive client prints zero loss fields in its local sender SUM,
            // even when all five remote receiver reports are missing.
            var run = new RunResult { Direction = TestDirection.Upload, Options = new TestOptions(DurationSeconds: 10,
                ParallelStreams: 5, Transport: TransportKind.Udp, Iperf2OfferedBitsPerSecondPerStream: 100_000_000) };
            Iperf2TextParser.Apply("[SUM]  0.0-10.0 sec  625000000 Bytes  500000000 bits/sec   0.000 ms    0/425000 (0%)", run);
            Require(run.Status == RunStatus.Partial && run.SenderMbps == 500 && run.ReceiverMbps == null
                && run.UploadMbps == null && run.Intervals.Count == 0 && run.StatusLabel == "Нет отчёта сервера", "Локальный SUM принят за приём сервера");
        });
        test("iPerf2: один ACK из пяти не становится полным результатом", () =>
        {
            var run = new RunResult { Direction = TestDirection.Upload, Options = new TestOptions(DurationSeconds: 10,
                ParallelStreams: 5, Transport: TransportKind.Udp, Iperf2OfferedBitsPerSecondPerStream: 100_000_000) };
            Iperf2TextParser.Apply("[SUM] 0.0-10.0 sec 625000000 Bytes 500000000 bits/sec\n[ 1] Server Report:\n[ 1] 0.0-10.0 sec 125000000 Bytes 100000000 bits/sec 0.1 ms 0/10000 (0%)", run);
            Require(run.Status == RunStatus.Partial && run.ReceiverStreams == 1 && run.ReceiverMbps == 100
                && run.ErrorText!.Contains("1 из 5") && !DirectionObservation.Confirmed(run), "Неполный отчёт выдан за полный");
        });
        test("iPerf2: повреждённое чтение старого ACK не превращается в гигантскую скорость", () =>
        {
            var run = new RunResult { Direction = TestDirection.Upload, Options = new TestOptions(DurationSeconds: 10) };
            Iperf2TextParser.Apply("[ 1] 0.00-10.00 sec 130960830 Bytes 104726145 bits/sec\n[ 1] Server Report:\n[ 1] 0.00-4357.00 sec 562466168305090560 Bytes 1032758628737197 bits/sec 0.000 ms 0/0 (0%)", run);
            Require(run.Status == RunStatus.EngineError && run.ErrorCode == "IPERF2_RECEIVER_REPORT_INVALID"
                && run.ReceiverMbps == null && run.UploadMbps == null && run.SenderMbps > 0, "Некорректный Server Report принят за результат");
        });
        test("iPerf2: ошибочная длительность старого ACK отклоняется даже при правдоподобной скорости", () =>
        {
            var run = new RunResult { Direction = TestDirection.Upload, Options = new TestOptions(DurationSeconds: 3) };
            Iperf2TextParser.Apply("[1] Server Report:\n[1] 0.00-900000.00 sec 160000000000000 Bytes 1422222222 bits/sec 0.0 ms 0/0 (0%)", run);
            Require(run.Status == RunStatus.EngineError && run.ReceiverMbps == null, "Отчёт на 900000 секунд принят за трёхсекундный тест");
        });
        test("iPerf3 UDP: график не использует интервалы отправителя вместо приёма", () =>
        {
            var run = new RunResult { Direction = TestDirection.Upload, Transport = TransportKind.Udp };
            Iperf3Parser.Apply("""
                {"intervals":[{"sum":{"start":0,"end":1,"bytes":125000000,"bits_per_second":1000000000,"sender":true}}],"end":{"sum_sent":{"bytes":1000000000,"bits_per_second":1000000000},"sum_received":{"bytes":250000000,"bits_per_second":250000000,"lost_packets":750,"packets":1000,"lost_percent":75}}}
                """, run);
            Require(run.Status == RunStatus.Measured && run.UploadMbps == 250 && run.Intervals.Count == 0 && run.SenderMbps == 1000,
                "График мог показать 1 Гбит/с вместо принятого сервером 250 Мбит/с");
        });
        test("Общий замер задержки не повторяется перед каждым профилем", () =>
        {
            var server = ServerCatalog.LoadPublished().Single(x => x.Id == "beeline-iperf2");
            var options = new List<TestOptions>();
            var runs = DirectionalTest.RunAsync(server, new TestOptions(Transport: TransportKind.Udp), true,
                next => { options.Add(next); return Task.FromResult(new RunResult()); }, CancellationToken.None).GetAwaiter().GetResult();
            Require(runs.Count == 12 && options.Count(x => x.MeasureLatencyBefore) == 1 && options[0].MeasureLatencyBefore
                && options.Count(x => x.MeasureLatencyAfter) == 1 && options[^1].MeasureLatencyAfter
                && options.All(x => x.MeasureLatency && x.BandwidthLimitBitsPerSecond == 0), "Повторяется ожидание RTT или добавлен потолок скорости");
        });
    }
}
