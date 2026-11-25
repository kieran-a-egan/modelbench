using ModelBench.Core.Domain;

namespace ModelBench.Core.Providers;


/// <summary>Replays fixed assistant turns, one per call. Used by selftest and tests.</summary>
public sealed class ScriptedProvider(IEnumerable<ChatMessage> turns) : IModelProvider
{
    private readonly Queue<ChatMessage> _turns = new(turns);

    public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct)
    {
        if (!_turns.TryDequeue(out var turn)) throw new InvalidOperationException("ScriptedProvider has no more turns");
        var response = new ModelResponse(turn.Content, turn.ToolCalls ?? [], null, turn.ToolCalls is { Count: > 0 } ? "tool_calls" : "stop",
            Usage.Unknown, new Timing(DateTimeOffset.UtcNow, null, TimeSpan.Zero), JsonUtil.Serialize(request), JsonUtil.Serialize(turn), turn.ProviderState);
        return Task.FromResult(response);
    }
}
