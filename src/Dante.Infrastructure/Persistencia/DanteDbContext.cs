using Microsoft.EntityFrameworkCore;

namespace Dante.Infrastructure.Persistencia;

public class DanteDbContext(DbContextOptions options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasDefaultSchema("brain_data");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(DanteDbContext).Assembly);
    }
}
