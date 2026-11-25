using System.Text;
using Microsoft.Data.Sqlite;
using ModelBench.Cli;
using ModelBench.Tests.Support;

namespace ModelBench.Tests.EndToEnd;

/// <summary>AC-24: API keys and secret headers never reach the DB file, exports or CLI output.</summary>
public class SecretHandlingTests
{
    private const string Key = "sk-LEAK-CANARY-123", Header = "hdr-LEAK-CANARY-456", EnvKey = "env-LEAK-CANARY-789";

    private static async Task<(int, string)> Cli(params string[] args)
    {
        using var sw = new StringWriter();
        var code = await CliApp.RunAsync(args, sw, sw);
        return (code, sw.ToString());
    }

    [Fact] public async Task NoSecretReachesTheDatabaseExportsOrCliOutput()
    {
        const string EnvVar = "MODELBENCH_TEST_LEAK_KEY";
        Environment.SetEnvironmentVariable(EnvVar, EnvKey);
        try
        {
            await using var server = await FakeServer.StartAsync((_, _) => FakeReply.Text("<answer>0</answer>"));
            using var dir = new TempDir();
            File.WriteAllText(dir.File("models.json"), $$$$"""
                {"providers":{
                  "lit":{"api":"openai-completions","baseUrl":"{{{{server.BaseUrl}}}}","apiKey":"{{{{Key}}}}","headers":{"x-custom-auth":"{{{{Header}}}}"},"models":[{"id":"m1"}]},
                  "envp":{"api":"openai-completions","baseUrl":"{{{{server.BaseUrl}}}}","apiKey":"{env:{{{{EnvVar}}}}}","models":[{"id":"m1"}]}}}
                """);
            string[] g = ["--db", dir.File("s.db"), "--models", dir.File("models.json")];
            var all = new StringBuilder();

            var (code, output) = await Cli([.. g, "suite", "generate", "--name", "s", "--seed", "3", "--family", "mathematics:1:2"]);
            Assert.Equal(0, code);
            all.Append(output);
            var suite = output.Split(' ')[1][..8];

            var runs = new List<string>();
            foreach (var model in new[] { "lit/m1", "envp/m1" })
            {
                (code, output) = await Cli([.. g, "run", "start", "--suite", suite, "--model", model]);
                Assert.Equal(0, code);
                all.Append(output);
                runs.Add(output.Split(' ')[1][..8]);
            }

            // The server really received the secrets, so absence below is meaningful.
            Assert.Contains(server.AuthHeaders, a => a == "Bearer " + Key);
            Assert.Contains(server.AuthHeaders, a => a == "Bearer " + EnvKey);
            Assert.Contains(server.Headers, h => h.GetValueOrDefault("x-custom-auth") == Header);

            foreach (var run in runs)
            {
                foreach (var args in new[] { new[] { "run", "show", run, "--failures" }, ["run", "regrade", run] })
                {
                    (code, output) = await Cli([.. g, .. args]);
                    Assert.Equal(0, code);
                    all.Append(output);
                }
                (code, output) = await Cli([.. g, "export", "--run", run, "--format", "json", "--out", dir.File($"{run}.json")]);
                Assert.Equal(0, code);
                (code, output) = await Cli([.. g, "export", "--run", run, "--format", "csv", "--out", dir.File($"csv-{run}")]);
                Assert.Equal(0, code);
            }
            foreach (var args in new[] { new[] { "run", "list" }, ["models", "list"], ["suite", "list"], ["leaderboard", "--all-runs"] })
            {
                (_, output) = await Cli([.. g, .. args]);
                all.Append(output);
            }

            SqliteConnection.ClearAllPools();
            var files = Directory.EnumerateFiles(dir.Path, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith("models.json")).ToList();
            Assert.Contains(files, f => f.EndsWith("s.db"));
            Assert.Contains(files, f => f.EndsWith(".json"));
            Assert.Contains(files, f => f.EndsWith("attempts.csv"));
            var haystacks = files.Select(f => (f, Encoding.Latin1.GetString(File.ReadAllBytes(f)))).Append(("cli output", all.ToString()));
            foreach (var (name, text) in haystacks)
                foreach (var canary in new[] { Key, Header, EnvKey, "LEAK-CANARY" })
                    Assert.False(text.Contains(canary, StringComparison.Ordinal), $"{canary} found in {name}");
        }
        finally { Environment.SetEnvironmentVariable(EnvVar, null); }
    }
}
