using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

if (args is ["--case"]) return CaseMain();
// Not dumpable: case processes run as the same user but cannot open /proc/1/fd/1 or /proc/1/mem to reach the frame stream (MB-004).
if (Native.prctl(4 /* PR_SET_DUMPABLE */, 0, 0, 0, 0) != 0) throw new InvalidOperationException("prctl(PR_SET_DUMPABLE) failed");

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
return 0;

static string Trunc(Exception e) { var s = $"{e.GetType().Name}: {e.Message}"; return s.Length > 500 ? s[..500] : s; }

List<object> Run(byte[] image)
{
    var entry = request.GetProperty("entryPoint").GetString()!;
    var reps = Math.Max(1, request.GetProperty("repetitions").GetInt32());
    return request.GetProperty("inputs").EnumerateArray().Select((args, i) => (object)Case(i, image, entry, args, reps)).ToList();
}

// MB-004: candidate code runs only in a child process per case. The runner checks its exit status and is the only writer of frames.
static object Case(int index, byte[] image, string entry, JsonElement args, int reps)
{
    object Fail(string error) => new { index, output = (object?)null, error, elapsedMs = 0.0 };
    var psi = new ProcessStartInfo(Environment.ProcessPath!)
    {
        RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
    };
    psi.ArgumentList.Add(typeof(Program).Assembly.Location);
    psi.ArgumentList.Add("--case");
    using var p = Process.Start(psi)!;
    var drain = p.StandardError.BaseStream.CopyToAsync(Stream.Null);
    try
    {
        p.StandardInput.Write(JsonSerializer.Serialize(new { image = Convert.ToBase64String(image), entryPoint = entry, args, repetitions = reps }));
        p.StandardInput.Close();
    }
    catch (IOException) { /* the case process exited early; its exit status says why */ }
    var read = Task.Run(() =>
    {
        using var kept = new MemoryStream();
        var buf = new byte[65536];
        int n;
        while ((n = p.StandardOutput.BaseStream.Read(buf)) > 0)
            if (kept.Length < 1 << 20) kept.Write(buf, 0, (int)Math.Min(n, (1 << 20) - kept.Length));
        return kept.ToArray();
    });
    p.WaitForExit();
    var exit = p.ExitCode;
    Reap(); // the case's own children may still hold its pipes open; stopping them ends them
    drain.Wait();
    var bytes = read.Result;
    if (exit != 0) return Fail($"case process exited with status {exit}");
    try
    {
        var c = JsonDocument.Parse(bytes).RootElement;
        var error = c.GetProperty("error") is { ValueKind: JsonValueKind.String } e ? e.GetString()! : c.GetProperty("error").ValueKind == JsonValueKind.Null ? null : throw new FormatException();
        var output = c.GetProperty("output") is { ValueKind: not JsonValueKind.Null } o ? (object?)o.Clone() : null;
        return new { index, output, error = error is { Length: > 500 } ? error[..500] : error, elapsedMs = c.GetProperty("elapsedMs").GetDouble() };
    }
    catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
    {
        return Fail("case process produced no result");
    }
}

// The runner is PID 1, so it inherits a case's leftover processes: stop and reap them, and clear every channel a case could leave state in (/tmp, /dev/shm, POSIX queues, SysV IPC), so nothing outlives its case.
static void Reap()
{
    if (Environment.ProcessId != 1) return; // only as the container's init; never signal every process of a host user
    Native.kill(-1, 9); // every process but init
    while (Native.waitpid(-1, IntPtr.Zero, 0) > 0) { }
    foreach (var dir in new[] { "/tmp", "/dev/shm", "/dev/mqueue" }) // the writable mounts are the other state a case could leave for the next
        foreach (var e in new DirectoryInfo(dir).EnumerateFileSystemInfos())
            try { if (e is DirectoryInfo d) d.Delete(true); else e.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    foreach (var l in File.ReadAllLines("/proc/sysvipc/shm").Skip(1)) Native.shmctl(int.Parse(l.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]), 0, IntPtr.Zero); // SysV objects outlive their creator; IPC_RMID (0) the ones the case (same uid) made
    foreach (var l in File.ReadAllLines("/proc/sysvipc/msg").Skip(1)) Native.msgctl(int.Parse(l.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]), 0, IntPtr.Zero);
    foreach (var l in File.ReadAllLines("/proc/sysvipc/sem").Skip(1)) Native.semctl(int.Parse(l.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]), 0, 0);
}

// The case process: one input, a fresh runtime, and no route to the runner's stdout.
static int CaseMain()
{
    var real = Console.OpenStandardOutput();
    var input = JsonDocument.Parse(Console.In.ReadToEnd()).RootElement;
    Console.SetOut(TextWriter.Null);
    Console.SetError(TextWriter.Null);
    object? output = null;
    string? error = null;
    var best = 0.0;
    try
    {
        var asm = Assembly.Load(Convert.FromBase64String(input.GetProperty("image").GetString()!));
        var entry = input.GetProperty("entryPoint").GetString()!;
        var dot = entry.LastIndexOf('.');
        var type = dot < 0 ? null : asm.GetType(entry[..dot]);
        var method = type?.GetMethod(entry[(dot + 1)..], BindingFlags.Public | BindingFlags.Static)
            ?? throw new MissingMethodException($"entry point '{entry}' not found (need a public static method)");
        var ps = method.GetParameters();
        var args = input.GetProperty("args").EnumerateArray().ToList();
        if (args.Count != ps.Length) throw new ArgumentException($"expected {ps.Length} arguments, got {args.Count}");
        best = double.MaxValue;
        object? value = null;
        for (var r = 0; r < input.GetProperty("repetitions").GetInt32(); r++)
        {
            var converted = ps.Select((p, k) => args[k].Deserialize(p.ParameterType)).ToArray();
            var sw = Stopwatch.StartNew();
            try { value = method.Invoke(null, converted); }
            catch (TargetInvocationException e) { throw e.InnerException ?? e; }
            best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
        }
        try { output = JsonSerializer.SerializeToElement(value, method.ReturnType == typeof(void) ? typeof(object) : method.ReturnType); }
        catch (Exception) { throw new InvalidOperationException($"unserialisable output: {method.ReturnType.Name}"); }
    }
    catch (Exception e) { (output, error, best) = (null, Trunc(e), 0.0); }
    real.Write(JsonSerializer.SerializeToUtf8Bytes(new { output, error, elapsedMs = best }));
    real.Flush();
    return 0;
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

static class Native
{
    [DllImport("libc.so.6", SetLastError = true)]
    public static extern int prctl(int option, ulong arg2, ulong arg3, ulong arg4, ulong arg5);

    [DllImport("libc.so.6", SetLastError = true)]
    public static extern int kill(int pid, int sig);

    [DllImport("libc.so.6", SetLastError = true)]
    public static extern int waitpid(int pid, IntPtr status, int options);

    [DllImport("libc.so.6")] public static extern int shmctl(int id, int cmd, IntPtr buf);
    [DllImport("libc.so.6")] public static extern int msgctl(int id, int cmd, IntPtr buf);
    [DllImport("libc.so.6")] public static extern int semctl(int id, int num, int cmd);
}
