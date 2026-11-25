using ModelBench.Core.Persistence;
using ModelBench.Core.Reporting;
using ModelBench.Web;
using ModelBench.Web.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

// DB path: MODELBENCH_DB (config or env), else ~/.modelbench/modelbench.db. Each caller opens and disposes its own BenchDb.
var dbPath = DbPaths.Resolve(builder.Configuration["MODELBENCH_DB"]);
builder.Services.AddSingleton<Func<BenchDb>>(_ => () => BenchDb.Open(dbPath));
builder.Services.AddSingleton(new ModelsFile(builder.Configuration["MODELBENCH_MODELS"]));
builder.Services.AddSingleton<RunHostService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RunHostService>());

var app = builder.Build();

if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAntiforgery();

app.MapStaticAssets();

app.MapGet("/export/runs/{id:guid}.json", (Guid id, Func<BenchDb> open) =>
{
    using var db = open();
    try { return Results.Text(Export.Json(db, id), "application/json", System.Text.Encoding.UTF8); }
    catch (InvalidOperationException) { return Results.NotFound(); }
});

app.MapGet("/export/runs/{id:guid}/{table}.csv", (Guid id, string table, Func<BenchDb> open) =>
{
    if (!Export.Tables.Contains(table)) return Results.NotFound();
    using var db = open();
    try
    {
        using var w = new StringWriter();
        Export.WriteCsv(db, id, table, w);
        return Results.Text(w.ToString(), "text/csv", System.Text.Encoding.UTF8);
    }
    catch (InvalidOperationException) { return Results.NotFound(); }
});

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();

