namespace ModelBench.Core.Grading;

public static class GraderRegistry
{
    private static readonly IReadOnlyDictionary<string, IGrader> ByType =
        new IGrader[] { new ExactGrader(), new StructuralGrader(), new NumericGrader() }.ToDictionary(g => g.Type);

    public static IGrader Get(string type) => ByType.TryGetValue(type, out var g)
        ? g : throw new InvalidOperationException($"unknown grader type '{type}'");
}
