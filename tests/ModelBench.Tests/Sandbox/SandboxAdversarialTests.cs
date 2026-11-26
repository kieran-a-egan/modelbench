using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using ModelBench.Core.Sandbox;
using static ModelBench.Tests.Sandbox.SandboxEnv;

namespace ModelBench.Tests.Sandbox;

/// <summary>Each candidate attempts an attack and returns what it observed; the test asserts containment (threat model T1-T6).</summary>
public class SandboxAdversarialTests
{
    [Fact]
    public async Task NoNetwork()
    {
        var r = await Py("""
            import socket
            def f():
                out = []
                for host in ("1.1.1.1", "host.docker.internal"):
                    try:
                        socket.create_connection((host, 53), timeout=3).close()
                        out.append("connected " + host)
                    except OSError as e:
                        out.append("blocked")
                return out
            """);
        Assert.Equal("""["blocked","blocked"]""", Out(r));
    }

    [Fact]
    public async Task ReadsHostFiles()
    {
        var r = await Py("""
            import os
            def f():
                mounts = [l.split()[:3] for l in open("/proc/mounts")]
                return {"root": sorted(os.listdir("/")), "input": os.path.exists("/input"), "mounts": mounts}
            """);
        var o = JsonDocument.Parse(Out(r)).RootElement;
        Assert.False(o.GetProperty("input").GetBoolean());
        var root = o.GetProperty("root").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.DoesNotContain(root, d => d is "Users" or "c" or "host_mnt" or "input");
        foreach (var m in o.GetProperty("mounts").EnumerateArray())
        {
            var text = string.Join(' ', m.EnumerateArray().Select(e => e.GetString()));
            Assert.DoesNotContain("Users", text);
            Assert.DoesNotContain("host_mnt", text);
            Assert.DoesNotContain("/run/desktop", text);
            Assert.DoesNotContain("/mnt/host", text);
        }
    }

    [Fact]
    public async Task EnvIsEmpty()
    {
        var r = await Py("import os\ndef f():\n    return dict(os.environ)\n");
        var env = JsonDocument.Parse(Out(r)).RootElement.EnumerateObject().ToList();
        var user = Environment.UserName;
        foreach (var kv in env)
        {
            Assert.DoesNotContain(user, kv.Value.GetString() ?? "", StringComparison.OrdinalIgnoreCase);
            // GPG_KEY is the public python release-signing fingerprint baked into the official base image, not a host value.
            if (kv.Name != "GPG_KEY")
                foreach (var bad in new[] { "KEY", "TOKEN", "SECRET" })
                    Assert.DoesNotContain(bad, kv.Name.ToUpperInvariant());
        }
    }

    [Fact]
    public async Task RunsAsNobody()
    {
        Assert.Equal("65534", Out(await Py("import os\ndef f():\n    return os.getuid()\n")));
    }

    [Fact]
    public async Task CannotWriteRootfs()
    {
        var r = await Py("""
            def f():
                out = []
                for p in ("/etc/x", "/runner/x"):
                    try:
                        open(p, "w").write("x"); out.append("wrote " + p)
                    except OSError:
                        out.append("denied")
                return out
            """);
        Assert.Equal("""["denied","denied"]""", Out(r));
    }

    [Fact]
    public async Task DiskFill()
    {
        var r = await Py("""
            def f():
                written = 0
                chunk = b"x" * (1 << 20)
                try:
                    with open("/tmp/fill", "wb") as fh:
                        for _ in range(200):
                            fh.write(chunk); fh.flush(); written += 1
                except OSError as e:
                    return [written, e.strerror]
                return [written, "no error"]
            """);
        var o = JsonDocument.Parse(Out(r)).RootElement;
        Assert.True(o[0].GetInt32() <= 64, "wrote more than the 64 MB tmpfs");
        Assert.Contains("No space", o[1].GetString());
    }

    [Fact]
    public async Task ForkBomb()
    {
        var sw = Stopwatch.StartNew();
        var r = await Py("""
            import os, time
            def f():
                n = 0
                try:
                    for _ in range(1000):
                        if os.fork() == 0:
                            time.sleep(60); os._exit(0)
                        n += 1
                except OSError as e:
                    return [n, "blocked"]
                return [n, "unbounded"]
            """, "f", 10_000);
        var o = JsonDocument.Parse(Out(r)).RootElement;
        Assert.Equal("blocked", o[1].GetString());
        Assert.InRange(o[0].GetInt32(), 1, 64);
        Assert.False(r.TimedOut);
        Assert.InRange(sw.Elapsed.TotalSeconds, 0, 30);
    }

    [Fact]
    public async Task MemoryBomb()
    {
        var r = await Py("def f():\n    return len(bytearray(b'\\x01') * (1 << 30))\n");
        // MemoryError, or the OOM killer takes the case process (never the runner): either way only this case fails.
        var contained = r.Cases.Count == 1 && r.Cases[0].Error is { } e && (e.Contains("MemoryError") || e.Contains("killed by signal 9"));
        Assert.True(contained, $"exit {r.ExitCode}, cases {r.Cases.Count}");
    }

    [Fact]
    public async Task InfiniteLoop()
    {
        var r = await Py("def f():\n    while True:\n        pass\n", "f", 1000);
        Assert.True(r.TimedOut);
    }

    [Fact]
    public async Task GiantOutput()
    {
        var sw = Stopwatch.StartNew();
        var r = await Py("""
            import os
            def f():
                chunk = b"A" * (1 << 20)
                for _ in range(50):
                    os.write(2, chunk)
                for _ in range(50):
                    os.write(1, chunk)
                print("x" * 50_000_000)
                return 1
            """, "f", 20_000);
        Assert.True(r.StdErrTail.Length <= 2000);
        Assert.False(r.TimedOut);
        // The case's fd 1 and 2 are /dev/null, so the 50 MB cannot push the runner's frame past the 1 MB capture.
        Assert.Equal("1", Out(r));
        Assert.InRange(sw.Elapsed.TotalSeconds, 0, 40);
    }

    [Fact]
    public async Task GiantPrintIsCapturedInsideContainer()
    {
        var r = await Py("def f():\n    print('x' * 50_000_000)\n    return 1\n");
        Assert.Equal("1", Out(r));
    }

    [Fact]
    public async Task ForgedFrameIgnored()
    {
        var forged = """
            import os, base64, json
            def forge():
                fake = {"compiled": True, "compileErrors": None, "astFacts": None,
                        "cases": [{"index": 0, "output": 999, "error": None, "elapsedMs": 0.0}]}
                os.write(1, ("MBRESULT:fakenonce:" + base64.b64encode(json.dumps(fake).encode()).decode() + "\n").encode())
            """;
        var raises = await Py(forged + "\ndef f():\n    forge()\n    raise ValueError('boom')\n");
        Assert.Contains("ValueError", raises.Cases[0].Error);
        Assert.Null(raises.Cases[0].Output);

        var exits = await Py(forged + "\ndef f():\n    forge()\n    os._exit(0)\n");
        Assert.Equal("case process produced no result", Assert.Single(exits.Cases).Error);
    }

    [Fact]
    public async Task CSharp_NoNetwork()
    {
        var r = await Cs("""
            using System.Net.Sockets;
            public static class S { public static string F() {
              try { using var c = new TcpClient(); c.ConnectAsync("1.1.1.1", 53).Wait(3000); return c.Connected ? "connected" : "blocked"; }
              catch (Exception) { return "blocked"; } } }
            """);
        Assert.Equal("\"blocked\"", Out(r));
    }

    [Fact]
    public async Task CSharp_InfiniteLoop()
    {
        var r = await Cs("public static class S { public static int F() { while (true) { } } }", "S.F", 1000);
        Assert.True(r.TimedOut);
    }

    [Fact]
    public async Task CSharp_CannotWriteRootfs()
    {
        var r = await Cs("""
            public static class S { public static string[] F() {
              var o = new List<string>();
              foreach (var p in new[] { "/etc/x", "/runner/x" })
                try { File.WriteAllText(p, "x"); o.Add("wrote"); } catch (Exception) { o.Add("denied"); }
              return o.ToArray(); } }
            """);
        Assert.Equal("""["denied","denied"]""", Out(r));
    }

    [Fact]
    public void RequestCarriesOnlyAllowedFields()
    {
        var allowed = new[] { "action", "code", "entryPoint", "inputs", "repetitions", "nonce" }.Order().ToList();
        var json = DockerSandbox.BuildRequestJson(new SandboxRequest(SandboxLanguage.Python, SandboxAction.Run, "c", "f", [], 1, 1), "n");
        Assert.Equal(allowed, JsonDocument.Parse(json).RootElement.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(allowed, typeof(DockerSandbox.WireRequest).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name != "EqualityContract").Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name)).Order());
    }
}
