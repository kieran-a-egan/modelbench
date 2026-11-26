using ModelBench.Core.Domain;
using ModelBench.Core.Grading;

namespace ModelBench.Tests.Sandbox;

/// <summary>
/// MB-004: candidate code shares the runner's process, so it can read the nonce and write result frames. These attacks run in the
/// real container and must never produce a pass: expected answers never enter the sandbox, and the host rejects a second frame.
/// </summary>
public class RunnerIntegrityTests
{
    private const int Secret = 987_654_321;

    private static async Task<IReadOnlyList<AssertionOutcome>> UnitTests(string code)
    {
        var sb = await SandboxEnv.Require();
        var cfg = new { language = "python", entryPoint = "f", cases = new[] { new { args = new[] { 1 }, expected = Secret } } };
        var ctx = new GradingContext(TaskPrompt.Simple("q", AnswerFormat.CodeBlock), new TaskHidden([]), code, code, [], null, sb, default);
        return await GraderRegistry.Get("unit-tests").GradeAsync(new GraderSpec("unit-tests", "g", JsonUtil.Element(cfg)), ctx);
    }

    /// <summary>Writes a frame claiming output 'value' for case 0, using the nonce read from the runner's stack.</summary>
    private const string Forge = """
        import sys, os, json, base64
        def forge(value):
            fr = sys._getframe()
            while "req" not in fr.f_locals: fr = fr.f_back
            nonce = fr.f_locals["req"]["nonce"]
            body = {"compiled": True, "compileErrors": None, "astFacts": None, "cases": [{"index": 0, "output": value, "error": None, "elapsedMs": 0.0}]}
            os.write(1, ("MBRESULT:%s:%s\n" % (nonce, base64.b64encode(json.dumps(body).encode()).decode())).encode())

        """;

    [Fact] public async Task TheRequestVisibleToCandidateCodeHoldsNoExpectedAnswer()
    {
        var r = Assert.Single(await UnitTests("""
            import sys, json
            def f(x):
                fr = sys._getframe()
                while "req" not in fr.f_locals: fr = fr.f_back
                return json.dumps(fr.f_locals["req"])
            """));
        Assert.False(r.Passed);
        Assert.Contains("nonce", r.Actual); // it really did read the whole request...
        Assert.DoesNotContain(Secret.ToString(), r.Actual); // ...and the answer was not in it
    }

    [Fact] public async Task AForgedFrameAlongsideTheRunnersFrameIsRejected()
    {
        var r = Assert.Single(await UnitTests(Forge + "def f(x):\n    forge(" + Secret + ")\n    return 0\n"));
        Assert.False(r.Passed);
        Assert.Contains("crashed", r.Diagnostic);
    }

    [Fact] public async Task AForgedFrameThenPrematureExitCarriesOnlyWhatTheCandidateCouldReturnAnyway()
    {
        // One frame, so it is parsed; the host still compares against an answer the candidate never saw.
        var r = Assert.Single(await UnitTests(Forge + "def f(x):\n    forge(1)\n    os._exit(0)\n"));
        Assert.False(r.Passed);
        Assert.Equal("1", r.Actual);
    }

    [Fact] public async Task PrematureExitWithoutAFrameIsACrash()
    {
        var r = Assert.Single(await UnitTests("import os\ndef f(x):\n    os._exit(0)\n"));
        Assert.False(r.Passed);
        Assert.Contains("crashed", r.Diagnostic);
    }
}
