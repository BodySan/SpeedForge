using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Diagnostics;

internal static class SandboxInfrastructure
{
    public static async Task<int> ServeAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        var listeners = new[] { (55203, "download"), (55204, "upload"), (55205, "all") };
        var tasks = listeners.Select(x => ProxyAsync(x.Item1, x.Item2, stop.Token)).Append(BlackholeAsync(stop.Token))
            .Append(UdpRelayAsync(55209, true, stop.Token)).Append(UdpRelayAsync(55210, false, stop.Token))
            .Append(UntrustedTlsAsync(stop.Token)).ToArray();
        await File.WriteAllTextAsync(Path.Combine(directory, "ready.txt"), "TCP: 55203 upload-only, 55204 download-only, 55205 denied. UDP: 55208 blackhole. Backend: 55201.");
        Console.WriteLine("Sandbox ready; only 127.0.0.1 listeners, real iPerf traffic, no fabricated speeds.");
        try { await Task.WhenAll(tasks); } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        return 0;
    }
    private static async Task ProxyAsync(int port, string deny, CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                _ = ConnectionAsync(client, deny, ct);
            }
        }
        finally { listener.Stop(); }
    }
    private static async Task ConnectionAsync(TcpClient client, string deny, CancellationToken ct)
    {
        using (client)
        using (var backend = new TcpClient())
        {
            try
            {
                await backend.ConnectAsync(IPAddress.Loopback, 55201, ct);
                var upstream = RelayAsync(client.GetStream(), backend.GetStream(), deny, ct);
                var downstream = backend.GetStream().CopyToAsync(client.GetStream(), ct);
                await Task.WhenAny(upstream, downstream);
                client.Close(); backend.Close();
                try { await Task.WhenAll(upstream, downstream); } catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or OperationCanceledException) { }
            }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or OperationCanceledException) { }
        }
    }
    private static async Task RelayAsync(NetworkStream source, NetworkStream target, string deny, CancellationToken ct)
    {
        byte[] buffer = new byte[65_536];
        using var prefix = new MemoryStream();
        bool inspected = false;
        while (true)
        {
            int read = await source.ReadAsync(buffer, ct);
            if (read == 0) return;
            if (!inspected && prefix.Length < 8192)
            {
                prefix.Write(buffer, 0, Math.Min(read, 8192 - (int)prefix.Length));
                // iPerf3 control: 37-byte cookie followed by length-prefixed JSON parameters.
                if (prefix.Length >= 41)
                {
                    var bytes = prefix.ToArray();
                    int length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(37, 4));
                    if (length is < 2 or > 4096) inspected = true; // This is a data socket.
                    else if (bytes.Length >= 41 + length)
                    {
                        inspected = true;
                        using var json = JsonDocument.Parse(bytes.AsMemory(41, length));
                        bool reverse = json.RootElement.TryGetProperty("reverse", out var value) && value.ValueKind == JsonValueKind.True;
                        if (deny == "all" || deny == (reverse ? "download" : "upload"))
                        { Console.WriteLine("Sandbox rejected " + (reverse ? "Download" : "Upload")); return; }
                    }
                }
            }
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }
    private static async Task BlackholeAsync(CancellationToken ct)
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 55208));
        while (!ct.IsCancellationRequested) await udp.ReceiveAsync(ct);
    }
    private static async Task UdpRelayAsync(int port, bool limited, CancellationToken ct)
    {
        // Actual datagrams reach a native iPerf2 receiver through a 100 Mbit/s token bucket.
        // The server's ACK is relayed unchanged; no speed or loss report is fabricated.
        using var frontend = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
        IgnoreUdpReset(frontend);
        frontend.Client.ReceiveBufferSize = 2_097_152;
        var peers = new Dictionary<IPEndPoint, UdpClient>();
        var replies = new List<Task>();
        double credit = 125_000;
        long previous = Stopwatch.GetTimestamp();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var packet = await frontend.ReceiveAsync(ct);
                if (!peers.TryGetValue(packet.RemoteEndPoint, out var backend))
                {
                    // Windows UDP socket reuse routes simultaneous peers to only one socket.
                    // Isolate each flow on a native server port while preserving datagrams and ACKs.
                    backend = new UdpClient(); IgnoreUdpReset(backend);
                    backend.Connect(IPAddress.Loopback, limited ? 55211 : 55301 + peers.Count % 40);
                    peers.Add(packet.RemoteEndPoint, backend);
                    replies.Add(Reply(backend, packet.RemoteEndPoint));
                }
                long now = Stopwatch.GetTimestamp();
                credit = Math.Min(125_000, credit + (now - previous) * 12_500_000.0 / Stopwatch.Frequency);
                previous = now;
                bool finish = packet.Buffer.Length >= 4 && BinaryPrimitives.ReadInt32BigEndian(packet.Buffer.AsSpan(0, 4)) < 0;
                if (limited && !finish && credit < packet.Buffer.Length) continue;
                if (limited) credit -= packet.Buffer.Length;
                await backend.SendAsync(packet.Buffer, ct);
            }
        }
        finally
        {
            foreach (var peer in peers.Values) peer.Dispose();
            try { await Task.WhenAll(replies); }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
        }
        async Task Reply(UdpClient backend, IPEndPoint client)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var packet = await backend.ReceiveAsync(ct);
                    await frontend.SendAsync(packet.Buffer, client, ct);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
        }
    }
    private static void IgnoreUdpReset(UdpClient socket)
    {
        // Late native ACKs can reach a client socket that has already closed.
        // Windows otherwise turns the resulting ICMP into an exception on the relay's shared socket.
        if (OperatingSystem.IsWindows()) socket.Client.IOControl(unchecked((int)0x9800000C), new byte[4], null);
    }
    private static async Task UntrustedTlsAsync(CancellationToken ct)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.Now.AddMinutes(-1), DateTimeOffset.Now.AddDays(1));
        var listener = new TcpListener(IPAddress.Loopback, 5091); listener.Start();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(ct);
                using var ssl = new SslStream(client.GetStream());
                try
                {
                    await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, ct);
                    await ssl.WriteAsync("HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: 1\r\n\r\nx"u8.ToArray(), ct);
                }
                catch (Exception e) when (e is IOException or AuthenticationException) { }
            }
        }
        finally { listener.Stop(); }
    }
}
