using ModelBench.Core.Domain;
using ModelBench.Core.Families;
using ModelBench.Core.Generation;

namespace ModelBench.Tests.Generation;

/// <summary>
/// Suite hashes must not depend on the host OS. Generators must emit '\n' only
/// (never AppendLine / Environment.NewLine, which is "\r\n" on Windows).
/// </summary>
public class PlatformIndependenceTests
{
    public static TheoryData<string> FamilyIds() => [.. FamilyRegistry.All.Select(f => f.Id)];

    [Theory]
    [MemberData(nameof(FamilyIds))]
    public void GeneratedTasksContainNoCarriageReturns(string familyId)
    {
        var family = FamilyRegistry.Get(familyId);
        for (var c = Complexity.Min; c <= Complexity.Max; c++)
        for (var seed = 1; seed <= 2; seed++)
        {
            var task = family.Generate(new Rng((long)seed), c, 0);
            var json = JsonUtil.Serialize(task.Prompt) + JsonUtil.Serialize(task.Hidden) + JsonUtil.Serialize(task.ReferenceTurns);
            Assert.False(json.Contains("\\r"), $"{familyId} c{c} seed {seed} emits a carriage return");
        }
    }
}
