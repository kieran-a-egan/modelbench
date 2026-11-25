using ModelBench.Core.Config;

namespace ModelBench.Core.Providers;

public static class ProviderFactory
{
    public static IModelProvider Create(ResolvedModel model) => model.Api switch
    {
        ApiKind.OpenAiCompletions => new OpenAiCompletionsProvider(model),
        ApiKind.Manual => throw new NotSupportedException("manual models have no provider; use 'manual export' and 'manual import'"),
        _ => throw new NotSupportedException($"api '{model.Api}' is not implemented yet"),
    };
}
