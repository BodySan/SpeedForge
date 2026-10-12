using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace SpeedForge.Core;

public sealed class TargetRejectedException(string message) : Exception(message);

public static partial class SecurityPolicy
{
    [GeneratedRegex(@"\A(?=.{1,253}\z)[a-zA-Z0-9](?:[a-zA-Z0-9.-]*[a-zA-Z0-9])?\z", RegexOptions.CultureInvariant)]
    private static partial Regex DnsNamePattern();

    public static bool ValidHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Length > 253 || host.Trim() != host || host.Contains("..")) return false;
        if (IPAddress.TryParse(host, out _)) return true;
        if (!DnsNamePattern().IsMatch(host)) return false;
        var labels = host.Split('.');
        return labels.All(x => x.Length is >= 1 and <= 63 && x[0] != '-' && x[^1] != '-');
    }

    public static async Task<IPAddress> ResolveAsync(ServerEntry server, TestOptions options, CancellationToken ct, RunDiagnostics? diagnostics = null)
    {
        if (!ValidHost(server.Host)) throw new TargetRejectedException("Неверное имя сервера");
        if (server.Port is < 1 or > 65535) throw new TargetRejectedException("Недопустимый порт");
        var addresses = await Dns.GetHostAddressesAsync(server.Host, ct);
        diagnostics?.Event("dns.answers", $"host={server.Host}; family={options.IpFamily}; answers={string.Join(',', addresses.Select(x => x.ToString()))}");
        var candidates = addresses.Where(x => options.IpFamily switch
        {
            IpFamily.IPv4 => x.AddressFamily == AddressFamily.InterNetwork,
            IpFamily.IPv6 => x.AddressFamily == AddressFamily.InterNetworkV6,
            _ => true
        }).ToList();
        if (candidates.Count == 0) throw new SocketException((int)SocketError.HostNotFound);
        if (!options.AllowPrivateTargets) candidates = candidates.Where(IsPublicUnicast).ToList();
        if (candidates.Count == 0) throw new TargetRejectedException("Частный/локальный или зарезервированный адрес: требуется явное разрешение");
        return candidates[0]; // Fixes IP at test start. Avoids a subsequent DNS rebind for iPerf.
    }

    public static bool IsPublicUnicast(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return false;
        if (address.IsIPv4MappedToIPv6) return IsPublicUnicast(address.MapToIPv4());
        var a = address.GetAddressBytes();
        if (a.Length == 4)
        {
            int b = a[0], c = a[1], d = a[2];
            if (b is 0 or 10 or 127 || b >= 224 || b == 169 && c == 254 || b == 172 && c is >= 16 and <= 31
                || b == 192 && c == 168 || b == 100 && c is >= 64 and <= 127 || b == 198 && c is 18 or 19
                || b == 192 && c == 0 && d == 0 || b == 192 && c == 0 && d == 2
                || b == 198 && c == 51 && d == 100 || b == 203 && c == 0 && d == 113)
                return false;
            return true;
        }
        if (a.Length == 16)
        {
            if ((a[0] & 0xFE) == 0xFC || a[0] == 0xFF || a[0] == 0xFE && (a[1] & 0xC0) == 0x80) return false;
            if (a.Take(15).All(x => x == 0)) return false; // :: and ::1
            if (a[0] == 0x20 && a[1] == 0x01 && a[2] == 0x0d && a[3] == 0xb8) return false; // documentation
            return true;
        }
        return false;
    }
}
