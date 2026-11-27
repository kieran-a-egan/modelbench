using System.Text.Json;
using ModelBench.Core.Domain;
using ModelBench.Core.Grading;
using ModelBench.Core.Reporting;
using ModelBench.Core.Sandbox;

namespace ModelBench.Tests.Grading;


public class AnswerExtractorTests
{
    [Fact] public void TaggedTakesLastMatch()
        => Assert.Equal("2", AnswerExtractor.Extract("<answer>1</answer> then <ANSWER> 2 </ANSWER>", AnswerFormat.Tagged));

    [Fact] public void TaggedMissingIsNull()
        => Assert.Null(AnswerExtractor.Extract("the answer is 2", AnswerFormat.Tagged));

    [Fact] public void ThinkBlocksAreStripped()
    {
        Assert.Equal("final", AnswerExtractor.Extract(AnswerExtractor.StripReasoning("<think><answer>wrong</answer></think>\n<answer>final</answer>"), AnswerFormat.Tagged));
        Assert.Null(AnswerExtractor.Extract(AnswerExtractor.StripReasoning("<thinking>maybe <answer>x</answer></thinking>done"), AnswerFormat.Tagged));
    }

    [Fact] public void OrphanClosingAndUnclosedThinkAreStripped()
    {
        Assert.Equal("ok", AnswerExtractor.StripReasoning("reasoning <answer>no</answer></think>ok"));
        Assert.Equal("before", AnswerExtractor.StripReasoning("before<think>never closed <answer>no</answer>"));
    }

    [Fact] public void CodeBlockTakesLastFence()
        => Assert.Equal("b = 2", AnswerExtractor.Extract("```python\na = 1\n```\ntext\n```\nb = 2\n```", AnswerFormat.CodeBlock));

    [Fact] public void JsonMustParse()
    {
        Assert.Equal("{\"a\":1}", AnswerExtractor.Extract("<answer>{\"a\":1}</answer>", AnswerFormat.Json));
        Assert.Equal("[1,2]", AnswerExtractor.Extract("```json\n[1,2]\n```", AnswerFormat.Json));
        Assert.Null(AnswerExtractor.Extract("<answer>{oops</answer>", AnswerFormat.Json));
    }

    [Theory]
    [InlineData("<answer>```json\n[1,2]\n```</answer>")]
    [InlineData("<answer>\n```\n[1,2]\n```\n</answer>")]
    [InlineData("<answer>```[1,2]```</answer>")]
    public void JsonFencedInsideTagsParses(string text)
        => Assert.Equal("[1,2]", AnswerExtractor.Extract(text, AnswerFormat.Json));

    [Fact] public void JsonWithBackticksInsideAStringIsUnchanged()
        => Assert.Equal("""{"s":"```x```"}""", AnswerExtractor.Extract("""<answer>{"s":"```x```"}</answer>""", AnswerFormat.Json));

    [Theory]
    [InlineData("1e99999999999")]
    [InlineData("١٢٣")]
    [InlineData("٣/٤")]
    [InlineData("1e-99999999999")]
    public void RationalNeverThrowsOnModelText(string text)
        => Assert.False(ModelBench.Core.Families.Rational.TryParse(text, out _));

    [Fact] public void FreeTextIsTrimmed()
    {
        Assert.Equal("hello", AnswerExtractor.Extract("  hello \n", AnswerFormat.FreeText));
        Assert.Null(AnswerExtractor.Extract("   ", AnswerFormat.FreeText));
    }
}

public class GraderTests
{
    private static GradingContext Ctx(string? answer) => new(TaskPrompt.Simple("q"), new TaskHidden([]), answer, answer, [], null, new UnavailableSandbox(), default);

    private static GraderSpec Spec(string type, object config) => new(type, "g", JsonUtil.Element(config));

    private static async Task<IReadOnlyList<AssertionOutcome>> Run(string type, object config, string? answer)
        => await GraderRegistry.Get(type).GradeAsync(Spec(type, config), Ctx(answer));

    [Theory]
    [InlineData("none", "Paris", "Paris", true)]
    [InlineData("none", "Paris", "paris", false)]
    [InlineData("trim", "Paris", " Paris ", true)]
    [InlineData("whitespace", "a b", "a   b", true)]
    [InlineData("casefold", "Paris", "PARIS", true)]
    [InlineData("whitespace", "a b", "ab", false)]
    public async Task Exact(string normalize, string expected, string actual, bool ok)
    {
        var o = Assert.Single(await Run("exact", new { expected, normalize }, actual));
        Assert.Equal(ok, o.Passed);
        Assert.Equal(ok ? 1 : 0, o.Score);
        if (!ok) Assert.NotNull(o.Diagnostic);
    }

    [Fact] public async Task ExactNoAnswerFails()
    {
        var o = Assert.Single(await Run("exact", new { expected = "x" }, null));
        Assert.False(o.Passed);
        Assert.Equal("no answer", o.Diagnostic);
    }

    [Fact] public async Task StructuralPassesOnEqualDocuments()
    {
        var o = Assert.Single(await Run("structural", new { expected = new { a = 1, b = new[] { 1, 2 } } }, """{"b":[1,2],"a":1.0}"""));
        Assert.True(o.Passed);
    }

    [Fact] public async Task StructuralReportsOneAssertionPerMismatchingPath()
    {
        var outcomes = await Run("structural", new { expected = new { a = 1, b = new[] { 1, 2 }, c = "x" } }, """{"a":2,"b":[1,3],"d":true}""");
        var names = outcomes.Select(o => o.Name).OrderBy(n => n).ToArray();
        Assert.All(outcomes, o => Assert.False(o.Passed));
        Assert.Equal(["g:$.a", "g:$.b[1]", "g:$.c", "g:$.d"], names);
    }

    [Fact] public async Task StructuralUnorderedArrays()
    {
        var cfg = new { expected = new { xs = new[] { 1, 2, 3 } }, unorderedArrays = true };
        Assert.True(Assert.Single(await Run("structural", cfg, """{"xs":[3,1,2]}""")).Passed);
        Assert.False(Assert.Single(await Run("structural", cfg, """{"xs":[3,1,4]}""")).Passed);
        Assert.All(await Run("structural", new { expected = new { xs = new[] { 1, 2 } } }, """{"xs":[2,1]}"""), o => Assert.False(o.Passed));
    }

    [Fact] public async Task StructuralInvalidJsonFails()
        => Assert.False(Assert.Single(await Run("structural", new { expected = 1 }, "{nope")).Passed);

    [Theory]
    [InlineData("3/7", "3/7", true)]
    [InlineData("3/7", "6/14", true)] // value equality; canonical form is the exact grader's job
    [InlineData("3/7", "3/8", false)]
    [InlineData("2", "2.0", true)]
    [InlineData("1/4", "0.25", true)]
    [InlineData("1/3", "0.3333", false)] // no tolerance: exact
    [InlineData("-5", "-5", true)]
    [InlineData("5", "abc", false)]
    public async Task NumericExact(string expected, string actual, bool ok)
        => Assert.Equal(ok, Assert.Single(await Run("numeric", new { expected }, actual)).Passed);

    [Fact] public async Task NumericTolerances()
    {
        Assert.True(Assert.Single(await Run("numeric", new { expected = "1/3", absTol = 0.001 }, "0.3333")).Passed);
        Assert.False(Assert.Single(await Run("numeric", new { expected = "1/3", absTol = 0.00001 }, "0.3333")).Passed);
        Assert.True(Assert.Single(await Run("numeric", new { expected = "1000", relTol = 0.01 }, "1009")).Passed);
        Assert.False(Assert.Single(await Run("numeric", new { expected = "1000", relTol = 0.01 }, "1011")).Passed);
    }

    [Fact] public void UnknownGraderThrows() => Assert.Throws<InvalidOperationException>(() => GraderRegistry.Get("nope"));
}

public class GradingPipelineTests
{
    private static TaskHidden Hidden(string expected) => new([new GraderSpec("numeric", "value", JsonUtil.Element(new { expected }))]);

    private static Task<GradingResult> Grade(string? reply, string expected = "3/7")
        => GradingPipeline.GradeAsync(TaskPrompt.Simple("q"), Hidden(expected), [new ChatMessage(Role.Assistant, reply)], null, new UnavailableSandbox(), default);

    [Fact] public async Task CorrectAnswerScoresOne()
    {
        var r = await Grade("<answer>3/7</answer>");
        Assert.True(r.Passed);
        Assert.Equal(1.0, r.Score);
        Assert.Equal(["answer-format", "value"], r.Assertions.Select(a => a.Name));
    }

    [Fact] public async Task NoAnswerStillRunsEveryGraderAndFails()
    {
        var r = await Grade("I think it is 3/7");
        Assert.False(r.Passed);
        Assert.Equal(0.0, r.Score);
        Assert.Contains(r.Assertions, a => a.Name == "value" && a.Diagnostic == "no answer");
        Assert.Contains(r.Assertions, a => a.Name == "answer-format" && !a.Passed);
    }

    [Fact] public async Task ScoreIsWeightedMeanOfStoredAssertions()
    {
        var hidden = new TaskHidden([
            new GraderSpec("numeric", "a", JsonUtil.Element(new { expected = "1" }), Weight: 3),
            new GraderSpec("numeric", "b", JsonUtil.Element(new { expected = "2" }), Weight: 1, Required: false)]);
        var r = await GradingPipeline.GradeAsync(TaskPrompt.Simple("q"), hidden, [new ChatMessage(Role.Assistant, "<answer>1</answer>")], null, new UnavailableSandbox(), default);
        Assert.Equal(0.75, r.Score);
        Assert.True(r.Passed); // the failing assertion is not required
        Assert.Equal(r.Score, r.Assertions.Sum(a => a.Weight * a.Score) / r.Assertions.Sum(a => a.Weight));
    }

    [Fact] public async Task AnswerInsideThinkingDoesNotCount()
        => Assert.False((await Grade("<think><answer>3/7</answer></think>unsure")).Passed);
}

public class ConsoleSafeTests
{
    [Fact] public void StripsControlCharactersButKeepsNewlineAndTab()
        => Assert.Equal("a\tb\nc[31mred", ConsoleSafe.Sanitize("a\tb\nc\u001b[31mred\u0007\r\u0085\u007f"));

    [Fact] public void NullIsEmpty() => Assert.Equal("", ConsoleSafe.Sanitize(null));

    [Fact] public void KeepsOrdinaryUnicode() => Assert.Equal("naïve ≡ ü", ConsoleSafe.Sanitize("naïve ≡ ü"));
}
