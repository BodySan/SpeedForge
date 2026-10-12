using SpeedForge.Core;

internal static class SpeedRegressionTests
{
    public static void Register(Action<string, Action> test)
    {
        void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        test("История различает все восемь профилей архива и направление", () =>
        {
            var server = ServerCatalog.LoadPublished().Single(x => x.Id == "beeline-iperf2");
            var names = server.Iperf2Profiles.Select(x => new RunResult { Direction = TestDirection.Upload,
                Options = new TestOptions(Transport: TransportKind.Udp, ParallelStreams: x.Streams,
                    Iperf2OfferedBitsPerSecondPerStream: x.BitsPerSecondPerStream) }.ProfileLabel).ToArray();
            Require(names.SequenceEqual(new[] { "1(100)", "5(100)", "10(100)", "20(100)", "1(1000)", "5(1000)", "10(1000)", "20(1000)" }), "Профили не совпадают с bat-файлами");
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
            Require(runs.Count == 9 && options.Count(x => x.MeasureLatencyBefore) == 1 && options[0].MeasureLatencyBefore
                && options.Count(x => x.MeasureLatencyAfter) == 1 && options[^1].MeasureLatencyAfter
                && options.All(x => x.MeasureLatency && x.BandwidthLimitBitsPerSecond == 0), "Повторяется ожидание RTT или добавлен потолок скорости");
        });
    }
}
