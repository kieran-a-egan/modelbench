using Microsoft.EntityFrameworkCore;
using ModelBench.Core.Domain;

namespace ModelBench.Core.Persistence;

/// <summary>EF Core SQLite context shared by the CLI and Web hosts.</summary>
public sealed class BenchDb(string path) : DbContext
{
    public DbSet<Suite> Suites => Set<Suite>();
    public DbSet<TaskInstance> Tasks => Set<TaskInstance>();
    public DbSet<Run> Runs => Set<Run>();
    public DbSet<Attempt> Attempts => Set<Attempt>();
    public DbSet<AssertionResult> Assertions => Set<AssertionResult>();
    public DbSet<Metric> Metrics => Set<Metric>();

    public string DbPath { get; } = path;

    /// <summary>Applies pending migrations, enables WAL and a busy timeout, and keeps the connection open.</summary>
    public static BenchDb Open(string path)
    {
        var db = new BenchDb(path);
        try
        {
            db.Database.OpenConnection();
            // Pre-migration databases were made by EnsureCreated: tables but no history. Pre-release, so no converter.
            using (var cmd = db.Database.GetDbConnection().CreateCommand())
            {
                cmd.CommandText = "SELECT (SELECT count(*) FROM sqlite_master WHERE name = 'Runs'), (SELECT count(*) FROM sqlite_master WHERE name = '__EFMigrationsHistory')";
                using var r = cmd.ExecuteReader();
                r.Read();
                if (r.GetInt64(0) > 0 && r.GetInt64(1) == 0)
                    throw new InvalidOperationException($"database '{path}' predates schema migrations; delete or rename it and run again");
            }
            db.Database.Migrate();
        }
        catch
        {
            db.Dispose(); // do not leak the file handle when the database is rejected
            throw;
        }
        db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
        db.Database.ExecuteSqlRaw("PRAGMA busy_timeout=30000;");
        return db;
    }

    protected override void OnConfiguring(DbContextOptionsBuilder options)
        => options.UseSqlite($"Data Source={DbPath};Pooling=False;Default Timeout=30");

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<TaskInstance>().HasIndex(t => t.SuiteId);
        b.Entity<TaskInstance>().Property(t => t.Mode).HasConversion<string>();
        b.Entity<Run>().Property(r => r.Mode).HasConversion<string>();
        b.Entity<Run>().Property(r => r.Status).HasConversion<string>();
        b.Entity<Attempt>().Property(a => a.Outcome).HasConversion<string>();
        b.Entity<Attempt>().HasIndex(a => new { a.RunId, a.TaskId, a.RepeatIndex }).IsUnique();
        b.Entity<Metric>().Property(m => m.Provenance).HasConversion<string>();
    }
}
