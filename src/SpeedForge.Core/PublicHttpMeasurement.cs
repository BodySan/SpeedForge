using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace SpeedForge.Core;

/// <summary>Bounded tests against published file and LibreSpeed endpoints.</summary>
public sealed class PublicHttpMeasurement
{
    public const long MaxBytes = 512L * 1_048_576;
    public async Task ExecuteAsync(ServerEntry server, TestOptions options, IPAddress ip,
        RunResult run, CancellationToken ct, IProgress<MeasurementProgress>? live = null)
    {
        options.Validate();
        var profile = server.Http ?? throw new InvalidOperationException("Отсутствует HTTP-профиль");
        if (options.Transport != TransportKind.Tcp || options.Direction == TestDirection.Bidirectional
            || options.Direction == TestDirection.Upload && profile.UploadUrl is null)
        {
            run.Status = RunStatus.Unsupported;
            run.ErrorCode = "HTTP_MODE_UNSUPPORTED";
            run.ErrorText = profile.UploadUrl is null ? "У этой точки опубликована только загрузка. Выберите сервер с отдачей." : "HTTP поддерживает TCP и последовательные направления.";
            return;
        }
        var uri = ValidateUri(options.Direction == TestDirection.Download ? profile.DownloadUrl : profile.UploadUrl!, server);
        run.Diagnostics?.Event("http.start", $"URL={uri}; protocol={profile.Protocol}; direction={options.Direction}; pinned IP={ip}; proxy=false; redirect=false; decompression=false; max bytes={MaxBytes}");
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(6),
            ConnectCallback = async (context, token) =>
            {
                var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    run.Diagnostics?.Event("tcp.connect", $"{ip}:{context.DnsEndPoint.Port}");
                    await socket.ConnectAsync(new IPEndPoint(ip, context.DnsEndPoint.Port), token);
                    run.Diagnostics?.Event("tcp.connected", $"local={socket.LocalEndPoint}; remote={socket.RemoteEndPoint}");
                    return new NetworkStream(socket, true);
                }
                catch (Exception ex) { run.Diagnostics?.Event("tcp.exception", ex.ToString()); socket.Dispose(); throw; }
            }
        };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var duration = new CancellationTokenSource(TimeSpan.FromSeconds(options.DurationSeconds));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, duration.Token);
        var state = new TransferState(options.BandwidthLimitBitsPerSecond);
        using var tickerStop = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        var ticker = TickAsync(state, run, options.Direction, live, tickerStop.Token);
        try
        {
            var workers = Enumerable.Range(0, options.ParallelStreams).Select(_ => TransferAsync()).ToArray();
            await Task.WhenAll(workers);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && duration.IsCancellationRequested) { }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        { run.Status = RunStatus.Cancelled; run.ErrorCode = "USER_CANCELLED"; return; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
        {
            run.Diagnostics?.Event("http.exception", ex.ToString());
            run.Status = state.Bytes > 0 ? RunStatus.Partial : RunStatus.NetworkError;
            run.ErrorCode = "HTTP_REQUEST_FAILED";
            run.ErrorText = ex.Message;
        }
        finally
        {
            tickerStop.Cancel();
            await ticker;
        }
        double seconds = state.Watch.Elapsed.TotalSeconds;
        run.Diagnostics?.Event("http.finish", $"seconds={seconds:F6}; transferred={state.Bytes}; acknowledged={state.AcknowledgedBytes}; deadline={duration.IsCancellationRequested}; user cancel={ct.IsCancellationRequested}");
        long measured = options.Direction == TestDirection.Download ? state.Bytes : state.AcknowledgedBytes;
        if (ct.IsCancellationRequested) { run.Status = RunStatus.Cancelled; run.ErrorCode = "USER_CANCELLED"; return; }
        if (measured == 0)
        {
            if (run.Status != RunStatus.NetworkError)
            {
                run.Status = duration.IsCancellationRequested ? RunStatus.Timeout : RunStatus.NoData;
                run.ErrorCode = duration.IsCancellationRequested ? "HTTP_TEST_TIMEOUT" : "ZERO_PAYLOAD";
            }
            return;
        }
        run.Mbps = measured * 8.0 / seconds / 1_000_000;
        if (options.Direction == TestDirection.Download)
        { run.DownloadMbps = run.ReceiverMbps = run.Mbps; run.ReceiverBytes = measured; }
        else
        {
            run.UploadMbps = run.SenderMbps = run.Mbps; run.SenderBytes = measured;
            run.Limitations.Add("HTTP отдача: учитываются полностью отправленные блоки с успешным ответом сервера; отдельной статистики приёмника нет");
        }
        if (run.Status != RunStatus.Partial) run.Status = RunStatus.Measured;
        run.MetricOrigin = $"{uri.Scheme.ToUpperInvariant()} {profile.Protocol}: " + (options.Direction == TestDirection.Download ? "байты, полученные клиентом" : "отправленные блоки, сервер вернул 2xx");
        run.Limitations.Add("HTTP и iPerf используют разные методики; результат относится к выбранной точке");
        if (state.Bytes >= MaxBytes) run.Limitations.Add("Достигнут предел полезного трафика 512 МиБ");
        live?.Report(new(options.Direction, seconds, run.Mbps.Value, measured));

        async Task TransferAsync()
        {
            try
            {
                while (!stop.IsCancellationRequested && state.Bytes < MaxBytes)
                {
                    if (options.Direction == TestDirection.Download)
                    {
                        var builder = new UriBuilder(uri);
                        builder.Query = uri.Query.TrimStart('?') + (string.IsNullOrEmpty(uri.Query) ? "" : "&") + "sf_nonce=" + Guid.NewGuid().ToString("N");
                        using var request = Request(HttpMethod.Get, builder.Uri);
                        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stop.Token);
                        TraceResponse(response, run);
                        Confirm(response, download: true);
                        await using var stream = await response.Content.ReadAsStreamAsync(stop.Token);
                        byte[] buffer = new byte[65_536];
                        long received = 0;
                        while (!stop.IsCancellationRequested)
                        {
                            int allowed = state.Reserve(buffer.Length);
                            if (allowed == 0) return;
                            int read;
                            try { read = await stream.ReadAsync(buffer.AsMemory(0, allowed), stop.Token); }
                            catch { state.Unreserve(allowed); throw; }
                            state.Unreserve(allowed - read);
                            if (read == 0) break;
                            received += read;
                            await state.AccountAsync(read, stop.Token);
                        }
                        if (received == 0) throw new InvalidDataException("Сервер вернул пустой файл");
                    }
                    else
                    {
                        // Keep blocks short enough to receive acknowledgements even at a low shared rate.
                        int blockSize = options.BandwidthLimitBitsPerSecond == 0 ? 1_048_576
                            : (int)Math.Min(1_048_576, Math.Max(4096, options.BandwidthLimitBitsPerSecond / 8 / options.ParallelStreams / 2));
                        int block = state.Reserve(blockSize);
                        if (block == 0) return;
                        using var request = Request(new HttpMethod(profile.UploadMethod), uri);
                        var content = new UploadContent(block, state);
                        request.Content = content;
                        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                        try
                        {
                            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stop.Token);
                            TraceResponse(response, run);
                            Confirm(response, download: false);
                            Interlocked.Add(ref state.AcknowledgedBytes, block);
                        }
                        finally { state.Unreserve(block - content.Sent); }
                    }
                }
            }
            catch
            {
                // Abort sibling requests immediately when one stream fails.
                stop.Cancel();
                throw;
            }
        }
    }

    private static void TraceResponse(HttpResponseMessage response, RunResult run) =>
        run.Diagnostics?.Event("http.response", $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}; HTTP/{response.Version} {(int)response.StatusCode} {response.ReasonPhrase}; Content-Type={response.Content.Headers.ContentType}; Content-Length={response.Content.Headers.ContentLength}; encoding={string.Join(',', response.Content.Headers.ContentEncoding)}; Location={response.Headers.Location}; Cache-Control={response.Headers.CacheControl}");

    public static Uri ValidateUri(string url, ServerEntry server)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment)
            || !uri.Host.Equals(server.Host, StringComparison.OrdinalIgnoreCase) || uri.Port != server.Port)
            throw new TargetRejectedException("HTTP URL должен указывать на выбранный сервер и порт");
        return uri;
    }
    private static HttpRequestMessage Request(HttpMethod method, Uri uri)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        request.Headers.AcceptEncoding.ParseAdd("identity");
        request.Headers.UserAgent.ParseAdd("SpeedForge/1.0");
        return request;
    }
    private static void Confirm(HttpResponseMessage response, bool download)
    {
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Сервер вернул HTTP {(int)response.StatusCode}. Проверьте доступность выбранной точки.");
        string? type = response.Content.Headers.ContentType?.MediaType;
        if (download && (type is null || !type.StartsWith("application/", StringComparison.OrdinalIgnoreCase)
            || type.Contains("json", StringComparison.OrdinalIgnoreCase) || type.Contains("xml", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Вместо тестовых данных сервер вернул страницу или неизвестный тип содержимого");
        if (response.Content.Headers.ContentEncoding.Any(x => !x.Equals("identity", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Сжатый ответ нельзя использовать для измерения объёма трафика");
    }
    private static async Task TickAsync(TransferState state, RunResult run, TestDirection direction,
        IProgress<MeasurementProgress>? progress, CancellationToken ct)
    {
        long previous = 0;
        double start = 0;
        try
        {
            while (true)
            {
                await Task.Delay(500, ct);
                long bytes = Interlocked.Read(ref state.Bytes);
                double end = state.Watch.Elapsed.TotalSeconds;
                double bps = (bytes - previous) * 8.0 / (end - start);
                run.Intervals.Add(new(start, end, bytes - previous, bps));
                progress?.Report(new(direction, end, bps / 1_000_000, bytes));
                previous = bytes; start = end;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
    private sealed class TransferState(long limit)
    {
        public readonly Stopwatch Watch = Stopwatch.StartNew();
        public long Bytes, AcknowledgedBytes;
        private long _reserved;
        public int Reserve(int wanted)
        {
            while (true)
            {
                long current = Interlocked.Read(ref _reserved);
                int size = (int)Math.Min(wanted, MaxBytes - current);
                if (size <= 0) return 0;
                if (Interlocked.CompareExchange(ref _reserved, current + size, current) == current) return size;
            }
        }
        public void Unreserve(int unused) => Interlocked.Add(ref _reserved, -unused);
        public async Task AccountAsync(int bytes, CancellationToken ct)
        {
            long total = Interlocked.Add(ref Bytes, bytes);
            if (limit == 0) return;
            double delay = total * 8.0 / limit - Watch.Elapsed.TotalSeconds;
            if (delay > 0) await Task.Delay(TimeSpan.FromSeconds(delay), ct);
        }
    }
    private sealed class UploadContent(int size, TransferState state) : HttpContent
    {
        public int Sent { get; private set; }
        protected override bool TryComputeLength(out long length) { length = size; return true; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
        {
            byte[] buffer = new byte[16_384];
            while (Sent < size)
            {
                int count = Math.Min(buffer.Length, size - Sent);
                RandomNumberGenerator.Fill(buffer.AsSpan(0, count));
                await stream.WriteAsync(buffer.AsMemory(0, count), ct);
                Sent += count;
                await state.AccountAsync(count, ct);
            }
        }
    }
}
