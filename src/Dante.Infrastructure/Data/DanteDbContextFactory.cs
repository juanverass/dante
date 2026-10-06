using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Dante.Infrastructure.Data;

public sealed class DanteDbContextFactory : IDesignTimeDbContextFactory<DanteDbContext>
{
    public DanteDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Dante");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Configure ConnectionStrings__Dante fora do repositório.");
        var options = new DbContextOptionsBuilder<DanteDbContext>();
        options.UseNpgsql(connectionString, provider =>
            provider.MigrationsHistoryTable("__EFMigrationsHistory", "brain_meta"));
        return new DanteDbContext(options.Options);
    }
}
