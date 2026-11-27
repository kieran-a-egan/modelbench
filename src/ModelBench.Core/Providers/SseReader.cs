using System.Runtime.CompilerServices;

namespace ModelBench.Core.Providers;

public static class SseReader
{
    /// <summary>Yields the payload of each "data:" line (one leading space removed). Comments and other fields are skipped; any other non-blank line (a non-SSE body) is added to <paramref name="ignored"/>.</summary>
    public static async IAsyncEnumerable<string> ReadDataAsync(Stream stream, [EnumeratorCancellation] CancellationToken ct, List<string>? ignored = null)
    {
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (line.Trim().Length > 0 && line[0] != ':' && !line.StartsWith("event:", StringComparison.Ordinal) && !line.StartsWith("id:", StringComparison.Ordinal) && !line.StartsWith("retry:", StringComparison.Ordinal)) ignored?.Add(line);
                continue;
            }
            var data = line[5..];
            yield return data.StartsWith(' ') ? data[1..] : data;
        }
    }
}
