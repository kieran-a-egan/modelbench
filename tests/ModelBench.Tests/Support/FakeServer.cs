using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Hosting;

namespace ModelBench.Tests.Support;

/// <summary>What the fake OpenAI-compatible endpoint answers: an HTTP error, or a stream of SSE data payloads.</summary>
public sealed record FakeReply(int Status = 200, IReadOnlyList<string>? Chunks = null, string Body = "")
{
    public static FakeReply Error(int status, string body = "boom") => new(status, null, body);

    public static string Delta(JsonObject delta, string? finish = null)
        => new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["delta"] = delta, ["finish_reason"] = finish }) }.ToJsonString();

    public static string UsageChunk(long prompt, long completion, long? reasoning)
    {
        var usage = new JsonObject { ["prompt_tokens"] = prompt, ["completion_tokens"] = completion };
        if (reasoning is { } r) usage["completion_tokens_details"] = new JsonObject { ["reasoning_tokens"] = r };
        return new JsonObject { ["choices"] = new JsonArray(), ["usage"] = usage }.ToJsonString();
    }

    public static FakeReply Text(string text, long prompt = 10, long completion = 5, long? reasoning = null)
        => new(200,
        [
            Delta(new JsonObject { ["content"] = text }),
            Delta([], "stop"),
            UsageChunk(prompt, completion, reasoning),
        ]);
}

/// <summary>In-process Kestrel server (port 0) that records every request body and Authorization header.</summary>
public sealed class FakeServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly List<string> _bodies = [];
    private readonly List<string?> _auth = [];
    private int _calls;

    public string BaseUrl { get; }

    public IReadOnlyList<string> Bodies { get { lock (_bodies) return _bodies.ToList(); } }
    public IReadOnlyList<string?> AuthHeaders { get { lock (_bodies) return _auth.ToList(); } }
    public int Calls => _calls;

    private FakeServer(WebApplication app, string baseUrl) { _app = app; BaseUrl = baseUrl; }

    /// <param name="handler">(zero-based call index, parsed request body) -> reply</param>
    public static async Task<FakeServer> StartAsync(Func<int, JsonObject, FakeReply> handler)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        FakeServer? server = null;
        app.MapPost("/v1/chat/completions", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var raw = await reader.ReadToEndAsync();
            int index;
            lock (server!._bodies) { server._bodies.Add(raw); server._auth.Add(ctx.Request.Headers.Authorization.FirstOrDefault()); index = server._calls++; }
            var reply = handler(index, JsonNode.Parse(raw)!.AsObject());
            ctx.Response.StatusCode = reply.Status;
            if (reply.Chunks is null) { await ctx.Response.WriteAsync(reply.Body); return; }
            ctx.Response.ContentType = "text/event-stream";
            foreach (var chunk in reply.Chunks)
            {
                await ctx.Response.WriteAsync($"data: {chunk}\n\n");
                await ctx.Response.Body.FlushAsync();
                await Task.Delay(15);
            }
            await ctx.Response.WriteAsync("data: [DONE]\n\n");
        });
        await app.StartAsync();
        server = new FakeServer(app, app.Urls.First().TrimEnd('/') + "/v1");
        return server;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
