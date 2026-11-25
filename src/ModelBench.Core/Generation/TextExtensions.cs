using System.Text;

namespace ModelBench.Core.Generation;

public static class TextExtensions
{
    /// <summary>Appends a line ending in '\n' on every OS. Generators use this, never AppendLine (ADR 0004).</summary>
    public static StringBuilder Ln(this StringBuilder sb, string? line = null) => sb.Append(line).Append('\n');
}
