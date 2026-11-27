using System.Text.Json;
using ModelBench.Core.Domain;
using ModelBench.Core.Grading;
using ModelBench.Core.Sandbox;
using ModelBench.Tests.Sandbox;
using ModelBench.Tests.Support;

namespace ModelBench.Tests.Grading;

public class CodeGraderTests
{
    private static GradingContext Ctx(string? answer, ISandbox sandbox)
        => new(TaskPrompt.Simple("q", AnswerFormat.CodeBlock), new TaskHidden([]), answer, answer, [], null, sandbox, default);

    private static async Task<IReadOnlyList<AssertionOutcome>> Grade(string type, object config, string? answer, ISandbox sandbox)
        => await GraderRegistry.Get(type).GradeAsync(new GraderSpec(type, "g", JsonUtil.Element(config)), Ctx(answer, sandbox));

    private static object Cases(params (object[] args, object expected)[] cs) => cs.Select(c => new { args = c.args, expected = c.expected }).ToArray();

    private const string Double = "def f(x):\n    return x * 2\n";

    // ---------------------------------------------------------------- compile

    [Fact] public async Task CompilePassesAndFailsForBothLanguages()
    {
        var sb = await SandboxEnv.Require();
        var py = new { language = "python", entryPoint = "f" };
        var cs = new { language = "csharp", entryPoint = "S.F" };
        Assert.True((await Grade("compile", py, Double, sb)).Single().Passed);
        var bad = (await Grade("compile", py, "def f(:\n", sb)).Single();
        Assert.False(bad.Passed);
        Assert.Contains("line 1", bad.Actual);
        Assert.True((await Grade("compile", cs, "public static class S { public static int F() => 1; }", sb)).Single().Passed);
        var csBad = (await Grade("compile", cs, "public static class S { public static int F() => ; }", sb)).Single();
        Assert.False(csBad.Passed);
        Assert.Contains("CS", csBad.Actual);
    }

    [Fact] public async Task CompileNoAnswerTimeoutAndCrash()
    {
        var cfg = new { language = "python", entryPoint = "f" };
        var o = (await Grade("compile", cfg, null, new FakeSandbox(_ => throw new InvalidOperationException("must not run")))).Single();
        Assert.False(o.Passed); Assert.Equal("no answer", o.Diagnostic);
        Assert.Equal("timed out", (await Grade("compile", cfg, "x", new FakeSandbox(_ => Task.FromResult(FakeSandbox.TimedOut)))).Single().Diagnostic);
        Assert.Contains("crashed", (await Grade("compile", cfg, "x", new FakeSandbox(_ => Task.FromResult(FakeSandbox.Crashed)))).Single().Diagnostic);
    }

    [Fact] public async Task InfraFailuresPropagate()
        => await Assert.ThrowsAsync<InfraException>(() => Grade("compile", new { language = "python", entryPoint = "f" }, "x",
            new FakeSandbox(_ => throw new InfraException("docker down", false))));

    // ---------------------------------------------------------------- unit-tests / differential

    [Fact] public async Task UnitTestsPassAndFailPerCase()
    {
        var sb = await SandboxEnv.Require();
        var cfg = new { language = "python", entryPoint = "f", cases = Cases(([1], 2), ([2], 5), ([3], 6)) };
        var r = await Grade("unit-tests", cfg, Double, sb);
        Assert.Equal(["case[0]", "case[1]", "case[2]"], r.Select(o => o.Name));
        Assert.Equal([true, false, true], r.Select(o => o.Passed));
        Assert.Equal("4", r[1].Actual);
        Assert.Equal("5", r[1].Expected);
    }

    [Fact] public async Task CandidateExceptionFailsOnlyThatCase()
    {
        var sb = await SandboxEnv.Require();
        var cfg = new { language = "python", entryPoint = "f", cases = Cases(([1], 1), ([0], 0)) };
        var r = await Grade("unit-tests", cfg, "def f(x):\n    return 1 // x\n", sb);
        Assert.True(r[0].Passed);
        Assert.False(r[1].Passed);
        Assert.Contains("ZeroDivisionError", r[1].Actual);
    }

    [Fact] public async Task ToleranceAppliesRecursivelyToNumbers()
    {
        var sb = await SandboxEnv.Require();
        const string code = "def f(x):\n    return {'a': [0.1 + 0.2, 1.0], 'b': 'x'}\n";
        var exact = new { language = "python", entryPoint = "f", cases = Cases(([1], new { a = new[] { 0.3, 1.0 }, b = "x" })) };
        var tol = new { language = "python", entryPoint = "f", tolerance = 1e-9, cases = Cases(([1], new { a = new[] { 0.3, 1.0 }, b = "x" })) };
        var wrongString = new { language = "python", entryPoint = "f", tolerance = 1e-9, cases = Cases(([1], new { a = new[] { 0.3, 1.0 }, b = "y" })) };
        Assert.False((await Grade("unit-tests", exact, code, sb)).Single().Passed);
        Assert.True((await Grade("unit-tests", tol, code, sb)).Single().Passed);
        Assert.False((await Grade("unit-tests", wrongString, code, sb)).Single().Passed);
    }

    [Fact] public async Task CSharpUnitTestsWork()
    {
        var sb = await SandboxEnv.Require();
        var cfg = new { language = "csharp", entryPoint = "S.F", cases = Cases((new object[] { new[] { 3, 1, 2 } }, new[] { 1, 2, 3 }), (new object[] { new[] { 2, 1 } }, new[] { 9 })) };
        var r = await Grade("unit-tests", cfg, "public static class S { public static int[] F(int[] a) => a.Order().ToArray(); }", sb);
        Assert.Equal([true, false], r.Select(o => o.Passed));
    }

    [Fact] public async Task DifferentialUsesRandomNames()
    {
        var sb = await SandboxEnv.Require();
        var cfg = new { language = "python", entryPoint = "f", cases = Cases(([1], 2), ([2], 4)) };
        var r = await Grade("differential", cfg, Double, sb);
        Assert.Equal(["random[0]", "random[1]"], r.Select(o => o.Name));
        Assert.All(r, o => Assert.True(o.Passed));
    }

    [Theory]
    [InlineData("unit-tests", "case")]
    [InlineData("differential", "random")]
    public async Task TimeoutCrashNoAnswerAndCompileErrorFailEveryCase(string type, string prefix)
    {
        var cfg = new { language = "python", entryPoint = "f", cases = Cases(([1], 2), ([2], 4)) };
        var sandbox = new FakeSandbox(_ => Task.FromResult(FakeSandbox.TimedOut));
        var timeout = await Grade(type, cfg, Double, sandbox);
        Assert.Equal([$"{prefix}[0]", $"{prefix}[1]"], timeout.Select(o => o.Name));
        Assert.All(timeout, o => { Assert.False(o.Passed); Assert.Equal("timed out", o.Diagnostic); });

        var crash = await Grade(type, cfg, Double, new FakeSandbox(_ => Task.FromResult(FakeSandbox.Crashed)));
        Assert.All(crash, o => Assert.Contains("crashed: Traceback: boom", o.Diagnostic));

        var noCases = await Grade(type, cfg, Double, new FakeSandbox(_ => Task.FromResult(new SandboxResult(true, null, [], null, false, 0, "oops"))));
        Assert.All(noCases, o => Assert.Contains("crashed: oops", o.Diagnostic));

        var compile = await Grade(type, cfg, "x", new FakeSandbox(_ => Task.FromResult(new SandboxResult(false, "line 1: bad", [], null, false, 0, ""))));
        Assert.All(compile, o => Assert.Contains("line 1: bad", o.Diagnostic));

        var none = await Grade(type, cfg, null, new FakeSandbox(_ => throw new InvalidOperationException("must not run")));
        Assert.Equal(2, none.Count);
        Assert.All(none, o => { Assert.False(o.Passed); Assert.Equal("no answer", o.Diagnostic); });
    }

    [Fact] public async Task RealCrashAndRealTimeoutAreModelFailures()
    {
        var sb = await SandboxEnv.Require();
        var cfg = new { language = "python", entryPoint = "f", cases = Cases(([1], 2)) };
        var crash = await Grade("unit-tests", cfg, "import os\nos._exit(3)\ndef f(x):\n    return x\n", sb);
        Assert.False(crash.Single().Passed);
        Assert.Contains("case process exited with status 3", crash.Single().Diagnostic); // module code runs in each case's process
        var hang = await Grade("unit-tests", cfg, "def f(x):\n    while True:\n        pass\n", sb);
        Assert.Equal("error: case timed out after 2000 ms", hang.Single().Diagnostic); // the runner kills the case; the host backstop (TimedOut) stays unused
    }

    [Fact] public async Task ExpectedValuesNeverReachTheSandbox()
    {
        const int marker = 987654321;
        foreach (var type in new[] { "unit-tests", "differential", "performance" })
        {
            var sandbox = new FakeSandbox(_ => Task.FromResult(new SandboxResult(true, null, [new CaseResult(0, JsonDocument.Parse("1").RootElement, null, 0)], null, false, 0, "")));
            object cfg = type == "performance"
                ? new { language = "python", entryPoint = "f", args = new[] { 41 }, expected = marker, budgetMs = 1000 }
                : new { language = "python", entryPoint = "f", cases = Cases(([41], marker)) };
            await Grade(type, cfg, Double, sandbox);
            var req = Assert.Single(sandbox.Requests);
            var json = DockerSandbox.BuildRequestJson(req, "nonce");
            Assert.DoesNotContain(marker.ToString(), json);
            Assert.Contains("[41]", json);
        }
    }

    // ---------------------------------------------------------------- performance

    private static object Perf(int budget, object? expected = null) => new { language = "python", entryPoint = "f", args = new[] { 21 }, expected = expected ?? 42, budgetMs = budget };

    private static Task<SandboxResult> Slow(int delayMs, int output = 42) => Task.Delay(delayMs).ContinueWith(_ =>
        new SandboxResult(true, null, [new CaseResult(0, JsonDocument.Parse(output.ToString()).RootElement, null, 0.5)], null, false, 0, ""));

    [Fact] public async Task PerformancePassesWhenCorrectAndInsideTheHostBudget()
    {
        var sandbox = new FakeSandbox(_ => Slow(10));
        var r = await Grade("performance", Perf(2000), Double, sandbox);
        Assert.Equal(["correct", "within-budget"], r.Select(o => o.Name));
        Assert.All(r, o => Assert.True(o.Passed));
        Assert.Equal(2000, sandbox.Requests.Single().TimeoutMs);
        Assert.Single(sandbox.Requests.Single().Inputs);
    }

    [Fact] public async Task FinishingInsideTheKillGraceButOverBudgetIsCorrectYetOverBudget()
    {
        // DockerSandbox only kills at budget + 5 s, so the grader must measure host wall time itself.
        var r = await Grade("performance", Perf(100), Double, new FakeSandbox(_ => Slow(400)));
        Assert.True(r[0].Passed);
        Assert.False(r[1].Passed);
        Assert.Contains("exceeds the 100 ms budget", r[1].Diagnostic);
        Assert.Contains("informational", r[1].Diagnostic);
    }

    [Fact] public async Task PerformanceCaseKilledByTheRunnerCountsAsKilledAtBudget()
    {
        var r = await Grade("performance", Perf(1000), Double, new FakeSandbox(_ => Task.FromResult(
            new SandboxResult(true, null, [new CaseResult(0, null, "case timed out after 1000 ms", 0)], null, false, 0, ""))));
        Assert.Equal("killed at budget", r[1].Actual);
        Assert.All(r, o => Assert.False(o.Passed));
    }

    [Fact] public async Task PerformanceTimeoutFailsBothAssertionsAndWrongOutputEarnsNoPerformanceCredit()
    {
        var killed = await Grade("performance", Perf(1000), Double, new FakeSandbox(_ => Task.FromResult(FakeSandbox.TimedOut)));
        Assert.All(killed, o => Assert.False(o.Passed));
        Assert.Equal("timed out", killed[0].Diagnostic);
        // MB-003: fast but wrong, or a fast compile failure, gets no performance credit; only fast AND correct does.
        var wrong = await Grade("performance", Perf(2000), Double, new FakeSandbox(_ => Slow(1, 7)));
        Assert.Equal((false, false, 0.0), (wrong[0].Passed, wrong[1].Passed, wrong[1].Score));
        Assert.Equal("incorrect", wrong[1].Actual);
        var noCompile = await Grade("performance", Perf(2000), Double, new FakeSandbox(_ => Task.FromResult(new SandboxResult(false, "line 1: invalid syntax", [], null, false, 0, ""))));
        Assert.Equal((false, false), (noCompile[0].Passed, noCompile[1].Passed));
        var right = await Grade("performance", Perf(2000), Double, new FakeSandbox(_ => Slow(1)));
        Assert.Equal((true, true, 1.0), (right[0].Passed, right[1].Passed, right[1].Score));
        var none = await Grade("performance", Perf(2000), null, new FakeSandbox(_ => throw new InvalidOperationException()));
        Assert.All(none, o => Assert.Equal("no answer", o.Diagnostic));
    }

    [Fact] public async Task MalformedCaseIndicesAreUnusableNeverAPass()
    {
        // MB-004: the host validates the result protocol instead of trusting the frame's shape.
        static SandboxResult Indices(params int[] idx) => new(true, null, [.. idx.Select(i => new CaseResult(i, JsonDocument.Parse("2").RootElement, null, 0))], null, false, 0, "");
        var cfg = new { language = "python", entryPoint = "f", cases = Cases(([1], 2), ([1], 2)) };
        foreach (var bad in new[] { Indices(1, 0), Indices(0, 0), Indices(0, 5) })
        {
            var r = await Grade("unit-tests", cfg, Double, new FakeSandbox(_ => Task.FromResult(bad)));
            Assert.All(r, o => Assert.False(o.Passed));
            Assert.Contains("malformed result", r[0].Diagnostic);
        }
        Assert.All(await Grade("unit-tests", cfg, Double, new FakeSandbox(_ => Task.FromResult(Indices(0, 1)))), o => Assert.True(o.Passed));
    }

    [Theory]
    [InlineData("9007199254740993", "9007199254740992", 0, false)] // adjacent integers above 2^53: equal as doubles
    [InlineData("123456789012345678901234567890", "123456789012345678901234567891", 0, false)] // beyond decimal: BigInteger
    [InlineData("1", "1.0", 0, true)]
    [InlineData("100", "1e2", 0, true)]
    [InlineData("0.1", "0.10", 0, true)]
    [InlineData("[1,{\"k\":9007199254740993}]", "[1,{\"k\":9007199254740992}]", 0, false)] // nested
    [InlineData("1e-29", "0", 0, false)] // decimal would round both to zero
    [InlineData("1e400", "2e400", 0, false)] // double would make both infinity
    [InlineData("1e2", "100", 0, true)]
    [InlineData("100", "100.0", 0, true)]
    [InlineData("1.00e2", "100", 0, true)]
    [InlineData("-0", "0", 0, true)]
    [InlineData("-0.0e5", "0", 0, true)]
    [InlineData("1e400", "10e399", 0, true)] // huge exponents are compared, never expanded
    [InlineData("1e999999999999", "1e999999999998", 0, false)]
    [InlineData("1.000000000000000000000000000001", "1.000000000000000000000000000002", 0, false)] // last digit beyond decimal
    [InlineData("-1", "1", 0, false)]
    [InlineData("1.0", "1.05", 0.1, true)]
    [InlineData("1.0", "1.5", 0.1, false)]
    public async Task ExactNumericGrading(string expected, string actual, double tolerance, bool equal)
    {
        // MB-007: zero tolerance is exact, never via double.
        var cfg = new { language = "python", entryPoint = "f", cases = new[] { new { args = new[] { 1 }, expected = JsonDocument.Parse(expected).RootElement } }, tolerance };
        var r = await Grade("unit-tests", cfg, Double, new FakeSandbox(_ => Task.FromResult(
            new SandboxResult(true, null, [new CaseResult(0, JsonDocument.Parse(actual).RootElement, null, 0)], null, false, 0, ""))));
        Assert.Equal(equal, r.Single().Passed);
    }

    [Fact] public async Task RealContainerPerformance()
    {
        var sb = await SandboxEnv.Require();
        var fast = await Grade("performance", Perf(10_000), Double, sb);
        Assert.All(fast, o => Assert.True(o.Passed, o.Diagnostic));
        // A budget that cannot include container start-up: never within budget.
        var tight = await Grade("performance", Perf(1), Double, sb);
        Assert.False(tight[1].Passed); // the runner kills the case at 1 ms: killed at budget
        Assert.False(tight[1].Passed);
        var hang = await Grade("performance", Perf(500), "def f(x):\n    while True:\n        pass\n", sb);
        Assert.All(hang, o => Assert.False(o.Passed));
        Assert.Contains("killed", hang[1].Diagnostic);
    }

    // ---------------------------------------------------------------- ast

    private static object Rules(string lang, params object[] rules) => new { language = lang, rules };

    [Fact] public async Task AstRulesPassAndFailOnPython()
    {
        var sb = await SandboxEnv.Require();
        const string code = "import heapq\ndef f(n):\n    if n == 0:\n        return 0\n    return f(n - 1) + sorted([n])[0]\n";
        var r = await Grade("ast", Rules("python",
            new { kind = "defines", name = "f" }, new { kind = "defines", name = "g" },
            new { kind = "forbids-import", name = "heapq" }, new { kind = "forbids-import", name = "re" },
            new { kind = "forbids-call", name = "sorted" }, new { kind = "forbids-call", name = "max" },
            new { kind = "requires-loop" }, new { kind = "forbids-recursion" }), code, sb);
        Assert.Equal(["defines:f", "defines:g", "forbids-import:heapq", "forbids-import:re", "forbids-call:sorted", "forbids-call:max", "requires-loop", "forbids-recursion"], r.Select(o => o.Name));
        Assert.Equal([true, false, false, true, false, true, false, false], r.Select(o => o.Passed));
        var loop = await Grade("ast", Rules("python", new { kind = "requires-loop" }, new { kind = "forbids-recursion" }), "def f(n):\n    for i in range(n):\n        pass\n", sb);
        Assert.All(loop, o => Assert.True(o.Passed));
    }

    [Fact] public async Task AstRulesOnCSharpAndUnparsableCode()
    {
        var sb = await SandboxEnv.Require();
        var r = await Grade("ast", Rules("csharp", new { kind = "defines", name = "F" }, new { kind = "forbids-import", name = "System.Linq" }, new { kind = "requires-loop" }),
            "using System.Linq;\npublic static class S { public static int F(int[] a) { var t = 0; foreach (var x in a) t += x; return t; } }", sb);
        Assert.Equal([true, false, true], r.Select(o => o.Passed));
        var broken = await Grade("ast", Rules("python", new { kind = "requires-loop" }), "def f(:", sb);
        Assert.False(broken.Single().Passed);
        Assert.Contains("could not be parsed", broken.Single().Diagnostic);
    }

    [Fact] public async Task AstNoAnswerFailsEveryRule()
    {
        var r = await Grade("ast", Rules("python", new { kind = "requires-loop" }, new { kind = "defines", name = "f" }), null, new FakeSandbox(_ => throw new InvalidOperationException()));
        Assert.Equal(2, r.Count);
        Assert.All(r, o => Assert.Equal("no answer", o.Diagnostic));
    }
}
