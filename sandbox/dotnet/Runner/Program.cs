using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

var realOut = new StreamWriter(Console.OpenStandardOutput());
var request = JsonDocument.Parse(Console.In.ReadToEnd()).RootElement;
Console.SetOut(TextWriter.Null);
Console.SetError(TextWriter.Null);

var action = request.GetProperty("action").GetString()!;
var code = request.GetProperty("code").GetString()!;
var result = new Dictionary<string, object?> { ["compiled"] = true, ["compileErrors"] = null, ["cases"] = new List<object>(), ["astFacts"] = null };

const string Usings = "global using System;\nglobal using System.Collections.Generic;\nglobal using System.Linq;\nglobal using System.Text;\nglobal using System.IO;\nglobal using System.Threading;\nglobal using System.Threading.Tasks;\n";
var options = new CSharpParseOptions(LanguageVersion.Latest);
var tree = CSharpSyntaxTree.ParseText(code, options);

if (action == "inspect")
    result["astFacts"] = Inspect(tree.GetRoot());
else
{
    var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
        .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToList();
    var compilation = CSharpCompilation.Create("Candidate",
        [CSharpSyntaxTree.ParseText(Usings, options), tree], refs,
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
    using var ms = new MemoryStream();
    var emit = compilation.Emit(ms);
    if (!emit.Success)
    {
        result["compiled"] = false;
        result["compileErrors"] = string.Join("\n", emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => $"line {d.Location.GetLineSpan().StartLinePosition.Line + 1}: {d.Id} {d.GetMessage()}"));
    }
    else if (action == "run")
        result["cases"] = Run(ms.ToArray());
}

realOut.WriteLine($"MBRESULT:{request.GetProperty("nonce").GetString()}:{Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(result))}");
realOut.Flush();

string Trunc(Exception e) { var s = $"{e.GetType().Name}: {e.Message}"; return s.Length > 500 ? s[..500] : s; }

List<object> Run(byte[] image)
{
    var inputs = request.GetProperty("inputs").EnumerateArray().ToList();
    var reps = Math.Max(1, request.GetProperty("repetitions").GetInt32());
    var cases = new List<object>();
    var ctx = new AssemblyLoadContext("candidate", isCollectible: true);
    MethodInfo? method = null;
    string? setupError = null;
    try
    {
        var asm = ctx.LoadFromStream(new MemoryStream(image));
        var entry = request.GetProperty("entryPoint").GetString()!;
        var dot = entry.LastIndexOf('.');
        var type = dot < 0 ? null : asm.GetType(entry[..dot]);
        method = type?.GetMethod(entry[(dot + 1)..], BindingFlags.Public | BindingFlags.Static);
        if (method == null) setupError = $"entry point '{entry}' not found (need a public static method)";
    }
    catch (Exception e) { setupError = Trunc(e); }

    for (var i = 0; i < inputs.Count; i++)
    {
        if (setupError != null) { cases.Add(new { index = i, output = (object?)null, error = setupError, elapsedMs = 0.0 }); continue; }
        try
        {
            var ps = method!.GetParameters();
            var args = inputs[i].EnumerateArray().ToList();
            if (args.Count != ps.Length) throw new ArgumentException($"expected {ps.Length} arguments, got {args.Count}");
            double best = double.MaxValue;
            object? value = null;
            for (var r = 0; r < reps; r++)
            {
                var converted = ps.Select((p, k) => args[k].Deserialize(p.ParameterType)).ToArray();
                var sw = Stopwatch.StartNew();
                try { value = method.Invoke(null, converted); }
                catch (TargetInvocationException e) { throw e.InnerException ?? e; }
                best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
            }
            JsonElement output;
            try { output = JsonSerializer.SerializeToElement(value, method.ReturnType == typeof(void) ? typeof(object) : method.ReturnType); }
            catch (Exception) { throw new InvalidOperationException($"unserialisable output: {method.ReturnType.Name}"); }
            cases.Add(new { index = i, output = (object?)output, error = (string?)null, elapsedMs = best });
        }
        catch (Exception e) { cases.Add(new { index = i, output = (object?)null, error = Trunc(e), elapsedMs = 0.0 }); }
    }
    return cases;
}

static object Inspect(SyntaxNode root)
{
    var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>().ToList();
    var calls = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
        .Select(c => c.Expression switch { IdentifierNameSyntax n => n.Identifier.Text, MemberAccessExpressionSyntax m => m.Name.Identifier.Text, _ => null })
        .Where(n => n != null).Distinct().Order().ToList();
    return new
    {
        imports = root.DescendantNodes().OfType<UsingDirectiveSyntax>().Select(u => u.Name?.ToString()).Where(n => n != null).Distinct().Order().ToList(),
        functions = methods.Select(m => m.Identifier.Text).ToList(),
        classes = root.DescendantNodes().OfType<TypeDeclarationSyntax>().Select(c => c.Identifier.Text).ToList(),
        calledNames = calls,
        hasLoop = root.DescendantNodes().Any(n => n is ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax),
        recursiveFunctions = methods.Where(m => m.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Any(c => c.Expression is IdentifierNameSyntax n && n.Identifier.Text == m.Identifier.Text)).Select(m => m.Identifier.Text).ToList(),
    };
}
