using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SpeedForge.Core;

public static class Iperf3Parser
{
    public static void ApplyStreaming(string output, RunResult run)
    {
        var assembled = new JsonObject();
        var intervals = new JsonArray();
        try
        {
            foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (JsonNode.Parse(line) is not JsonObject node) continue;
                string? kind = node["event"]?.GetValue<string>();
                var data = node["data"];
                if (kind == "start") assembled["start"] = data?.DeepClone();
                else if (kind == "interval") intervals.Add(data?.DeepClone());
                else if (kind == "end")
                {
                    if (data is JsonObject obj && obj.ContainsKey("end")) assembled = (JsonObject)obj.DeepClone();
                    else assembled["end"] = data?.DeepClone();
                }
                else if (kind == "error") assembled["error"] = data?.DeepClone();
            }
            if (!assembled.ContainsKey("intervals")) assembled["intervals"] = intervals;
            Apply(assembled.ToJsonString(), run);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        { run.Status = RunStatus.EngineError; run.ErrorCode = "IPERF3_INVALID_JSON_STREAM"; }
    }

    public static void Apply(string output, RunResult run)
    {
        try
        {
            using var doc = JsonDocument.Parse(output, new JsonDocumentOptions { MaxDepth = 48 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                run.Status = RunStatus.EngineError;
                run.ErrorCode = "IPERF3_INVALID_JSON";
                return;
            }
            if (root.TryGetProperty("error", out var err))
            {
                run.Status = RunStatus.EngineError;
                run.ErrorCode = "IPERF3_REMOTE_ERROR";
                run.ErrorText = SafeString(err, 512);
                return;
            }
            run.EngineVersion = Try(root, "start", "version") is { } v ? SafeString(v, 100) : null;
            var end = Try(root, "end");
            if (end is null || end.Value.ValueKind != JsonValueKind.Object)
            {
                run.Status = RunStatus.InsufficientData;
                run.ErrorCode = "END_MISSING";
                return;
            }
            var sending = Try(end.Value, "sum_sent");
            var receiving = Try(end.Value, "sum_received");
            // UDP iperf3 may use "sum" plus sender flag instead of "sum_received".
            var sum = Try(end.Value, "sum");
            run.SenderBytes = Long(sending, "bytes");
            run.ReceiverBytes = Long(receiving, "bytes");
            run.SenderMbps = Mbps(sending);
            run.ReceiverMbps = Mbps(receiving);
            run.TcpRetransmits = Long(sending, "retransmits");
            if (run.Transport == TransportKind.Udp)
            {
                var receiver = receiving;
                if (receiver is null && sum is { } sm && Try(sm, "sender") is { } sendFlag
                    && sendFlag.ValueKind is JsonValueKind.False)
                    receiver = sm;
                if (receiver is not null)
                {
                    run.ReceiverBytes = Long(receiver, "bytes");
                    run.ReceiverMbps = Mbps(receiver);
                    run.JitterMilliseconds = Double(receiver, "jitter_ms");
                    run.LostPercent = Double(receiver, "lost_percent");
                    run.LostPackets = Long(receiver, "lost_packets");
                    run.TotalPackets = Long(receiver, "packets");
                    run.MetricOrigin = "iperf3 UDP, отчёт приёмной стороны";
                }
                else
                {
                    run.Limitations.Add("Отсутствует подтверждённая статистика UDP-приёмника: потери и джиттер не вычислены");
                    run.MetricOrigin = "iperf3 UDP, данные отправителя";
                }
            }
            else run.MetricOrigin = "iperf3 TCP, end.sum_received";

            if (!(run.Transport == TransportKind.Udp && run.Direction == TestDirection.Upload)
                && Try(root, "intervals") is { } intervals && intervals.ValueKind == JsonValueKind.Array)
            {
                foreach (var interval in intervals.EnumerateArray().Take(100))
                {
                    var metric = Try(interval, "sum");
                    if (metric is null) continue;
                    var start = Double(metric, "start");
                    var stop = Double(metric, "end");
                    var bytes = Long(metric, "bytes");
                    var bps = Double(metric, "bits_per_second");
                    if (start is >= 0 && stop > start && bytes is >= 0 && bps is >= 0 && double.IsFinite(bps.Value))
                        run.Intervals.Add(new IntervalSample(start.Value, stop.Value, bytes.Value, bps.Value));
                }
            }

            if (run.Direction == TestDirection.Bidirectional)
            {
                var reverseReceiver = Try(end.Value, "sum_received_bidir_reverse");
                var upload = run.ReceiverMbps;
                var download = Mbps(reverseReceiver);
                run.UploadReceiverBytes = run.ReceiverBytes;
                run.DownloadReceiverBytes = Long(reverseReceiver, "bytes");
                run.Limitations.Add("Два направления измерялись одновременно: результат не эквивалентен последовательным тестам");
                run.MetricOrigin = "iperf3 --bidir, приёмные суммы по направлениям";
                if (download is null || upload is null
                    || run.DownloadReceiverBytes is null || run.UploadReceiverBytes is null)
                {
                    run.Status = RunStatus.InsufficientData;
                    run.ErrorCode = "BIDIR_RECEIVER_MISSING";
                    return;
                }
                if (download <= 0 || upload <= 0
                    || run.DownloadReceiverBytes <= 0 || run.UploadReceiverBytes <= 0)
                {
                    run.Status = RunStatus.NoData;
                    run.ErrorCode = "ZERO_PAYLOAD";
                    return;
                }
                if (download > 1_000_000 || upload > 1_000_000)
                {
                    run.Status = RunStatus.EngineError;
                    run.ErrorCode = "IMPLAUSIBLE_RATE";
                    return;
                }
                run.UploadMbps = upload;
                run.DownloadMbps = download;
                run.Mbps = null;
                run.Status = RunStatus.Measured;
                return;
            }

            var pickedBytes = run.ReceiverBytes;
            var pickedRate = run.ReceiverMbps;
            if (pickedBytes is null || pickedRate is null)
            {
                run.Status = RunStatus.InsufficientData;
                run.ErrorCode = "RECEIVER_MISSING";
                run.Limitations.Add("Данные отправителя не подменяют отсутствующие данные получателя");
                return;
            }
            if (pickedBytes <= 0 || pickedRate <= 0)
            {
                run.Status = RunStatus.NoData;
                run.ErrorCode = "ZERO_PAYLOAD";
                return;
            }
            if (!double.IsFinite(pickedRate.Value) || pickedRate > 1_000_000)
            {
                run.Status = RunStatus.EngineError;
                run.ErrorCode = "IMPLAUSIBLE_RATE";
                return;
            }
            if (run.Direction == TestDirection.Download)
            { run.DownloadMbps = pickedRate; run.DownloadReceiverBytes = pickedBytes; }
            else
            { run.UploadMbps = pickedRate; run.UploadReceiverBytes = pickedBytes; }
            run.Mbps = pickedRate;
            run.Status = RunStatus.Measured;
        }
        catch (JsonException)
        {
            run.Status = RunStatus.EngineError;
            run.ErrorCode = "IPERF3_INVALID_JSON";
        }
        catch (FormatException)
        {
            run.Status = RunStatus.EngineError;
            run.ErrorCode = "IPERF3_INVALID_NUMBER";
        }
    }

    private static JsonElement? Try(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var p) ? p : null;
    private static JsonElement? Try(JsonElement value, string first, string second) =>
        Try(value, first) is { } v ? Try(v, second) : null;
    private static long? Long(JsonElement? element, string key)
    {
        var v = element is { } x ? Try(x, key) : null;
        if (v == null || v.Value.ValueKind != JsonValueKind.Number) return null;
        return v.Value.TryGetInt64(out var r) && r >= 0 ? r : null;
    }
    private static double? Double(JsonElement? element, string key)
    {
        var v = element is { } x ? Try(x, key) : null;
        if (v == null || v.Value.ValueKind != JsonValueKind.Number || !v.Value.TryGetDouble(out var r)) return null;
        return double.IsFinite(r) && r >= 0 ? r : null;
    }
    private static double? Mbps(JsonElement? x) => Double(x, "bits_per_second") is { } bps ? bps / 1_000_000.0 : null;
    private static string SafeString(JsonElement e, int max)
    {
        var str = e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.ToString();
        return str[..Math.Min(max, str.Length)];
    }
}

public static class Iperf2Parser
{
    public static void Apply(string output, RunResult result)
    {
        // iperf2 -y C basic CSV: time,source,sourcePort,destination,destinationPort,id,interval,bytes,bits/s[,UDP fields]
        // CSV is not stable across all iperf2 builds; reject unknown rows instead of guessing.
        var valid = new List<(double start, double end, long bytes, double bps, string[] cols)>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(400))
        {
            var col = line.Trim().Split(',');
            if (col.Length < 9) continue;
            var times = col[6].Split('-');
            if (times.Length != 2 || !double.TryParse(times[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var start)
                || !double.TryParse(times[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var end)
                || !long.TryParse(col[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes)
                || !double.TryParse(col[8], NumberStyles.Float, CultureInfo.InvariantCulture, out var bps)) continue;
            if (start < 0 || end <= start || bytes < 0 || bps < 0 || !double.IsFinite(bps)) continue;
            valid.Add((start, end, bytes, bps, col));
        }
        if (valid.Count == 0) { result.Status = RunStatus.InsufficientData; result.ErrorCode = "IPERF2_CSV_NOT_RECOGNIZED"; return; }
        var summary = valid.Where(x => x.start <= 0.15).OrderByDescending(x => x.end).FirstOrDefault();
        if (summary.end <= 0) { result.Status = RunStatus.InsufficientData; result.ErrorCode = "IPERF2_SUMMARY_MISSING"; return; }
        foreach (var e in valid.Where(x => x.start >= 0 && x.end < summary.end))
            result.Intervals.Add(new IntervalSample(e.start, e.end, e.bytes, e.bps));
        result.SenderBytes = summary.bytes;
        result.SenderMbps = summary.bps / 1_000_000.0;
        result.MetricOrigin = "iperf2 -y C, сторона клиента (возможности сервера не подтверждены)";
        if (summary.bytes <= 0 || summary.bps <= 0) { result.Status = RunStatus.NoData; result.ErrorCode = "ZERO_PAYLOAD"; return; }
        if (result.Transport == TransportKind.Udp)
        {
            if (summary.cols.Length < 13
                || !double.TryParse(summary.cols[9], NumberStyles.Float, CultureInfo.InvariantCulture, out var jitter)
                || !long.TryParse(summary.cols[10], out var lost)
                || !long.TryParse(summary.cols[11], out var total)
                || !double.TryParse(summary.cols[12], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent)
                || !double.IsFinite(jitter) || !double.IsFinite(percent)
                || jitter < 0 || lost < 0 || total <= 0 || lost > total || percent is < 0 or > 100)
            {
                result.Status = RunStatus.InsufficientData;
                result.ErrorCode = "IPERF2_UDP_RECEIVER_UNCONFIRMED";
                result.Limitations.Add("UDP-статистика без подтверждённой строки приёмника не является измерением потерь");
                return;
            }
            result.JitterMilliseconds = jitter;
            result.LostPackets = lost;
            result.TotalPackets = total;
            result.LostPercent = percent;
            result.ReceiverMbps = summary.bps / 1_000_000.0;
            result.ReceiverBytes = summary.bytes;
            result.MetricOrigin = "iperf2 UDP, CSV со статистикой датаграмм; необходима полевая квалификация формата";
        }
        else
        {
            result.ReceiverBytes = null;
            result.ReceiverMbps = null;
            result.Limitations.Add("iperf2 CSV показывает сторону клиента; байты получателя не подтверждены");
        }
        result.Mbps = result.ReceiverMbps;
        result.UploadMbps = result.ReceiverMbps;
        result.Status = result.ReceiverMbps is > 0 ? RunStatus.Measured : RunStatus.InsufficientData;
    }
}
