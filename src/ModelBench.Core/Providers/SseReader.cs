using System.Runtime.CompilerServices;

namespace ModelBench.Core.Providers;

public static class SseReader
{
    /// <summary>Yields the payload of each "data:" line (one leading space removed). Comments and other fields are skipped.</summary>
    public static async IAsyncEnumerable<string> ReadDataAsync(Stream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..];
            yield return data.StartsWith(' ') ? data[1..] : data;
        }
    }
}
