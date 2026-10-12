using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Collections.Concurrent;

namespace SpeedForge.Core;

public sealed record GeoPoint(double Latitude, double Longitude)
{
    public bool Valid => double.IsFinite(Latitude) && double.IsFinite(Longitude)
        && Latitude is >= -90 and <= 90 && Longitude is >= -180 and <= 180;
}
public sealed record NetworkIdentity(string? ExternalIp, string? HttpIp, string Source,
    string? City = null, string? Organization = null, int? Asn = null, GeoPoint? Location = null);

public sealed class NetworkIdentityClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly Func<string, CancellationToken, Task<IPAddress?>> _stun;
    private readonly ConcurrentDictionary<string,(DateTimeOffset Time, NetworkIdentity Info)> _geoCache = new();
    public NetworkIdentityClient(HttpMessageHandler? handler = null,
        Func<string, CancellationToken, Task<IPAddress?>>? stun = null)
    {
        _http = new(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None, ConnectTimeout = TimeSpan.FromSeconds(3) })
            { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("SpeedForge/1.0 GeoIP");
        _stun = stun ?? StunAsync;
    }
    public async Task<NetworkIdentity> GetAsync(bool geolocation, CancellationToken ct)
    {
        var udp = new[] { _stun("stun.cloudflare.com", ct), _stun("global.stun.twilio.com", ct) };
        var httpTask = ExternalHttpIpAsync(ct);
        await Task.WhenAll(udp.Cast<Task>().Append(httpTask));
        var observed = udp.Select(x => x.Result).ToArray();
        var httpIp = await httpTask;
        var consensus = observed.Where(x => x != null && SecurityPolicy.IsPublicUnicast(x))
            .GroupBy(x => x!.ToString()).FirstOrDefault(x => x.Count() >= 2)?.Key;
        string? address = consensus ?? httpIp;
        var identity = new NetworkIdentity(address, httpIp, consensus != null ? "STUN" : httpIp != null ? "HTTPS" : "Unavailable");
        if (!geolocation || address == null) return identity;
        if (_geoCache.TryGetValue(address, out var cached) && DateTimeOffset.UtcNow - cached.Time < TimeSpan.FromMinutes(15))
            return identity with { City = cached.Info.City, Organization = cached.Info.Organization, Asn = cached.Info.Asn, Location = cached.Info.Location };
        var geo = GeoAsync(address, ct);
        var asn = AsnAsync(address, ct);
        await Task.WhenAll(geo, asn);
        var location = await geo;
        var result = identity with { City = location.City, Organization = location.Organization,
            Asn = await asn ?? location.Asn, Location = location.Location };
        if (result.Location != null || result.Asn != null) _geoCache[address] = (DateTimeOffset.UtcNow, result);
        return result;
    }
    private async Task<string?> ExternalHttpIpAsync(CancellationToken ct)
    {
        foreach (string url in new[] { "https://ipv4.ifconfig.me/all.json", "https://api.ipify.org?format=json" })
        {
            using var document = await JsonAsync(url, ct);
            if (document == null) continue;
            var root = document.RootElement;
            string? raw = Text(root, "ip_addr") ?? Text(root, "ip");
            if (IPAddress.TryParse(raw, out var ip) && SecurityPolicy.IsPublicUnicast(ip)) return ip.ToString();
        }
        return null;
    }
    private async Task<NetworkIdentity> GeoAsync(string address, CancellationToken ct)
    {
        using var document = await JsonAsync("https://ipwho.is/" + Uri.EscapeDataString(address), ct);
        return document == null ? new(address, null, "GeoUnavailable") : ParseGeo(document.RootElement, address);
    }
    public static NetworkIdentity ParseGeo(JsonElement root, string address)
    {
        if (!IPAddress.TryParse(address, out var ip) || !SecurityPolicy.IsPublicUnicast(ip))
            throw new ArgumentException("Требуется публичный IP", nameof(address));
        var empty = new NetworkIdentity(ip.ToString(), null, "GeoUnavailable");
        if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False) return empty;
        string? claimed = Text(root, "ip");
        if (claimed != null && (!IPAddress.TryParse(claimed, out var other) || !ip.Equals(other))) return empty;
        double? Number(string key) => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var n) && double.IsFinite(n) ? n : null;
        var lat = Number("latitude"); var lon = Number("longitude");
        var point = lat.HasValue && lon.HasValue ? new GeoPoint(lat.Value, lon.Value) : null;
        var connection = root.TryGetProperty("connection", out var info) && info.ValueKind == JsonValueKind.Object ? info : root;
        int? asn = connection.TryGetProperty("asn", out var value) ? Asn(value) : null;
        return empty with { Source = "ipwho.is", City = Text(root, "city"), Organization = Text(connection, "org") ?? Text(connection, "isp"),
            Asn = asn, Location = point?.Valid == true ? point : null };
    }
    private async Task<int?> AsnAsync(string address, CancellationToken ct)
    {
        using var document = await JsonAsync("https://stat.ripe.net/data/network-info/data.json?resource=" + Uri.EscapeDataString(address), ct);
        if (document?.RootElement.TryGetProperty("data", out var data) == true && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("asns", out var list)
            && list.ValueKind == JsonValueKind.Array)
            foreach (var value in list.EnumerateArray()) if (Asn(value) is { } asn) return asn;
        return null;
    }
    private static int? Asn(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) && number > 0) return number;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString()?.Replace("AS", "", StringComparison.OrdinalIgnoreCase), out int parsed) && parsed > 0) return parsed;
        return null;
    }
    private async Task<JsonDocument?> JsonAsync(string url, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 65_536) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            byte[] bytes = new byte[65_537]; int length = 0;
            while (length < bytes.Length)
            {
                int count = await stream.ReadAsync(bytes.AsMemory(length), timeout.Token);
                if (count == 0) break; length += count;
            }
            if (length > 65_536) return null;
            var document = JsonDocument.Parse(bytes.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = 32 });
            if (document.RootElement.ValueKind == JsonValueKind.Object) return document;
            document.Dispose(); return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException) { return null; }
    }
    private static string? Text(JsonElement root, string key)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String) return null;
        string text = new((value.GetString() ?? "").Where(x => !char.IsControl(x)).Take(120).ToArray());
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }
    private static async Task<IPAddress?> StunAsync(string host, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(1800));
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, timeout.Token);
            var ip = addresses.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork && SecurityPolicy.IsPublicUnicast(x));
            if (ip == null) return null;
            using var client = new UdpClient(AddressFamily.InterNetwork);
            client.Connect(ip, 3478);
            byte[] request = new byte[20]; request[1] = 1;
            BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(4), 0x2112A442);
            RandomNumberGenerator.Fill(request.AsSpan(8));
            await client.SendAsync(request, timeout.Token);
            var response = await client.ReceiveAsync(timeout.Token);
            return ParseStun(response.Buffer, request.AsSpan(8).ToArray());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (SocketException) { return null; }
    }
    public static IPAddress? ParseStun(byte[] packet, byte[] transaction)
    {
        if (packet.Length < 20 || transaction.Length != 12 || BinaryPrimitives.ReadUInt16BigEndian(packet) != 0x0101
            || BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4)) != 0x2112A442
            || !packet.AsSpan(8,12).SequenceEqual(transaction)) return null;
        int length = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2));
        if (length % 4 != 0 || packet.Length != length + 20) return null;
        for (int index = 20; index + 4 <= packet.Length;)
        {
            int kind = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(index));
            int size = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(index + 2));
            if (index + 4 + size > packet.Length) return null;
            if (kind is 0x0020 or 0x0001 && size >= 8 && packet[index + 5] == 1)
            {
                uint value = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(index + 8));
                if (kind == 0x0020) value ^= 0x2112A442;
                byte[] bytes = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
                var address = new IPAddress(bytes);
                if (SecurityPolicy.IsPublicUnicast(address)) return address;
            }
            index += 4 + ((size + 3) & ~3);
        }
        return null;
    }
    public void Dispose() => _http.Dispose();
}
