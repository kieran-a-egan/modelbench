using Microsoft.EntityFrameworkCore.Design;

namespace ModelBench.Core.Persistence;

/// <summary>Used only by 'dotnet ef migrations add'.</summary>
internal sealed class BenchDbFactory : IDesignTimeDbContextFactory<BenchDb>
{
    public BenchDb CreateDbContext(string[] args) => new("design-time.db");
}
