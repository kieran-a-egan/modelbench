using System.Text;

namespace ModelBench.Core.Reporting;

public static class ConsoleSafe
{
    /// <summary>Strips C0/C1 control characters (including ESC) except \n and \t, so untrusted text cannot drive the terminal.</summary>
    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
            if (!char.IsControl(c) || c is '\n' or '\t') sb.Append(c);
        return sb.ToString();
    }
}
