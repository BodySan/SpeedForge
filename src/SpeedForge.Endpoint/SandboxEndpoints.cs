using System.Security.Cryptography;

internal static class SandboxEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapMethods("/sandbox/{scenario}/{direction}", ["GET", "POST"], async (HttpContext ctx, string scenario, string direction) =>
        {
            bool upload = direction == "upload";
            if (scenario == "busy") { ctx.Response.StatusCode = 503; return; }
            if (scenario == "download-only" && upload || scenario == "upload-only" && !upload)
            { ctx.Response.StatusCode = 405; return; }
            if (scenario == "stall") { await Task.Delay(30_000, ctx.RequestAborted); return; }
            if (scenario == "redirect") { ctx.Response.Redirect("/sandbox/both/download"); return; }
            if (scenario == "html") { ctx.Response.ContentType = "text/html"; await ctx.Response.WriteAsync("<html>Not a test file</html>"); return; }
            if (scenario == "json") { ctx.Response.ContentType = "application/json"; await ctx.Response.WriteAsync("{\"message\":\"not a speed payload\"}"); return; }
            ctx.Response.ContentType = "application/octet-stream";
            if (scenario == "binary") ctx.Response.ContentType = "binary/octet-stream";
            if (scenario == "compressed") ctx.Response.Headers.ContentEncoding = "gzip";
            if (scenario == "empty") { ctx.Response.ContentLength = 0; return; }
            if (upload)
            {
                long total = 0;
                byte[] buffer = new byte[65_536];
                while (true)
                {
                    int read = await ctx.Request.Body.ReadAsync(buffer, ctx.RequestAborted);
                    if (read == 0) break;
                    total += read;
                    if (total > 32L * 1_048_576) { ctx.Response.StatusCode = 413; return; }
                }
                ctx.Response.Headers["X-Sandbox-Received"] = total.ToString();
                if (scenario == "ack-stall") await Task.Delay(30_000, ctx.RequestAborted);
                await ctx.Response.WriteAsync("OK", ctx.RequestAborted);
                return;
            }
            ctx.Response.ContentLength = 1_048_576;
            byte[] data = RandomNumberGenerator.GetBytes(65_536);
            for (int i = 0; i < 16; i++)
            {
                await ctx.Response.Body.WriteAsync(data, ctx.RequestAborted);
                if (scenario == "partial")
                {
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                    await Task.Delay(100, ctx.RequestAborted);
                    ctx.Abort(); return;
                }
            }
        });
    }
}
