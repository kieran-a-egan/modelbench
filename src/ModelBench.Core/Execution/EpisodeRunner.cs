using System.Diagnostics;
using ModelBench.Core.Domain;
using ModelBench.Core.Providers;

namespace ModelBench.Core.Execution;

public sealed record EpisodeResult(IReadOnlyList<ChatMessage> Transcript, IReadOnlyList<ModelResponse> Responses, TimeSpan WallTime);

/// <summary>Runs one episode against a provider. Messages come from the TaskPrompt only (ADR 0002).</summary>
public static class EpisodeRunner
{
    public static async Task<EpisodeResult> RunSingleTurnAsync(TaskPrompt prompt, IModelProvider provider, CancellationToken ct)
    {
        var messages = new List<ChatMessage>();
        if (!string.IsNullOrEmpty(prompt.System)) messages.Add(new ChatMessage(Role.System, prompt.System));
        messages.Add(new ChatMessage(Role.User, prompt.User));

        var sw = Stopwatch.StartNew();
        var response = await provider.CompleteAsync(new ModelRequest(messages.ToArray(), []), ct);
        sw.Stop();

        messages.Add(new ChatMessage(Role.Assistant, response.Text, response.ToolCalls.Count > 0 ? response.ToolCalls : null));
        return new EpisodeResult(messages, [response], sw.Elapsed);
    }
}
