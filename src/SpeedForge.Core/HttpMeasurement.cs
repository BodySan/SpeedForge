using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace SpeedForge.Core;

public sealed class HttpMeasurement
{
    private const int ChunkSize = 65_536;
    private const int MaxRoundBytes = 32 * 1_048_576;
    private const long MaxSessionBytes = 256L * 1_048_576;

    public async Task ExecuteAsync(ServerEntry server, TestOptions options, IPAddress ip,
        RunResult run, string? sessionKey, CancellationToken ct)
    {
        options.Validate();
        run.Diagnostics?.Event("http.endpoint", $"host={server.Host}:{server.Port}; ip={ip}; direction={options.Direction}; SpeedForge v1; session key is never logged");
        if (server.Http is not null)
        {
            await new PublicHttpMeasurement().ExecuteAsync(server, options, ip, run, ct);
            return;
        }
        if (options.Direction == TestDirection.Bidirectional || options.Transport == TransportKind.Udp)
        {
            run.Status = RunStatus.Unsupported;
            run.ErrorCode = "HTTP_MODE_UNSUPPORTED";
            return;
        }
        bool loopback = IPAddress.IsLoopback(ip);
        string scheme = loopback && options.AllowPrivateTargets ? "http" : "https";
        var baseUri = new UriBuilder(scheme, server.Host, server.Port).Uri;
        // Pins the resolved IP for each TCP connection while keeping hostname for TLS SNI + cert validation.
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            UseProxy = false,
            ConnectCallback = async (context, token) =>
            {
                var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(ip, context.DnsEndPoint.Port), token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            }
        };
        using (handler)
        using (var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan })
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(options.DurationSeconds + 15)))
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token))
        {
            try
            {
                long bytes = 0;
                var watch = Stopwatch.StartNew();
                var pacer = new PayloadPacer(options.BandwidthLimitBitsPerSecond);
                long budget = options.BandwidthLimitBitsPerSecond == 0 ? MaxSessionBytes : Math.Min(MaxSessionBytes, options.BandwidthLimitBitsPerSecond / 8 * options.DurationSeconds);
                int roundSize = options.BandwidthLimitBitsPerSecond == 0 ? MaxRoundBytes : (int)Math.Min(MaxRoundBytes, options.BandwidthLimitBitsPerSecond / 8);
                int rounds = 0;
                while (watch.Elapsed.TotalSeconds < options.DurationSeconds && bytes < budget)
                {
                    int size = (int)Math.Min(roundSize, budget - bytes);
                    long delta;
                    if (options.Direction == TestDirection.Download)
                        delta = await DownloadRound(client, new Uri(baseUri, "download?bytes=" + size + "&nonce=" + Guid.NewGuid().ToString("N")), sessionKey, size, pacer, linked.Token);
                    else
                        delta = await UploadRound(client, new Uri(baseUri, "upload"), sessionKey, size, pacer, linked.Token);
                    bytes += delta;
                    rounds++;
                    if (delta == 0) break;
                    if (rounds >= 40) break;
                }
                watch.Stop();
                run.MetricOrigin = "HTTP(S): подтверждённый SpeedForge Endpoint v1";
                run.Limitations.Add("HTTP тест использует ограниченные порции данных и не эквивалентен iPerf");
                run.Limitations.Add("HTTP использует один поток и ограничивает среднюю скорость полезных данных; буферы TCP допускают краткие всплески");
                if (bytes >= MaxSessionBytes) run.Limitations.Add("Тест завершён по лимиту трафика 256 МиБ");
                if (bytes <= 0 || watch.Elapsed.TotalSeconds <= 0)
                {
                    run.Status = RunStatus.NoData; run.ErrorCode = "ZERO_PAYLOAD"; return;
                }
                double throughput = bytes * 8.0 / watch.Elapsed.TotalSeconds / 1_000_000.0;
                if (!double.IsFinite(throughput)) { run.Status = RunStatus.EngineError; run.ErrorCode = "RATE_OVERFLOW"; return; }
                run.Mbps = throughput;
                if (options.Direction == TestDirection.Download) { run.DownloadMbps = throughput; run.ReceiverBytes = bytes; run.ReceiverMbps = throughput; }
                else { run.UploadMbps = throughput; run.SenderBytes = bytes; run.ReceiverBytes = bytes; run.ReceiverMbps = throughput; }
                run.Status = RunStatus.Measured;
            }
            catch (OperationCanceledException)
            {
                run.Status = ct.IsCancellationRequested ? RunStatus.Cancelled : RunStatus.Timeout;
                run.ErrorCode = ct.IsCancellationRequested ? "USER_CANCELLED" : "HTTP_DEADLINE";
            }
            catch (HttpRequestException ex)
            {
                run.Diagnostics?.Event("http.exception", ex.ToString());
                run.Status = RunStatus.NetworkError; run.ErrorCode = "HTTP_REQUEST_FAILED";
                run.ErrorText = ex.GetType().Name;
            }
            catch (InvalidDataException ex)
            {
                run.Diagnostics?.Event("http.exception", ex.ToString());
                run.Status = RunStatus.EngineError; run.ErrorCode = "HTTP_ENDPOINT_PROTOCOL_ERROR";
                run.ErrorText = ex.Message;
            }
        }
    }

    private static void ApplyHeaders(HttpRequestMessage msg, string? key)
    {
        msg.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        msg.Headers.TryAddWithoutValidation("X-SF-Protocol", "v1");
        if (!string.IsNullOrEmpty(key)) msg.Headers.TryAddWithoutValidation("X-SF-Key", key);
    }
    private static void ConfirmEndpoint(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode) throw new InvalidDataException("Сервер отклонил тест: " + (int)response.StatusCode);
        if (!response.Headers.TryGetValues("X-SF-Protocol", out var values) || !values.Contains("v1"))
            throw new InvalidDataException("Это не подтверждённый SpeedForge Endpoint v1");
    }

    private static async Task<long> DownloadRound(HttpClient client, Uri uri, string? key, int size, PayloadPacer pacer, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        ApplyHeaders(request, key);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        ConfirmEndpoint(response);
        if (response.Content.Headers.ContentLength != size || response.Content.Headers.ContentType?.MediaType != "application/octet-stream")
            throw new InvalidDataException("Неправильный размер/тип контролируемой загрузки");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        byte[] buffer = new byte[ChunkSize];
        long total = 0;
        while (true)
        {
            int read = await stream.ReadAsync(buffer, ct);
            if (read == 0) break;
            total += read;
            if (total > size) throw new InvalidDataException("Превышение лимита HTTP-ответа");
            await pacer.AccountAsync(read, ct);
        }
        if (total != size) throw new InvalidDataException("Сервер передал неполный блок");
        return total;
    }
    private static async Task<long> UploadRound(HttpClient client, Uri uri, string? key, int size, PayloadPacer pacer, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        ApplyHeaders(request, key);
        request.Content = new PacedContent(size, pacer);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        ConfirmEndpoint(response);
        if (!response.Headers.TryGetValues("X-SF-Accepted-Bytes", out var values)
            || !long.TryParse(values.SingleOrDefault(), out var accepted) || accepted != size)
            throw new InvalidDataException("Сервер не подтвердил полный принятый объём");
        return accepted;
    }

    private sealed class PayloadPacer(long bitsPerSecond)
    {
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private long _bytes;
        public async Task AccountAsync(int bytes, CancellationToken ct)
        {
            if (bitsPerSecond == 0) return;
            _bytes += bytes;
            double delay = _bytes * 8.0 / bitsPerSecond - _watch.Elapsed.TotalSeconds;
            if (delay > 0) await Task.Delay(TimeSpan.FromSeconds(delay), ct);
        }
    }

    private sealed class PacedContent(int size, PayloadPacer pacer) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = size; return true; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
        {
            byte[] buffer = new byte[ChunkSize];
            for (int sent = 0; sent < size;)
            {
                int count = Math.Min(buffer.Length, size - sent);
                RandomNumberGenerator.Fill(buffer.AsSpan(0, count));
                await pacer.AccountAsync(count, ct);
                await stream.WriteAsync(buffer.AsMemory(0, count), ct);
                sent += count;
            }
        }
    }
}
