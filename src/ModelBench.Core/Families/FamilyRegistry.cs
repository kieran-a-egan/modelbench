namespace ModelBench.Core.Families;

public static class FamilyRegistry
{
    public static readonly IReadOnlyList<IBenchmarkFamily> All = [new MathematicsFamily(),
        new CodeGenerationFamily(), new DebuggingFamily()];

    public static IBenchmarkFamily? Find(string id) => All.FirstOrDefault(f => f.Id == id);

    public static IBenchmarkFamily Get(string id) => Find(id)
        ?? throw new ArgumentException($"unknown family '{id}' (known: {string.Join(", ", All.Select(f => f.Id))})");
}
