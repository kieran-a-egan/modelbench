using ModelBench.Core.Domain;
using System.Text.Json;
using Json.Schema;

namespace ModelBench.Core.Grading;

/// <summary>Config: { schema: json }. A "parses" assertion, then one assertion per schema error (named by instance location) or a single "schema" pass.</summary>
public sealed class JsonSchemaGrader : IGrader
{
    public string Type => "json-schema";

    public Task<IReadOnlyList<AssertionOutcome>> GradeAsync(GraderSpec spec, GradingContext ctx)
        => Task.FromResult(Grade(spec, ctx));

    private static IReadOnlyList<AssertionOutcome> Grade(GraderSpec spec, GradingContext ctx)
    {
        if (ctx.Answer is null) return [AssertionOutcome.Check("parses", false, "valid JSON", null, "no answer")];
        JsonDocument doc;
        try { doc = JsonDocument.Parse(ctx.Answer); }
        catch (JsonException ex) { return [AssertionOutcome.Check("parses", false, "valid JSON", ctx.Answer, ex.Message)]; }
        using (doc)
        {
            var schema = JsonSchema.FromText(spec.Config.GetProperty("schema").GetRawText());
            var result = schema.Evaluate(doc.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
            var outcomes = new List<AssertionOutcome> { AssertionOutcome.Check("parses", true, "valid JSON", ctx.Answer) };
            if (result.IsValid) { outcomes.Add(AssertionOutcome.Check("schema", true, "schema-valid", ctx.Answer)); return outcomes; }
            Collect(result, outcomes);
            if (outcomes.Count == 1) outcomes.Add(AssertionOutcome.Check("schema", false, "schema-valid", ctx.Answer, "schema validation failed"));
            return outcomes;
        }
    }

    private static void Collect(EvaluationResults r, List<AssertionOutcome> into)
    {
        foreach (var (keyword, message) in r.Errors ?? [])
        {
            var loc = r.InstanceLocation.ToString();
            into.Add(AssertionOutcome.Check(loc.Length == 0 ? "$" : loc, false, "schema-valid", null, $"{keyword}: {message}"));
        }
        foreach (var d in r.Details ?? []) Collect(d, into);
    }
}
