using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ModelBench.Core.Domain;
using ModelBench.Core.Families;
using ModelBench.Core.Providers;

namespace ModelBench.Core.Execution;

public sealed record EpisodeResult(IReadOnlyList<ChatMessage> Transcript, IReadOnlyList<ModelResponse> Responses, TimeSpan WallTime, JsonElement? FinalEnvironmentState = null);

/// <summary>Runs one episode against a provider. Messages come from the TaskPrompt only (ADR 0002).</summary>
public static class EpisodeRunner
{
    public const int ToolAssistedMaxCalls = 8, AgenticMaxCalls = 30, MaxToolArgsBytes = 64 * 1024;

    /// <summary>Runs the episode for the task's mode; tool modes get a fresh environment from the family (a retry restarts it).</summary>
    public static Task<EpisodeResult> RunAsync(ExecutionMode mode, TaskPrompt prompt, TaskHidden hidden, IBenchmarkFamily family, IModelProvider provider, CancellationToken ct)
    {
        if (mode is not (ExecutionMode.ToolAssisted or ExecutionMode.Agentic)) return RunSingleTurnAsync(prompt, provider, ct);
        var env = family.CreateEnvironment(hidden) ?? throw new InvalidOperationException($"family '{family.Id}' has no simulated environment but runs in {mode} mode");
        return RunToolLoopAsync(prompt, env, provider, mode == ExecutionMode.Agentic ? AgenticMaxCalls : ToolAssistedMaxCalls, ct);
    }

    public static async Task<EpisodeResult> RunSingleTurnAsync(TaskPrompt prompt, IModelProvider provider, CancellationToken ct)
    {
        var messages = Initial(prompt);
        var sw = Stopwatch.StartNew();
        var response = await provider.CompleteAsync(new ModelRequest(messages.ToArray(), []), ct);
        sw.Stop();

        messages.Add(Assistant(response));
        return new EpisodeResult(messages, [response], sw.Elapsed);
    }

    /// <summary>Calls the model until it answers without tool calls or maxCalls model calls were made (pending calls are then not executed).</summary>
    public static async Task<EpisodeResult> RunToolLoopAsync(TaskPrompt prompt, ISimulatedEnvironment env, IModelProvider provider, int maxCalls, CancellationToken ct)
    {
        var messages = Initial(prompt);
        var responses = new List<ModelResponse>();
        var sw = Stopwatch.StartNew();
        for (var n = 1; n <= maxCalls; n++)
        {
            var response = await provider.CompleteAsync(new ModelRequest(messages.ToArray(), prompt.Tools), ct);
            responses.Add(response);
            messages.Add(Assistant(response));
            if (response.ToolCalls.Count == 0 || n == maxCalls) break;
            foreach (var call in response.ToolCalls)
            {
                var result = Encoding.UTF8.GetByteCount(call.ArgumentsJson) > MaxToolArgsBytes
                    ? JsonUtil.Serialize(new { error = $"arguments exceed {MaxToolArgsBytes / 1024} KB" })
                    : env.Execute(call);
                messages.Add(new ChatMessage(Role.Tool, result, ToolCallId: call.Id, ToolName: call.Name));
            }
        }
        sw.Stop();
        return new EpisodeResult(messages, responses, sw.Elapsed, env.State);
    }

    private static List<ChatMessage> Initial(TaskPrompt prompt)
    {
        var messages = new List<ChatMessage>();
        if (!string.IsNullOrEmpty(prompt.System)) messages.Add(new ChatMessage(Role.System, prompt.System));
        messages.Add(new ChatMessage(Role.User, prompt.User));
        return messages;
    }

    private static ChatMessage Assistant(ModelResponse r)
        => new(Role.Assistant, r.Text, r.ToolCalls.Count > 0 ? r.ToolCalls : null);
}
