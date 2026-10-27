using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Kanban.Infrastructure.Persistence;

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<KanbanDbContext>
{
    public KanbanDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("Database__ConnectionString");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Set Database__ConnectionString explicitly before generating migrations.");
        }

        return new KanbanDbContext(new DbContextOptionsBuilder<KanbanDbContext>()
            .UseSqlServer(connectionString).Options);
    }
}
