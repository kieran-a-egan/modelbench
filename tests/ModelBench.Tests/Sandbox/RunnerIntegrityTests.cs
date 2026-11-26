using System.Text;
using System.Text.Json;
using ModelBench.Core.Domain;
using ModelBench.Core.Grading;
using ModelBench.Core.Sandbox;
using static ModelBench.Tests.Sandbox.SandboxEnv;

namespace ModelBench.Tests.Sandbox;

/// <summary>
/// MB-004 / #3: candidate code runs in a fresh child process per case, and the runner (PID 1, not dumpable) is the only writer of
/// result frames. These attacks run in the real container: none may produce a frame, reach another case, or yield a false pass.
/// </summary>
public class RunnerIntegrityTests
{
    private const int Secret = 987_654_321;

    [Fact] public async Task TheRequestVisibleToCandidateCodeHoldsNoExpectedAnswer()
    {
        var sb = await Require();
        var cfg = new { language = "python", entryPoint = "f", cases = new[] { new { args = new[] { 1 }, expected = Secret } } };
        var code = """
            import sys, json
            def f(x):
                fr = sys._getframe()
                while "req" not in fr.f_locals: fr = fr.f_back
                return json.dumps(fr.f_locals["req"])
            """;
        var ctx = new GradingContext(TaskPrompt.Simple("q", AnswerFormat.CodeBlock), new TaskHidden([]), code, code, [], null, sb, default);
        var r = Assert.Single(await GraderRegistry.Get("unit-tests").GradeAsync(new GraderSpec("unit-tests", "g", JsonUtil.Element(cfg)), ctx));
        Assert.False(r.Passed);
        Assert.Contains("nonce", r.Actual); // it really did read the whole request...
        Assert.DoesNotContain(Secret.ToString(), r.Actual); // ...and the answer was not in it
    }

    [Fact] public async Task PythonCannotWriteAFrameWithTheRealNonce()
    {
        // The child even holds the real nonce (fork copies the runner's stack), but every fd it can write, and /proc/1/fd/1, lead nowhere.
        var r = await Py("""
            import sys, os, json, base64
            def f(x):
                fr, nonce = sys._getframe(), None
                while fr is not None and nonce is None:
                    nonce, fr = fr.f_locals.get("nonce"), fr.f_back
                body = {"compiled": True, "compileErrors": None, "astFacts": None,
                        "cases": [{"index": 0, "output": 987654321, "error": None, "elapsedMs": 0.0}]}
                line = ("MBRESULT:%s:%s\n" % (nonce, base64.b64encode(json.dumps(body).encode()).decode())).encode()
                for fd in [d for d in range(256) if d != 3]:  # fd 3 is this case's own result pipe
                    try: os.write(fd, line)
                    except OSError: pass
                out = {"nonce": nonce is not None}
                for p in ("/proc/1/fd/1", "/proc/1/mem", "/proc/1/environ"):
                    try:
                        with open(p, "wb") as fh: fh.write(line)
                        out[p] = "opened"
                    except OSError:
                        out[p] = "denied"
                return out
            """, "f", 10_000, "[0]", "[1]");
        Assert.Equal(2, r.Cases.Count); // a second frame would have made the result unusable
        foreach (var i in new[] { 0, 1 })
        {
            var o = JsonDocument.Parse(Out(r, i)).RootElement;
            Assert.True(o.GetProperty("nonce").GetBoolean());
            foreach (var p in new[] { "/proc/1/fd/1", "/proc/1/mem", "/proc/1/environ" }) Assert.Equal("denied", o.GetProperty(p).GetString());
        }
    }

    [Fact] public async Task CSharpCannotReachTheRunnersStdout()
    {
        var r = await Cs("""
            public static class S { public static string F(int x) {
              var line = Encoding.UTF8.GetBytes("MBRESULT:x:e30=\n");
              using (var o = Console.OpenStandardOutput()) o.Write(line); // this process's stdout is its own result pipe
              var res = new List<string>();
              foreach (var p in new[] { "/proc/1/fd/1", "/proc/1/mem" })
                try { using var f = File.OpenWrite(p); f.Write(line); res.Add("opened"); } catch (Exception) { res.Add("denied"); }
              return string.Join(",", res); } }
            """, "S.F", 10_000, "[0]");
        // Its stray bytes corrupt only its own case's result; the runner's frame is intact and the case is an error, not a pass.
        var c = Assert.Single(r.Cases);
        Assert.Equal("case process produced no result", c.Error);

        var probe = await Cs("""
            public static class S { public static string F() {
              var res = new List<string>();
              foreach (var p in new[] { "/proc/1/fd/1", "/proc/1/mem" })
                try { using var f = File.OpenWrite(p); res.Add("opened"); } catch (Exception) { res.Add("denied"); }
              return string.Join(",", res); } }
            """);
        Assert.Equal("\"denied,denied\"", Out(probe));
    }

    [Fact] public async Task PythonStateNeverCarriesToTheNextCase()
    {
        var r = await Py("""
            import builtins, os, ctypes
            libc = ctypes.CDLL(None, use_errno=True)
            count = 0
            def f(x):
                global count
                count += 1
                seen = [getattr(builtins, "mark", None), os.path.exists("/tmp/mark"), os.path.exists("/dev/shm/mark"), os.path.exists("/dev/mqueue/mark"), libc.shmget(4242, 0, 0) >= 0, libc.msgget(4242, 0) >= 0, libc.semget(4242, 0, 0) >= 0, len([p for p in os.listdir("/proc") if p.isdigit()])]
                builtins.mark = 1
                builtins.sorted = lambda *a, **k: [42]
                open("/tmp/mark", "w").write("1")
                open("/dev/shm/mark", "w").write("1")
                libc.mq_open.argtypes = [ctypes.c_char_p, ctypes.c_int, ctypes.c_int, ctypes.c_void_p]
                libc.mq_open(b"/mark", 0o102, 0o600, None)  # O_CREAT|O_RDWR
                libc.shmget(4242, 100, 0o1600); libc.msgget(4242, 0o1600); libc.semget(4242, 1, 0o1600)  # IPC_CREAT
                if os.fork() == 0:  # a leftover process that would otherwise live into the next case
                    import time; time.sleep(60); os._exit(0)
                return [count] + seen
            """, "f", 10_000, "[0]", "[1]", "[2]");
        // Every case: a fresh module, untouched builtins, empty /tmp, /dev/shm, message queues and SysV IPC, and only the runner and itself alive.
        Assert.All(Enumerable.Range(0, 3), i => Assert.Equal("[1,null,false,false,false,false,false,false,2]", Out(r, i)));
    }

    [Fact] public async Task CSharpStateNeverCarriesToTheNextCase()
    {
        var r = await Cs("""
            public static class S { static int count;
              public static string F(int x) {
                count++;
                var seen = $"{count},{Environment.GetEnvironmentVariable("MB_MARK") ?? "-"},{File.Exists("/tmp/mark")}";
                Environment.SetEnvironmentVariable("MB_MARK", "1");
                File.WriteAllText("/tmp/mark", "1");
                return seen; } }
            """, "S.F", 10_000, "[0]", "[1]");
        Assert.All(Enumerable.Range(0, 2), i => Assert.Equal("\"1,-,False\"", Out(r, i)));
    }

    [Fact] public async Task PythonPrematureExitFailsOnlyItsOwnCase()
    {
        var r = await Py("""
            import os, sys, signal
            def f(x):
                if x == 0: os._exit(0)
                if x == 1: sys.exit(5)
                if x == 2: os.kill(os.getpid(), signal.SIGKILL)
                if x == 3: os.kill(1, signal.SIGKILL)  # init ignores it
                return x
            """, "f", 10_000, "[0]", "[1]", "[2]", "[3]", "[4]");
        Assert.Equal(5, r.Cases.Count);
        Assert.Equal("case process produced no result", r.Cases[0].Error);
        Assert.Equal("case process exited with status 1", r.Cases[1].Error); // SystemExit escapes the case and the child exits 1
        Assert.Equal("case process killed by signal 9", r.Cases[2].Error);
        Assert.Equal("3", Out(r, 3));
        Assert.Equal("4", Out(r, 4));
    }

    [Fact] public async Task CSharpPrematureExitFailsOnlyItsOwnCase()
    {
        var r = await Cs("""
            public static class S {
              static int Down(int n) => Down(n + 1) + 1;
              public static int F(int x) {
                if (x == 0) Environment.Exit(3);
                if (x == 1) return Down(0);
                return x; } }
            """, "S.F", 10_000, "[0]", "[1]", "[2]");
        Assert.Equal(3, r.Cases.Count);
        Assert.Equal("case process exited with status 3", r.Cases[0].Error);
        Assert.StartsWith("case process exited with status", r.Cases[1].Error); // stack overflow aborts only its own process
        Assert.Equal("2", Out(r, 2));
    }

    [Fact] public void AFrameIsAcceptedOnlyWhenTheRunnerExitedCleanly()
    {
        var body = """{"compiled":true,"compileErrors":null,"astFacts":null,"cases":[{"index":0,"output":1,"error":null,"elapsedMs":0.0}]}""";
        var stdout = $"MBRESULT:n:{Convert.ToBase64String(Encoding.UTF8.GetBytes(body))}\n";
        Assert.NotNull(DockerSandbox.ParseFrame(stdout, "n", 0));
        Assert.Null(DockerSandbox.ParseFrame(stdout, "n", 1));
        Assert.Null(DockerSandbox.ParseFrame(stdout, "n", 137));
    }
}
