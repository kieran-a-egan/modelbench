using ModelBench.Core.Domain;
using ModelBench.Core.Grading;
using ModelBench.Core.Sandbox;

namespace ModelBench.Tests.Grading;

public class ToolCallsGraderTests
{
    private static async Task<IReadOnlyList<AssertionOutcome>> Run(object config, params (string Name, string Args)[] calls)
    {
        var transcript = calls.Select(c => new ChatMessage(Role.Assistant, null, [new ToolCall("id", c.Name, c.Args)])).ToList();
        var ctx = new GradingContext(TaskPrompt.Simple("q"), new TaskHidden([]), null, null, transcript, null, new UnavailableSandbox(), default);
        return await GraderRegistry.Get("tool-calls").GradeAsync(new GraderSpec("tool-calls", "g", JsonUtil.Element(config)), ctx);
    }

    private static string[] Passed(IEnumerable<AssertionOutcome> os) => os.Where(o => o.Passed).Select(o => o.Name).ToArray();

    [Fact] public async Task RequiredCallsMatchByNameAndArgumentSubset()
    {
        var os = await Run(new { required = new object[] { new { name = "a", args = new { x = 1, s = "Q" } }, new { name = "b" } } },
            ("a", """{"x":1.0,"s":"Q","extra":true}"""), ("b", "{}"));
        Assert.Equal(["called[0]:a", "called[1]:b"], Passed(os));
    }

    [Theory]
    [InlineData("""{"x":2,"s":"Q"}""")]     // wrong number
    [InlineData("""{"x":1,"s":"q"}""")]     // strings are exact
    [InlineData("""{"x":"1","s":"Q"}""")]   // type matters
    [InlineData("""{"s":"Q"}""")]           // missing property
    [InlineData("""not json""")]            // unparsable
    public async Task MismatchedArgumentsFail(string args)
    {
        var o = Assert.Single(await Run(new { required = new[] { new { name = "a", args = new { x = 1, s = "Q" } } } }, ("a", args)));
        Assert.False(o.Passed);
        Assert.Contains("no 'a' call had arguments matching", o.Diagnostic);
    }

    [Fact] public async Task UnparsableArgumentsAreDiagnosed()
    {
        var o = Assert.Single(await Run(new { required = new[] { new { name = "a", args = new { x = 1 } } } }, ("a", "{oops")));
        Assert.Contains("unparsable", o.Diagnostic);
    }

    [Fact] public async Task NeverCalledFailsAndNestedValuesAreDeepEqual()
    {
        var os = await Run(new { required = new object[] { new { name = "a" }, new { name = "b", args = new { o = new { k = new[] { 1, 2 } } } } } },
            ("b", """{"o":{"k":[1,2]}}"""));
        Assert.Equal(["called[1]:b"], Passed(os));
        Assert.Contains("never called", os[0].Diagnostic);
        Assert.Empty(Passed(await Run(new { required = new[] { new { name = "b", args = new { o = new { k = new[] { 1, 2 } } } } } }, ("b", """{"o":{"k":[2,1]}}"""))));
    }

    [Theory]
    [InlineData("a,b", true)]
    [InlineData("a,x,b", true)]
    [InlineData("b,a", false)]
    [InlineData("a", false)]
    public async Task OrderedChecksTheSequence(string names, bool ok)
    {
        var os = await Run(new { required = new[] { new { name = "a" }, new { name = "b" } }, ordered = true }, names.Split(',').Select(n => (n, "{}")).ToArray());
        Assert.Equal(ok, os.Single(o => o.Name == "order").Passed);
    }

    [Fact] public async Task ForbiddenAndMaxCalls()
    {
        var os = await Run(new { required = Array.Empty<object>(), forbidden = new[] { "rm", "ls" }, maxCalls = 2 }, ("ls", "{}"), ("a", "{}"), ("a", "{}"));
        Assert.Equal(["forbidden:rm"], Passed(os));
        Assert.Equal(["forbidden:rm", "forbidden:ls", "max-calls"], os.Select(o => o.Name));
        Assert.False(os.Single(o => o.Name == "max-calls").Passed);
        Assert.True((await Run(new { required = Array.Empty<object>(), maxCalls = 3 }, ("a", "{}"), ("a", "{}"), ("a", "{}"))).Single().Passed);
    }

    [Fact] public async Task NoCallsFailsEveryRequirement()
        => Assert.Empty(Passed(await Run(new { required = new[] { new { name = "a" } } })));
}
