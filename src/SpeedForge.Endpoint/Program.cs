using System.Security.Cryptography;

// Default bind: localhost ONLY. Explicit HTTPS reverse proxy, firewall and authentication are
// required before exposing to a wider network. This server is optional infrastructure.
var builder = WebApplication.CreateBuilder(args);
// Kestrel's default body limit is smaller than the protocol's 32 MiB upload block.
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 32L * 1_048_576);
bool sandbox = builder.Configuration["sandbox"] == "true";
builder.WebHost.UseUrls(sandbox ? "http://127.0.0.1:5090" : "http://127.0.0.1:5088");
var app = builder.Build();
const int BlockSize = 65_536;
const long MaxBytes = 32L * 1_048_576;
var slots = new SemaphoreSlim(2, 2);
var key = Environment.GetEnvironmentVariable("SF_SHARED_KEY");
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["Cache-Control"] = "no-store, no-cache";
    if (ctx.Connection.RemoteIpAddress is not { } remote || !System.Net.IPAddress.IsLoopback(remote))
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    // For remote deployment: local-only restriction is intended. Use a trusted reverse proxy
    // on the same machine with TLS + firewall + an SF_SHARED_KEY to expose this endpoint.
    if (!string.IsNullOrEmpty(key) && ctx.Request.Headers["X-SF-Key"] != key)
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }
    // Allow completion of a preceding response before rejecting the next block.
    // The simultaneous transfer cap remains two; the queue is bounded in time.
    if (!await slots.WaitAsync(TimeSpan.FromMilliseconds(100), ctx.RequestAborted))
    {
        ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        return;
    }
    try { await next(); }
    finally { slots.Release(); }
});

app.MapGet("/health", () => Results.Ok(new { status = "ready", version = "v1" }));
app.MapGet("/download", async (HttpContext ctx, long bytes) =>
{
    if (bytes is < 1 or > MaxBytes) { ctx.Response.StatusCode = 400; return; }
    ctx.Response.ContentType = "application/octet-stream";
    ctx.Response.ContentLength = bytes;
    ctx.Response.Headers["X-SF-Protocol"] = "v1";
    // Generated each request to inhibit compression/caching; write in bounded chunks.
    var buffer = new byte[BlockSize];
    long sent = 0;
    while (sent < bytes && !ctx.RequestAborted.IsCancellationRequested)
    {
        int len = (int)Math.Min(buffer.Length, bytes - sent);
        RandomNumberGenerator.Fill(buffer.AsSpan(0, len));
        await ctx.Response.Body.WriteAsync(buffer.AsMemory(0, len), ctx.RequestAborted);
        sent += len;
    }
});
app.MapPost("/upload", async (HttpContext ctx) =>
{
    if (ctx.Request.ContentLength is > MaxBytes)
    { ctx.Response.StatusCode = 413; return; }
    if (ctx.Request.ContentType != "application/octet-stream")
    { ctx.Response.StatusCode = 415; return; }
    long received = 0;
    byte[] buffer = new byte[BlockSize];
    while (true)
    {
        int read = await ctx.Request.Body.ReadAsync(buffer, ctx.RequestAborted);
        if (read == 0) break;
        received += read;
        if (received > MaxBytes) { ctx.Response.StatusCode = 413; return; }
    }
    ctx.Response.Headers["X-SF-Protocol"] = "v1";
    ctx.Response.Headers["X-SF-Accepted-Bytes"] = received.ToString(System.Globalization.CultureInfo.InvariantCulture);
    await ctx.Response.WriteAsync("OK", ctx.RequestAborted);
});
if (sandbox) SandboxEndpoints.Map(app);
app.Run();
