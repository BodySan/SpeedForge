using SpeedForge.Core;

internal static class AuditRegressionTests
{
    public static void Register(Action<string, Action> test)
    {
        static void Require(bool value) { if (!value) throw new InvalidOperationException("Audit regression"); }
        test("Streaming iPerf3 сохраняет start при вложенном end", () =>
        {
            var run = new RunResult { Direction = TestDirection.Download, EngineVersion = "trusted" };
            Iperf3Parser.ApplyStreaming("""
                {"event":"start","data":{"version":"iperf 3.22"}}
                {"event":"interval","data":{"sum":{"start":0,"end":1,"bytes":1000,"bits_per_second":8000}}}
                {"event":"end","data":{"end":{"sum_received":{"bytes":2000,"bits_per_second":8000}}}}
                """, run);
            Require(run.Status == RunStatus.Measured && run.EngineVersion == "iperf 3.22" && run.Intervals.Count == 1);
        });
        test("JSON без start сохраняет версию проверенного бинарника", () =>
        {
            var run = new RunResult { Direction = TestDirection.Download, EngineVersion = "trusted" };
            Iperf3Parser.Apply("""{"end":{"sum_received":{"bytes":2000,"bits_per_second":8000}}}""", run);
            Require(run.EngineVersion == "trusted");
        });
        test("Подтверждённый Билайн отличается от локальной отправки", () =>
        {
            var run = new RunResult { Direction = TestDirection.Upload, Options = BeelineTest.Configure(new TestOptions(), 500),
                Status = RunStatus.Measured, ReceiverBytes = 1000, ReceiverMbps = 1, ReceiverStreams = 5,
                SenderMbps = 1, UploadMbps = 1, Mbps = 1 };
            Require(DirectionObservation.From([run]).Verdict == DirectionVerdict.UploadObserved);
        });
        test("CSV iPerf2 reverse попадает в загрузку", () =>
        {
            var run = new RunResult { Direction = TestDirection.Download, Transport = TransportKind.Udp };
            Iperf2Parser.Apply("20260101010101,1.2.3.4,5151,5.6.7.8,5001,1,0.0-8.0,10000000,10000000,0.4,1,200,0.5", run);
            Require(run.DownloadMbps == 10 && run.UploadMbps == null);
        });
        test("Внешний таймаут Endpoint не обрывает внутренний бюджет", () =>
        {
            var options = new TestOptions(DurationSeconds: 30, MeasureLatency: false);
            Require(RunTiming.RunSeconds(ServerCatalog.Custom("127.0.0.1",5088,EngineKind.Http), options) >= 45);
        });
    }
}
