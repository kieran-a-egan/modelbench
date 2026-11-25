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

    /// <summary>Creates the schema if needed, enables WAL and a busy timeout, and keeps the connection open.</summary>
    public static BenchDb Open(string path)
    {
        var db = new BenchDb(path);
        db.Database.EnsureCreated();
        db.Database.OpenConnection();
        db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
        db.Database.ExecuteSqlRaw("PRAGMA busy_timeout=30000;");
        return db;
    }

    protected override void OnConfiguring(DbContextOptionsBuilder options)
        => options.UseSqlite($"Data Source={path};Pooling=False;Default Timeout=30");

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
