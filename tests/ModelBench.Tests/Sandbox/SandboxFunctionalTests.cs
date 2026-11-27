using System.Diagnostics;
using ModelBench.Core.Sandbox;
using static ModelBench.Tests.Sandbox.SandboxEnv;

namespace ModelBench.Tests.Sandbox;

public class SandboxFunctionalTests
{
    [Fact]
    public async Task Python_RunsSeveralCases()
    {
        var r = await Py("def f(a, b):\n    return a + b\n", "f", 10_000, "[1,2]", "[10,-4]", "[0,0]");
        Assert.Equal(["3", "6", "0"], r.Cases.Select(c => c.Output!.Value.GetRawText()));
        Assert.Equal([0, 1, 2], r.Cases.Select(c => c.Index));
    }

    [Fact]
    public async Task Python_IsolatesCaseExceptions()
    {
        var r = await Py("def f(x):\n    return 10 // x\n", "f", 10_000, "[5]", "[0]", "[2]");
        Assert.Equal("2", r.Cases[0].Output!.Value.GetRawText());
        Assert.Contains("ZeroDivisionError", r.Cases[1].Error);
        Assert.Equal("5", r.Cases[2].Output!.Value.GetRawText());
    }

    [Fact]
    public async Task Python_ConvertsJsonValues()
    {
        var r = await Py("def f(a, s, n, d):\n    return [a, s, n, d, (1, 2), {3, 1, 2}]\n", "f", 10_000,
            """[[1,2],"hi",[[1],[2,3]],{"k":[true,null]}]""");
        Assert.Equal("""[[1,2],"hi",[[1],[2,3]],{"k":[true,null]},[1,2],[1,2,3]]""", Out(r));
    }

    [Fact]
    public async Task Python_UnserialisableOutputIsAnError()
    {
        var r = await Py("def f():\n    return object()\n");
        Assert.Contains("unserialisable output: object", r.Cases[0].Error);
    }

    [Fact]
    public async Task Python_ReportsCompileErrorWithLine()
    {
        var r = await Run(SandboxLanguage.Python, "x = 1\ndef f(:\n    pass\n", "f", action: SandboxAction.Compile);
        Assert.False(r.Compiled);
        Assert.StartsWith("line 2", r.CompileErrors);
    }

    [Fact]
    public async Task Python_InspectsFacts()
    {
        var code = "import os\nfrom collections import deque\ndef fact(n):\n    return 1 if n < 2 else n * fact(n - 1)\ndef g(xs):\n    for x in xs:\n        print(x)\nclass A: pass\n";
        var r = await Run(SandboxLanguage.Python, code, "fact", action: SandboxAction.Inspect);
        var f = r.AstFacts!.Value;
        Assert.Equal(["collections", "os"], f.GetProperty("imports").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["fact", "g"], f.GetProperty("functions").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["A"], f.GetProperty("classes").EnumerateArray().Select(e => e.GetString()));
        Assert.True(f.GetProperty("hasLoop").GetBoolean());
        Assert.Equal(["fact"], f.GetProperty("recursiveFunctions").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("print", f.GetProperty("calledNames").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Python_TimesOutOnInfiniteLoop()
    {
        var sw = Stopwatch.StartNew();
        var r = await Py("def f(x):\n    while x:\n        pass\n    return x\n", "f", 1000, "[1]", "[0]");
        Assert.False(r.TimedOut);
        Assert.Equal("case timed out after 1000 ms", r.Cases[0].Error);
        Assert.Equal("0", Out(r, 1)); // one hung case fails alone
        Assert.InRange(sw.Elapsed.TotalSeconds, 0, 20);
    }

    [Fact]
    public async Task CSharp_RunsSeveralCasesWithConversions()
    {
        var code = """
            using System.Collections.Generic;
            namespace Demo { public static class S {
              public static int Add(int a, int b) => a + b;
              public static List<List<int>> Nest(int[][] x) => x.Select(r => r.Reverse().ToList()).ToList();
              public static Dictionary<string, int> Count(string s) => s.GroupBy(c => c.ToString()).ToDictionary(g => g.Key, g => g.Count());
            } }
            """;
        Assert.Equal("5", Out(await Cs(code, "Demo.S.Add", 10_000, "[2,3]")));
        Assert.Equal("[[2,1],[4,3]]", Out(await Cs(code, "Demo.S.Nest", 10_000, "[[[1,2],[3,4]]]")));
        Assert.Equal("""{"a":2,"b":1}""", Out(await Cs(code, "Demo.S.Count", 10_000, """["aba"]""")));
    }

    [Fact]
    public async Task CSharp_IsolatesCaseExceptions()
    {
        var r = await Cs("public static class S { public static int F(int x) => 10 / x; }", "S.F", 10_000, "[5]", "[0]", "[2]");
        Assert.Equal("2", r.Cases[0].Output!.Value.GetRawText());
        Assert.Contains("DivideByZeroException", r.Cases[1].Error);
        Assert.Equal("5", r.Cases[2].Output!.Value.GetRawText());
    }

    [Fact]
    public async Task CSharp_ReportsCompileErrorWithLine()
    {
        var r = await Run(SandboxLanguage.CSharp, "public static class S {\n  public static int F() { return \"x\"; }\n}\n", "S.F", action: SandboxAction.Compile);
        Assert.False(r.Compiled);
        Assert.StartsWith("line 2", r.CompileErrors);
    }

    [Fact]
    public async Task CSharp_CapsCompileErrorsAt500Chars()
    {
        var body = string.Concat(Enumerable.Range(0, 100).Select(i => $"  public static int F{i}() {{ return \"x\"; }}\n"));
        var r = await Run(SandboxLanguage.CSharp, $"public static class S {{\n{body}}}\n", "S.F0", action: SandboxAction.Compile);
        Assert.False(r.Compiled);
        Assert.Equal(500, r.CompileErrors!.Length);
    }

    [Fact]
    public async Task CSharp_InspectsFacts()
    {
        var code = "using System.Text;\npublic static class S {\n public static int Fact(int n) { return n < 2 ? 1 : n * Fact(n - 1); }\n public static void G(int[] a) { foreach (var x in a) Console.WriteLine(x); }\n}\n";
        var r = await Run(SandboxLanguage.CSharp, code, "S.Fact", action: SandboxAction.Inspect);
        var f = r.AstFacts!.Value;
        Assert.Equal(["System.Text"], f.GetProperty("imports").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["Fact", "G"], f.GetProperty("functions").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["S"], f.GetProperty("classes").EnumerateArray().Select(e => e.GetString()));
        Assert.True(f.GetProperty("hasLoop").GetBoolean());
        Assert.Equal(["Fact"], f.GetProperty("recursiveFunctions").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("WriteLine", f.GetProperty("calledNames").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task CSharp_TimesOutOnInfiniteLoop()
    {
        var r = await Cs("public static class S { public static int F(int x) { while (x != 0) { } return x; } }", "S.F", 1000, "[1]", "[0]");
        Assert.False(r.TimedOut);
        Assert.Equal("case timed out after 1000 ms", r.Cases[0].Error);
        Assert.Equal("0", Out(r, 1));
    }

    [Fact]
    public void WallClockFormula()
    {
        var one = new SandboxRequest(SandboxLanguage.Python, SandboxAction.Run, "", "f", [], 1000, 1);
        Assert.Equal(6100, DockerSandbox.WallClockMs(one)); // 1 s + 5 s + 100 ms per call
        var many = one with { Inputs = new[] { default(System.Text.Json.JsonElement), default, default }, Repetitions = 2 };
        Assert.Equal(1000 * 6 + 5000 + 100 * 6, DockerSandbox.WallClockMs(many));
        Assert.Equal(6100, DockerSandbox.WallClockMs(many with { Action = SandboxAction.Compile }));
    }
}
