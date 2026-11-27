using System.Text.Json;
using ModelBench.Core.Domain;
using ModelBench.Core.Grading;
using ModelBench.Core.Sandbox;

namespace ModelBench.Tests.Grading;

public class ConstraintGraderTests
{
    private const string Knapsack = """{"capacity":10,"items":[{"id":1,"weight":5,"value":10},{"id":2,"weight":4,"value":40},{"id":3,"weight":6,"value":30},{"id":4,"weight":3,"value":50}]}""";
    private const string Schedule = """{"tasks":[{"id":1,"release":0,"duration":3,"deadline":10},{"id":2,"release":0,"duration":2,"deadline":10},{"id":3,"release":5,"duration":2,"deadline":20}]}""";
    private const string Coloring = """{"vertices":4,"maxColors":4,"edges":[[0,1],[1,2],[0,2],[2,3]]}""";

    private static async Task<GradingResult> Grade(string problem, string instance, int optimum, string answerText)
    {
        var inst = JsonDocument.Parse(instance).RootElement;
        var hidden = new TaskHidden([
            new GraderSpec("constraint", "feasibility", JsonUtil.Element(new { problem, instance = inst }), 3),
            new GraderSpec("constraint", "optimality", JsonUtil.Element(new { problem, instance = inst, optimum, optimalOnly = true }), 2)]);
        return await GradingPipeline.GradeAsync(TaskPrompt.Simple("x", AnswerFormat.Json), hidden,
            [new ChatMessage(Role.Assistant, answerText)], null, new UnavailableSandbox(), default);
    }

    private static string Ans(string json) => $"<answer>{json}</answer>";

    private static string[] Failed(GradingResult r) => r.Assertions.Where(a => !a.Passed).Select(a => a.Name).ToArray();

    [Fact] public async Task KnapsackOptimalPasses()
    {
        var r = await Grade("knapsack", Knapsack, 90, Ans("""{"items":[4,2]}"""));
        Assert.True(r.Passed); Assert.Equal(1.0, r.Score);
        Assert.Contains(r.Assertions, a => a.Name == "optimal" && a.Expected == "90" && a.Actual == "90");
    }

    [Fact] public async Task KnapsackFeasibleButSuboptimalFailsOnlyOptimalAndKeepsPartialCredit()
    {
        var r = await Grade("knapsack", Knapsack, 90, Ans("""{"items":[4,3]}"""));
        Assert.False(r.Passed);
        Assert.Equal(["optimal"], Failed(r));
        Assert.Equal("80", r.Assertions.Single(a => a.Name == "optimal").Actual);
        Assert.InRange(r.Score, 0.5, 0.99);
    }

    [Fact] public async Task EachSpecContributesItsDeclaredWeightRegardlessOfAssertionCount()
    {
        // feasibility (weight 3, several checks, all pass) + optimality (weight 2, fails) = 3/5
        var r = await Grade("knapsack", Knapsack, 90, Ans("""{"items":[]}"""));
        Assert.Equal(3.0 / 5, r.Score, 12);
    }

    [Theory]
    [InlineData("""{"items":[1,2,3]}""", "capacity")]
    [InlineData("""{"items":[2,9]}""", "item-ids-valid")]
    [InlineData("""{"items":[2,2]}""", "no-duplicates")]
    public async Task KnapsackInfeasibleAnswersFailTheirConstraint(string json, string constraint)
    {
        var r = await Grade("knapsack", Knapsack, 90, Ans(json));
        Assert.False(r.Passed);
        Assert.Contains(constraint, Failed(r));
        Assert.Contains("optimal", Failed(r));
    }

    [Fact] public async Task ScheduleOptimalAndSuboptimal()
    {
        var best = await Grade("schedule", Schedule, 7, Ans("""{"schedule":[{"id":1,"start":0},{"id":2,"start":3},{"id":3,"start":5}]}"""));
        Assert.True(best.Passed);
        Assert.Contains(best.Assertions, a => a.Name == "no-overlap[1,2]");
        Assert.Contains(best.Assertions, a => a.Name == "deadline[3]");

        var slow = await Grade("schedule", Schedule, 7, Ans("""{"schedule":[{"id":1,"start":0},{"id":2,"start":3},{"id":3,"start":8}]}"""));
        Assert.Equal(["optimal"], Failed(slow));
        Assert.Equal("10", slow.Assertions.Single(a => a.Name == "optimal").Actual);
    }

    [Theory]
    [InlineData("""{"schedule":[{"id":1,"start":0},{"id":2,"start":2},{"id":3,"start":5}]}""", "no-overlap[1,2]")]
    [InlineData("""{"schedule":[{"id":1,"start":8},{"id":2,"start":0},{"id":3,"start":5}]}""", "deadline[1]")]
    [InlineData("""{"schedule":[{"id":1,"start":0},{"id":2,"start":3},{"id":3,"start":4}]}""", "release[3]")]
    [InlineData("""{"schedule":[{"id":1,"start":0},{"id":2,"start":3}]}""", "all-scheduled")]
    [InlineData("""{"schedule":[{"id":1,"start":0},{"id":1,"start":3},{"id":3,"start":5}]}""", "no-duplicates")]
    public async Task ScheduleViolationsAreNamed(string json, string constraint)
    {
        var r = await Grade("schedule", Schedule, 7, Ans(json));
        Assert.False(r.Passed);
        Assert.Contains(constraint, Failed(r));
    }

    [Fact] public async Task ColoringOptimalSuboptimalAndInvalid()
    {
        var best = await Grade("coloring", Coloring, 3, Ans("""{"colors":[1,2,3,1]}"""));
        Assert.True(best.Passed);
        Assert.Contains(best.Assertions, a => a.Name == "edge[2,3]");
        Assert.Contains(best.Assertions, a => a.Name == "colors<=k");

        var wasteful = await Grade("coloring", Coloring, 3, Ans("""{"colors":[1,2,3,4]}"""));
        Assert.Equal(["optimal"], Failed(wasteful));

        var clash = await Grade("coloring", Coloring, 3, Ans("""{"colors":[1,1,2,3]}"""));
        Assert.Contains("edge[0,1]", Failed(clash));
        var short_ = await Grade("coloring", Coloring, 3, Ans("""{"colors":[1,2,3]}"""));
        Assert.Contains("colors-valid", Failed(short_));
    }

    [Fact] public async Task ColoringColourLimitIsChecked()
    {
        var tight = Coloring.Replace("\"maxColors\":4", "\"maxColors\":3");
        var r = await Grade("coloring", tight, 3, Ans("""{"colors":[1,2,3,4]}"""));
        Assert.Contains("colors<=k", Failed(r));
    }

    [Theory]
    [InlineData("no json here")]
    [InlineData("<answer>[1,2]</answer>")]
    public async Task UnparseableOrWrongShapeFailsEverything(string text)
    {
        var r = await Grade("knapsack", Knapsack, 90, text);
        Assert.False(r.Passed);
        Assert.All(r.Assertions.Where(a => a.Grader == "constraint"), a => Assert.False(a.Passed));
    }

    [Fact] public async Task UnknownProblemIsAConfigurationError()
        => await Assert.ThrowsAsync<ArgumentException>(() => Grade("sudoku", Knapsack, 1, Ans("{}")));
}
