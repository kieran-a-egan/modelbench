using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ModelBench.Web;

/// <summary>Plain-text formatting helpers. Everything returned is rendered through Blazor's default encoding.</summary>
public static class Fmt
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static string Pct(double? v) => v is { } d ? (d * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%" : "-";
    public static string Score(double? v) => v is { } d ? d.ToString("0.000", CultureInfo.InvariantCulture) : "-";
    // Same magnitude rule as the CLI: whole numbers from 100, 2 dp from 1, up to 6 dp below 1 (small costs/energy).
    public static string Num(double? v) => v is { } d ? d.ToString(Math.Abs(d) >= 100 ? "0" : Math.Abs(d) >= 1 ? "0.##" : "0.######", CultureInfo.InvariantCulture) : "-";
    public static string Delta(double? v) => v is { } d ? d.ToString("+0.######;-0.######;0", CultureInfo.InvariantCulture) : "-";
    public static string Width(double v) => (Math.Clamp(v, 0, 1) * 100).ToString("0.#", CultureInfo.InvariantCulture);
    public static string Short(Guid id) => id.ToString()[..8];
    public static string Date(DateTimeOffset d) => d.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    public static string Q(string value) => Uri.EscapeDataString(value);

    /// <summary>Indented JSON when parseable, otherwise the text unchanged.</summary>
    public static string Pretty(string? json)
    {
        if (json is null) return "";
        try { return JsonNode.Parse(json)?.ToJsonString(Indented) ?? json; }
        catch (JsonException) { return json; }
    }
}
