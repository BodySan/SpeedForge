using System.Globalization;
using System.Text.RegularExpressions;

namespace SpeedForge.Core;

public static partial class Iperf2TextParser
{
    [GeneratedRegex(@"^\[\s*\*?(?<id>\d+|SUM)\s*\]\s+(?<start>\d+(?:\.\d+)?)\s*-\s*(?<end>\d+(?:\.\d+)?)\s+sec\s+(?<bytes>[\d.eE+\-]+)\s+(?<byteUnit>[KMGT]?Bytes)\s+(?<rate>[\d.eE+\-]+)\s+(?<rateUnit>[kKmMgGtT]?bits/sec)(?:\s+(?<jitter>\S+)\s+ms\s+(?<lost>\d+)\s*/\s*(?<total>\d+)\s*\((?<loss>[\d.]+)%\))?", RegexOptions.CultureInvariant)]
    private static partial Regex RowPattern();
    [GeneratedRegex(@"^\[\s*\*?(?<id>\d+|SUM)\s*\]\s+Server Report:", RegexOptions.CultureInvariant)]
    private static partial Regex ServerReportPattern();
    private sealed record Row(string Id, double Start, double End, long Bytes, double Bps,
        bool Receiver, double? Jitter, long? Lost, long? Total, double? Loss);
    public static void Apply(string output, RunResult result)
    {
        var rows = new List<Row>();
        var relayed = new HashSet<string>();
        foreach (string raw in output.Split('\n').Take(2000))
        {
            string line = raw.Trim();
            var marker = ServerReportPattern().Match(line);
            if (marker.Success) { relayed.Add(marker.Groups["id"].Value); continue; }
            var row = Parse(line, result.Direction, relayed);
            if (row != null) rows.Add(row);
        }
        var receivers = Summary(rows.Where(x => x.Receiver), result.Options.DurationSeconds);
        var senders = Summary(rows.Where(x => !x.Receiver), result.Options.DurationSeconds);
        if (senders.Length > 0)
        {
            result.SenderBytes = senders.Sum(x => x.Bytes);
            result.SenderMbps = senders.Sum(x => x.Bps) / 1_000_000;
        }
        foreach (var group in rows.Where(x => (result.Direction == TestDirection.Download || x.Receiver)
            && (x.Start > 0.15 || x.End < result.Options.DurationSeconds * 0.9))
            .GroupBy(x => (x.Start, x.End)))
        {
            var selected = group.Any(x => x.Id == "SUM") ? group.Where(x => x.Id == "SUM").Take(1).ToArray()
                : group.GroupBy(x => x.Id).Select(x => x.First()).ToArray();
            result.Intervals.Add(new(group.Key.Start, group.Key.End, selected.Sum(x => x.Bytes), selected.Sum(x => x.Bps)));
        }
        if (receivers.Length == 0)
        {
            if (relayed.Count > 0)
            {
                result.Status = RunStatus.EngineError;
                result.ErrorCode = "IPERF2_RECEIVER_REPORT_INVALID";
                result.ErrorText = "Сервер вернул отчёт, но его значения не соответствуют формату или длительности теста. Возможна несовместимость версий iPerf2.";
                result.MetricOrigin = "iPerf2: полученный Server Report отклонён";
                return;
            }
            result.Status = result.SenderBytes is > 0 ? RunStatus.Partial : RunStatus.InsufficientData;
            result.ErrorCode = result.Direction == TestDirection.Download ? "IPERF2_REVERSE_NO_DATA" : "IPERF2_RECEIVER_UNCONFIRMED";
            result.ErrorText = result.Direction == TestDirection.Download ? "Обратное направление не передало данные. Ответ измерителя сохранён в отладке."
                : "Сервер не подтвердил приём. Доступна только статистика отправителя; потери и джиттер неизвестны.";
            result.MetricOrigin = result.Direction == TestDirection.Download ? "iPerf2 UDP reverse: приём не подтверждён" : "iPerf2: локальный отправитель, UDP ACK отсутствует";
            result.Limitations.Add("TX-only: статистика отправителя сохранена только в отладке. Без отчёта приёмника скорость до сервера неизвестна.");
            return;
        }
        result.ReceiverBytes = receivers.Sum(x => x.Bytes);
        result.ReceiverMbps = receivers.Sum(x => x.Bps) / 1_000_000;
        result.ReceiverStreams = receivers.Any(x => x.Id == "SUM") ? result.Options.ParallelStreams : receivers.Length;
        result.Mbps = result.ReceiverMbps;
        if (result.Direction == TestDirection.Download) result.DownloadMbps = result.Mbps;
        else result.UploadMbps = result.Mbps;
        if (receivers.All(x => x.Total.HasValue && x.Lost.HasValue))
        {
            result.TotalPackets = receivers.Sum(x => x.Total!.Value);
            result.LostPackets = receivers.Sum(x => x.Lost!.Value);
            if (result.TotalPackets > 0) result.LostPercent = result.LostPackets * 100.0 / result.TotalPackets;
        }
        var jitter = receivers.Where(x => x.Jitter.HasValue).Select(x => x.Jitter!.Value).ToArray();
        if (jitter.Length > 0) result.JitterMilliseconds = jitter.Max();
        result.MetricOrigin = result.Direction == TestDirection.Download ? "iPerf2 UDP: локальный приёмник" : "iPerf2 UDP: Server Report принимающей стороны";
        bool missingStreams = !receivers.Any(x => x.Id == "SUM") && receivers.Length < result.Options.ParallelStreams;
        result.Status = result.ReceiverBytes == 0 ? RunStatus.NoData : missingStreams ? RunStatus.Partial : RunStatus.Measured;
        if (missingStreams)
        {
            result.ErrorCode = "IPERF2_MISSING_STREAM_REPORTS";
            result.ErrorText = $"Сервер подтвердил приём {receivers.Length} из {result.Options.ParallelStreams} потоков. Скорость относится только к подтверждённым потокам.";
            result.Limitations.Add("Не все потоки вернули отчёт приёмника");
        }
        result.Diagnostics?.Event("iperf2.parser", $"receiver summaries={receivers.Length}; sender summaries={senders.Length}; missing streams={missingStreams}");
    }
    public static MeasurementProgress? Interval(string line, TestDirection direction, int streams = 1)
    {
        var row = Parse(line.Trim(), direction, []);
        return row == null || streams > 1 && row.Id != "SUM" ? null : new(direction, row.End, row.Bps / 1_000_000, row.Bytes);
    }
    public static bool IsSummary(string line, int duration, int streams)
    {
        var row = Parse(line.Trim(), TestDirection.Upload, []);
        return row != null && (streams == 1 || row.Id == "SUM") && row.Start <= .15
            && row.End >= duration * .9 && row.End <= duration + Math.Max(2, duration * .1);
    }
    private static Row[] Summary(IEnumerable<Row> candidates, int duration)
    {
        var rows = candidates.Where(x => x.Start <= 0.15 && x.End >= Math.Max(1, duration * 0.75)
            && x.End <= duration + Math.Max(2, duration * 0.1)).ToArray();
        var sum = rows.Where(x => x.Id == "SUM").OrderByDescending(x => x.End).FirstOrDefault();
        return sum != null ? [sum] : rows.GroupBy(x => x.Id).Select(x => x.OrderByDescending(y => y.End).First()).ToArray();
    }
    private static Row? Parse(string line, TestDirection direction, HashSet<string> relayed)
    {
        var match = RowPattern().Match(line);
        if (!match.Success) return null;
        double? Number(string name) => double.TryParse(match.Groups[name].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            && double.IsFinite(n) && n >= 0 ? n : null;
        var start = Number("start"); var end = Number("end"); var bytes = Number("bytes"); var rate = Number("rate");
        if (start == null || end == null || end <= start || bytes == null || rate == null) return null;
        int bytePower = " KMGT".IndexOf(match.Groups["byteUnit"].Value[0]);
        if (match.Groups["byteUnit"].Value == "Bytes") bytePower = 0;
        double scaled = bytes.Value * Math.Pow(1024, Math.Max(0, bytePower));
        int ratePower = " kmgt".IndexOf(char.ToLowerInvariant(match.Groups["rateUnit"].Value[0]));
        if (match.Groups["rateUnit"].Value == "bits/sec") ratePower = 0;
        double bps = rate.Value * Math.Pow(1000, Math.Max(0, ratePower));
        if (!double.IsFinite(scaled) || scaled >= long.MaxValue || !double.IsFinite(bps) || bps > 1_000_000_000_000) return null;
        long lost = 0, total = 0;
        bool lossFields = long.TryParse(match.Groups["lost"].Value, out lost) && long.TryParse(match.Groups["total"].Value, out total)
            && lost >= 0 && total >= lost;
        bool receiver = direction == TestDirection.Download || relayed.Contains(match.Groups["id"].Value);
        return new(match.Groups["id"].Value, start.Value, end.Value, (long)scaled, bps, receiver,
            receiver ? Number("jitter") : null, receiver && lossFields ? lost : null,
            receiver && lossFields ? total : null, receiver ? Number("loss") : null);
    }
}
