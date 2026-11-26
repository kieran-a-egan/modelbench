using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ModelBench.Cli;
using ModelBench.Cli.Commands;
using ModelBench.Tests.Support;

namespace ModelBench.Tests.Cli;

/// <summary>`modelbench web` must only announce its own child: not whatever else answers on the port. Uses the ModelBench.Web.exe in the test output.</summary>
public class WebCommandTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A server on a random port that answers 200 "hello" to every path, like an unrelated service or an older dashboard.</summary>
    private static async Task<(WebApplication app, string url)> UnrelatedServerAsync()
    {
        var b = WebApplication.CreateSlimBuilder();
        b.Logging.ClearProviders();
        b.WebHost.UseUrls("http://127.0.0.1:0");
        var app = b.Build();
        app.Run(ctx => ctx.Response.WriteAsync("hello"));
        await app.StartAsync(Ct);
        return (app, app.Urls.Single());
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Fact] public async Task AnUnrelated200IsNotReady()
    {
        var (app, url) = await UnrelatedServerAsync();
        await using var _ = app;
        Assert.False(await WebCommand.WaitReadyAsync(url, "nonce", () => false, TimeSpan.FromSeconds(1), Ct));
    }

    [Fact] public async Task AChildThatNeverAnswersTimesOut()
        => Assert.False(await WebCommand.WaitReadyAsync($"http://127.0.0.1:{FreePort()}", "nonce", () => false, TimeSpan.FromSeconds(1), Ct));

    [Fact] public async Task AChildThatExitedStopsWaitingImmediately()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(await WebCommand.WaitReadyAsync($"http://127.0.0.1:{FreePort()}", "nonce", () => true, TimeSpan.FromSeconds(30), Ct));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact] public async Task OccupiedPortFailsWithoutAnnouncingTheOtherService()
    {
        using var dir = new TempDir();
        var (app, url) = await UnrelatedServerAsync();
        await using var _ = app;
        var output = new StringWriter();
        var code = await CliApp.RunAsync(["--db", dir.File("w.db"), "web", "--no-browser", "--port", new Uri(url).Port.ToString()], output, output, Ct);
        Assert.Equal(1, code);
        Assert.Contains("exited during startup", output.ToString());
        Assert.DoesNotContain("dashboard:", output.ToString());
    }

    [Fact] public async Task PortOutOfRangeIsRejected()
    {
        var output = new StringWriter();
        Assert.NotEqual(0, await CliApp.RunAsync(["web", "--no-browser", "--port", "70000"], output, output, Ct));
        Assert.Contains("between 1 and 65535", output.ToString());
    }

    [Fact] public async Task StartsAnnouncesAndStopsItsOwnChild()
    {
        using var dir = new TempDir();
        var port = FreePort();
        var sw = new StringWriter();
        var output = TextWriter.Synchronized(sw);
        string Text() { lock (output) return sw.ToString(); } // SyncTextWriter locks on itself
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = CliApp.RunAsync(["--db", dir.File("w.db"), "web", "--no-browser", "--port", port.ToString()], output, output, cts.Token);
        using var http = new HttpClient();
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (!Text().Contains("dashboard:") && !run.IsCompleted && DateTime.UtcNow < deadline) await Task.Delay(100, Ct);
            Assert.Contains($"dashboard: http://127.0.0.1:{port}", Text());
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"http://127.0.0.1:{port}/", Ct)).StatusCode);
        }
        finally { await cts.CancelAsync(); } // also on failure, so the child is never left running
        Assert.Equal(0, await run);
        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync($"http://127.0.0.1:{port}/", Ct)); // child killed
    }
}
