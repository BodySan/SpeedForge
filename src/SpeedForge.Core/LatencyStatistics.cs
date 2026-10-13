namespace SpeedForge.Core;

public enum MetricQuality { Unknown, Good, Moderate, Poor }

public static class LatencyStatistics
{
    public static double? Median(IEnumerable<LatencySample> samples, ProbePhase phase)
    {
        var values = samples.Where(x => x.Phase == phase && x.RttMilliseconds is >= 0)
            .Select(x => x.RttMilliseconds!.Value).Where(double.IsFinite).Order().ToArray();
        return values.Length == 0 ? null : values.Length % 2 == 0
            ? (values[values.Length / 2 - 1] + values[values.Length / 2]) / 2 : values[values.Length / 2];
    }

    // Mean absolute difference of consecutive ICMP replies in one phase.
    // A lost reply interrupts the pair; idle and loaded samples never mix.
    public static double? Jitter(IEnumerable<LatencySample> samples, ProbePhase phase)
    {
        double total = 0; int pairs = 0;
        double? previous = null;
        foreach (var sample in samples.Where(x => x.Phase == phase).OrderBy(x => x.Time))
        {
            double? current = sample.RttMilliseconds is >= 0 && double.IsFinite(sample.RttMilliseconds.Value)
                ? sample.RttMilliseconds : null;
            if (previous.HasValue && current.HasValue) { total += Math.Abs(current.Value - previous.Value); pairs++; }
            previous = current;
        }
        return pairs == 0 ? null : total / pairs;
    }

    public static MetricQuality PingQuality(double? value) => Quality(value, 50, 100);
    public static MetricQuality JitterQuality(double? value) => Quality(value, 5, 20);
    public static MetricQuality LossQuality(double? value) => Quality(value, 0, 1);
    private static MetricQuality Quality(double? value, double good, double moderate) =>
        value is not >= 0 || !double.IsFinite(value.Value) ? MetricQuality.Unknown
            : value <= good ? MetricQuality.Good : value <= moderate ? MetricQuality.Moderate : MetricQuality.Poor;
}
