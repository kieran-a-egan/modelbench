using System.Text.Json;
using ModelBench.Core.Domain;

namespace ModelBench.Core.Families;

/// <summary>Helpers shared by the code families.</summary>
internal static class CodeKit
{
    public static IReadOnlyList<ChatMessage> Turn(string text) => [new ChatMessage(Role.Assistant, text)];

    public static IReadOnlyList<ChatMessage> Answer(string lang, string code) => Turn($"Here is my solution:\n```{lang}\n{code}\n```");

    /// <summary>Syntactically broken in both Python and C#.</summary>
    public static string Broken(string code) => code + "\n)))";

    public static JsonElement[] Elems(object[] args) => JsonSerializer.SerializeToElement(args).EnumerateArray().ToArray();

    public static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value);

    public static string J(object value) => JsonSerializer.Serialize(value);

    public static string Call(string name, object[] args) => $"{name}({string.Join(", ", args.Select(J))})";
}
