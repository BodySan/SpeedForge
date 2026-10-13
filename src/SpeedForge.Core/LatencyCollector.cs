using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;

namespace SpeedForge.Core;

public static class LatencyCollector
{
    public static async Task<List<LatencySample>> CollectAsync(IPAddress address, ProbePhase phase,
        TimeSpan duration, CancellationToken ct)
    {
        var list = new List<LatencySample>();
        var timer = Stopwatch.StartNew();
        using var ping = new Ping();
        while (timer.Elapsed < duration && !ct.IsCancellationRequested)
        {
            var remaining = duration - timer.Elapsed;
            if (remaining.TotalMilliseconds < 1) break;
            var now = DateTimeOffset.Now;
            try
            {
                var reply = await ping.SendPingAsync(address, TimeSpan.FromMilliseconds(Math.Min(900, Math.Floor(remaining.TotalMilliseconds))), new byte[32], null, ct);
                list.Add(reply.Status == IPStatus.Success
                    ? new LatencySample(phase, now, reply.RoundtripTime, null)
                    : new LatencySample(phase, now, null, reply.Status.ToString()));
            }
            catch (PingException e) { list.Add(new LatencySample(phase, now, null, e.GetType().Name)); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            remaining = duration - timer.Elapsed;
            if (remaining <= TimeSpan.Zero) break;
            try { await Task.Delay(remaining < TimeSpan.FromMilliseconds(350) ? remaining : TimeSpan.FromMilliseconds(350), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
        return list;
    }
}
