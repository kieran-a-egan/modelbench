using System.Text.Json;
using ModelBench.Core.Domain;
using ModelBench.Core.Sandbox;

namespace ModelBench.Core.Grading;

public sealed record GradingResult(double Score, bool Passed, string? FinalText, string? Answer, IReadOnlyList<AssertionResult> Assertions)
{
    /// <summary>Copies the graded outcome onto an attempt (Passed/Failed, score, final text, assertions).</summary>
    public void ApplyTo(Attempt attempt)
    {
        attempt.Outcome = Passed ? AttemptOutcome.Passed : AttemptOutcome.Failed;
        attempt.Score = Score;
        attempt.FinalText = FinalText;
        attempt.Assertions.AddRange(Assertions);
    }
}

/// <summary>Strip reasoning, extract the answer, run every grader, score as the weighted mean of assertions.</summary>
public static class GradingPipeline
{
    /// <summary>
    /// Bump whenever a change can move an existing attempt's Score or Passed. Stored on Run.GradingVersion.
    /// 1: original. 2: end-to-end ranking, exact numbers, executed-only tool credit, performance gated on correctness and unscored (issue #1).
    /// </summary>
    public const int Version = 2;

    public static async Task<GradingResult> GradeAsync(
        TaskPrompt prompt, TaskHidden hidden, IReadOnlyList<ChatMessage> transcript,
        JsonElement? finalEnvState, ISandbox sandbox, CancellationToken ct)
    {
        var raw = transcript.LastOrDefault(m => m.Role == Role.Assistant && !string.IsNullOrEmpty(m.Content))?.Content;
        var finalText = raw is null ? null : AnswerExtractor.StripReasoning(raw);
        var answer = AnswerExtractor.Extract(finalText, prompt.AnswerFormat);
        var ctx = new GradingContext(prompt, hidden, finalText, answer, transcript, finalEnvState, sandbox, ct);

        // Weight 0: instruction fidelity gates Passed but does not inflate the score of a wrong answer.
        var results = new List<AssertionResult>
        {
            new()
            {
                Grader = "answer-format", Name = "answer-format", Required = true, Weight = 0,
                Expected = prompt.AnswerFormat.ToString(), Actual = answer, Passed = answer is not null,
                Score = answer is not null ? 1 : 0,
                Diagnostic = answer is null ? $"no {prompt.AnswerFormat} answer found in the final text" : null,
            },
        };
        // A tool loop that ended on pending tool calls hit its step limit. Informational only.
        if (prompt.Tools.Count > 0 && transcript is [.., { Role: Role.Assistant, ToolCalls.Count: > 0 }])
            results.Add(new AssertionResult { Grader = "step-limit", Name = "step-limit", Required = false, Weight = 0, Passed = false, Score = 0, Diagnostic = "the model was still calling tools when the step limit was reached" });
        foreach (var spec in hidden.Graders)
        {
            // v2 (ADR 0006): performance is empirical (host wall time), so whatever the stored spec says (frozen v1 suites, static definitions)
            // it is reported but never moves Score or Passed.
            var scored = spec.Type != "performance";
            foreach (var o in await GraderRegistry.Get(spec.Type).GradeAsync(spec, ctx))
                results.Add(new AssertionResult
                {
                    Grader = spec.Type, Name = o.Name, Expected = o.Expected, Actual = o.Actual, Score = o.Score,
                    Weight = scored ? spec.Weight : 0, Required = scored && spec.Required, Passed = o.Passed, Diagnostic = o.Diagnostic,
                });
        }

        var passed = results.All(r => !r.Required || r.Passed);
        var totalWeight = results.Sum(r => r.Weight);
        var score = totalWeight > 0 ? results.Sum(r => r.Weight * r.Score) / totalWeight : passed ? 1 : 0;
        return new GradingResult(score, passed, finalText, answer, results);
    }
}
