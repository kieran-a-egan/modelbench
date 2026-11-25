namespace ModelBench.Core.Generation;

/// <summary>Version is filled in by SuiteGenerator.Generate (the family version used); callers leave it 0.</summary>
public sealed record FamilySpec(string Family, int Complexity, int Count, int Version = 0);

public sealed record SuiteSpec(string Name, long MasterSeed, IReadOnlyList<FamilySpec> Families);
