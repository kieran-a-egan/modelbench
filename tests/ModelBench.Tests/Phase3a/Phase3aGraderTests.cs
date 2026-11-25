using ModelBench.Core.Domain;
using ModelBench.Core.Grading;
using ModelBench.Core.Sandbox;

namespace ModelBench.Tests.Phase3a;

public class TextConstraintsGraderTests
{
    private static async Task<IReadOnlyList<AssertionOutcome>> Run(object[] constraints, string? answer)
    {
        var ctx = new GradingContext(TaskPrompt.Simple("q"), new TaskHidden([]), answer, answer, [], null, new UnavailableSandbox(), default);
        return await GraderRegistry.Get("text-constraints").GradeAsync(new GraderSpec("text-constraints", "g", JsonUtil.Element(new { constraints })), ctx);
    }

    public static TheoryData<object, string, string> Cases => new()
    {
        { new { kind = "word-count", min = 3, max = 5 }, "one two three", "one two" },
        { new { kind = "word-count", min = 3, max = 5 }, "a b c d e", "a b c d e f" },
        { new { kind = "max-words", max = 2 }, "a  b", "a b c" },
        { new { kind = "contains", text = "Cat" }, "a Cat", "a cat" },
        { new { kind = "contains", text = "Cat", caseSensitive = false }, "a cat", "a dog" },
        { new { kind = "not-contains", text = "dog" }, "a cat", "a dog" },
        { new { kind = "regex", pattern = @"\d{3}" }, "x 123", "x 12" },
        { new { kind = "line-count", min = 2, max = 2 }, "a\n\n b\n", "a\n" },
        { new { kind = "starts-with", text = "Hi" }, "Hi there", "Oh Hi" },
        { new { kind = "ends-with", text = "bye." }, "ok bye.", "bye. ok" },
        { new { kind = "all-lowercase" }, "abc 1", "abC" },
        { new { kind = "no-letter", letter = "e" }, "a cat", "A cEt" },
        { new { kind = "json-keys", keys = new[] { "a", "b" } }, """{"a":1,"b":2,"c":3}""", """{"a":1}""" },
        { new { kind = "json-keys", keys = new[] { "a", "b" }, exact = true }, """{"b":1,"a":2}""", """{"a":1,"b":2,"c":3}""" },
        { new { kind = "json-keys", keys = new[] { "a" } }, """{"a":1}""", "[1]" },
        { new { kind = "bullet-count", count = 2 }, "x\n- a\n  * b", "- a\n-b\nc" },
    };

    [Theory, MemberData(nameof(Cases))]
    public async Task EveryKindPassesAndFails(object constraint, string good, string bad)
    {
        var pass = Assert.Single(await Run([constraint], good));
        Assert.True(pass.Passed, pass.Diagnostic);
        Assert.StartsWith(((dynamic)constraint).kind, pass.Name);
        var fail = Assert.Single(await Run([constraint], bad));
        Assert.False(fail.Passed);
        Assert.NotNull(fail.Diagnostic);
        Assert.NotNull(fail.Expected);
        Assert.NotNull(fail.Actual);
    }

    [Fact] public async Task OneAssertionPerConstraintNamedByIndex()
    {
        var o = await Run([new { kind = "all-lowercase" }, new { kind = "contains", text = "x" }], "abc");
        Assert.Equal(["all-lowercase[0]", "contains[1]"], o.Select(x => x.Name));
        Assert.Equal([true, false], o.Select(x => x.Passed));
    }

    [Fact] public async Task RegexTimeoutFailsWithDiagnostic()
    {
        var o = Assert.Single(await Run([new { kind = "regex", pattern = "^(a+)+$" }], new string('a', 60) + "b"));
        Assert.False(o.Passed);
        Assert.Contains("timed out", o.Diagnostic);
    }

    [Fact] public async Task MissingAnswerFailsEveryConstraint()
        => Assert.All(await Run([new { kind = "all-lowercase" }, new { kind = "line-count", min = 1 }], null), o => Assert.Equal("no answer", o.Diagnostic));
}

public class JsonSchemaGraderTests
{
    private static readonly object Schema = new
    {
        type = "object",
        required = new[] { "a", "b" },
        properties = new { a = new { type = "integer" }, b = new { type = "string" } },
    };

    private static async Task<IReadOnlyList<AssertionOutcome>> Run(string? answer)
    {
        var ctx = new GradingContext(TaskPrompt.Simple("q"), new TaskHidden([]), answer, answer, [], null, new UnavailableSandbox(), default);
        return await GraderRegistry.Get("json-schema").GradeAsync(new GraderSpec("json-schema", "g", JsonUtil.Element(new { schema = Schema })), ctx);
    }

    [Fact] public async Task ValidDocumentPassesParsesAndSchema()
    {
        var o = await Run("""{"a":1,"b":"x"}""");
        Assert.Equal(["parses", "schema"], o.Select(x => x.Name));
        Assert.All(o, x => Assert.True(x.Passed));
    }

    [Fact] public async Task InvalidDocumentReportsEachErrorByLocation()
    {
        var o = await Run("""{"a":"no","b":5}""");
        Assert.True(o[0].Passed);
        var errors = o.Skip(1).ToList();
        Assert.All(errors, e => { Assert.False(e.Passed); Assert.NotNull(e.Diagnostic); });
        Assert.Contains(errors, e => e.Name == "/a");
        Assert.Contains(errors, e => e.Name == "/b");
    }

    [Fact] public async Task MissingRequiredIsReportedAtTheRoot()
    {
        var o = await Run("""{"a":1}""");
        Assert.Contains(o.Skip(1), e => !e.Passed && e.Diagnostic!.Contains("required"));
    }

    [Fact] public async Task UnparsableAnswerFailsParsesOnly()
    {
        var o = Assert.Single(await Run("{nope"));
        Assert.Equal("parses", o.Name);
        Assert.False(o.Passed);
    }

    [Fact] public async Task NoAnswerFails() => Assert.False(Assert.Single(await Run(null)).Passed);
}
