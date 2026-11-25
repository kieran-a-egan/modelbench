namespace ModelBench.Core.Grading;

public static class GraderRegistry
{
    private static readonly IReadOnlyDictionary<string, IGrader> ByType = new IGrader[]
    {
        new ExactGrader(), new StructuralGrader(), new NumericGrader(), new TextConstraintsGrader(), new JsonSchemaGrader(),
        new CompileGrader(), new UnitTestsGrader(), new DifferentialGrader(), new PerformanceGrader(), new AstGrader(),
        new ConstraintGrader(), new ToolCallsGrader(),
    }.ToDictionary(g => g.Type);

    public static IGrader Get(string type) => ByType.TryGetValue(type, out var g)
        ? g : throw new InvalidOperationException($"unknown grader type '{type}'");
}
