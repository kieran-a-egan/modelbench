using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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

    [Fact] public async Task PythonUnreadableDirectoriesAreClearedForTheNextCase()
    {
        var r = await Py("""
            import os
            def f(x):
                seen = [os.listdir(d) for d in ("/tmp", "/dev/shm")]
                if x == 0:
                    for d in ("/tmp", "/dev/shm"):
                        os.makedirs(d + "/a/b")
                        open(d + "/a/b/f", "w").write("1")
                        os.chmod(d + "/a/b", 0); os.chmod(d + "/a", 0)
                return seen
            """, "f", 10_000, "[0]", "[1]");
        Assert.All(Enumerable.Range(0, 2), i => Assert.Equal("[[],[]]", Out(r, i)));
    }

    [Fact] public async Task CSharpUnreadableDirectoriesAreClearedForTheNextCase()
    {
        var r = await Cs("""
            public static class S { public static string F(int x) {
                var seen = string.Join(",", new[] { "/tmp", "/dev/shm" }.Select(d => Directory.GetFileSystemEntries(d).Length));
                if (x == 0) foreach (var d in new[] { "/tmp", "/dev/shm" }) {
                    Directory.CreateDirectory(d + "/a/b");
                    File.WriteAllText(d + "/a/b/f", "1");
                    File.SetUnixFileMode(d + "/a/b", 0); File.SetUnixFileMode(d + "/a", 0); }
                return seen; } }
            """, "S.F", 10_000, "[0]", "[1]");
        Assert.All(Enumerable.Range(0, 2), i => Assert.Equal("\"0,0\"", Out(r, i)));
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

    // Signals sent to PID 1 from inside the container are dropped unless PID 1 has a handler, so the kill alone proves little
    // for Python (the survival check passes even without the ignore code). The meaningful assertion is the runner's own signal disposition.
    [Fact] public async Task PythonCaseCannotSignalTheRunner()
    {
        var r = await Py("""
            import os, signal
            def f(x):
                if x == 0:
                    os.kill(1, signal.SIGINT); os.kill(1, signal.SIGTERM)
                if x == 2:
                    return int([l for l in open('/proc/1/status') if l.startswith('SigIgn')][0].split()[1], 16)
                return x
            """, "f", 10_000, "[0]", "[1]", "[2]");
        Assert.Equal(0, r.ExitCode);
        Assert.Equal("0", Out(r, 0));
        Assert.Equal("1", Out(r, 1));
        const int sigIntAndTerm = (1 << (2 - 1)) | (1 << (15 - 1));
        Assert.Equal(sigIntAndTerm, int.Parse(Out(r, 2)) & sigIntAndTerm); // PID 1 ignores SIGINT and SIGTERM
    }

    // Unlike Python, the .NET runtime installs its own handlers for SIGINT/SIGTERM, so /proc/1/status SigCgt/SigIgn is identical with and without
    // the PosixSignalRegistration. The kill itself is meaningful here: without the registration the runner exits 130 (checked with a throwaway image).
    [Fact] public async Task CSharpCaseCannotSignalTheRunner()
    {
        var r = await Cs("""
            using System.Diagnostics;
            public static class S { public static int F(int x) {
                if (x == 0) foreach (var s in new[] { "INT", "TERM" }) Process.Start("kill", new[] { "-" + s, "1" })!.WaitForExit();
                return x; } }
            """, "S.F", 10_000, "[0]", "[1]");
        Assert.Equal(0, r.ExitCode);
        Assert.Equal("0", Out(r, 0));
        Assert.Equal("1", Out(r, 1));
    }

    [Fact] public async Task RepetitionsMultiplyThePerCaseDeadline()
    {
        // 3 x 0.7 s of work exceeds the 1 s per-call timeout but not the 3 s case deadline; a hang fails alone at the full 3 s
        var r = await Run(SandboxLanguage.Python, """
            import time
            def f(x):
                while x:
                    pass
                time.sleep(0.7)
                return x
            """, "f", 1000, SandboxAction.Run, 3, "[0]", "[1]");
        Assert.False(r.TimedOut);
        Assert.Equal("0", Out(r, 0));
        Assert.Equal("case timed out after 3000 ms", r.Cases[1].Error);
    }

    [Fact] public async Task EveryCSharpCaseHangingFailsPerCaseNotTheWholeRequest()
    {
        var r = await Cs("public static class S { public static int F(int x) { while (true) { } } }", "S.F", 1000, "[1]", "[2]", "[3]", "[4]", "[5]", "[6]");
        Assert.False(r.TimedOut);
        Assert.Equal(6, r.Cases.Count);
        Assert.All(r.Cases, c => Assert.Equal("case timed out after 1000 ms", c.Error));
    }

    [Fact] public void RunnerOutputBudgetsFitUnderTheHostOutputCap()
    {
        var root = Path.GetDirectoryName(SandboxImages.FindSandboxDir())!;
        var py = int.Parse(Regex.Match(File.ReadAllText(Path.Combine(root, "sandbox", "python", "runner.py")), @"^OUTPUT_BUDGET = ([\d_]+)", RegexOptions.Multiline).Groups[1].Value.Replace("_", ""));
        var cs = int.Parse(Regex.Match(File.ReadAllText(Path.Combine(root, "sandbox", "dotnet", "Runner", "Program.cs")), @"const int OutputBudget = ([\d_]+);").Groups[1].Value.Replace("_", ""));
        Assert.Equal(py, cs);
        Assert.True(DockerSandbox.FrameBytes(py) < DockerSandbox.OutputCap, "the base64 frame for a full budget must fit under the host's output cap");
    }

    [Fact] public async Task AnOversizedOutputFailsAloneAndTheFrameStillFits()
    {
        var r = await Py("def f(x):\n    return 'a' * 700_000 if x == 0 else 'b' * 100_000 if x == 1 else x\n", "f", 10_000, "[0]", "[1]", "[2]");
        Assert.Equal(0, r.ExitCode);
        Assert.Equal(700_002, Out(r, 0).Length);
        Assert.Equal("output budget exceeded", r.Cases[1].Error);
        Assert.Equal("2", Out(r, 2));
    }

    [Fact] public async Task CSharpOversizedOutputFailsAloneAndTheFrameStillFits()
    {
        var r = await Cs("public static class S { public static object F(int x) => x == 0 ? new string('a', 700_000) : x == 1 ? new string('b', 100_000) : x; }", "S.F", 10_000, "[0]", "[1]", "[2]");
        Assert.Equal(0, r.ExitCode);
        Assert.Equal(700_002, Out(r, 0).Length);
        Assert.Equal("output budget exceeded", r.Cases[1].Error);
        Assert.Equal("2", Out(r, 2));
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
