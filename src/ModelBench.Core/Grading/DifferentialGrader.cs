namespace ModelBench.Core.Grading;

/// <summary>
/// Same config and mechanics as unit-tests; the cases are seeded random inputs whose expected values the family's
/// C# reference solver computed at generation time. Assertions are named "random[i]".
/// </summary>
public sealed class DifferentialGrader : UnitTestsGrader
{
    public override string Type => "differential";
    protected override string Prefix => "random";
}
