using Microsoft.EntityFrameworkCore;

namespace Dante.Infrastructure.Persistencia;

public class DanteDbContext(DbContextOptions options) : DbContext(options)
{
    public DbSet<Dante.Domain.EspacosDeConhecimento.EspacoDeConhecimento> EspacosDeConhecimento => Set<Dante.Domain.EspacosDeConhecimento.EspacoDeConhecimento>();
    public DbSet<Dante.Domain.Projetos.Projeto> Projetos => Set<Dante.Domain.Projetos.Projeto>();
    public DbSet<Dante.Domain.Conhecimentos.Conhecimento> Conhecimentos => Set<Dante.Domain.Conhecimentos.Conhecimento>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasDefaultSchema("brain_data");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(DanteDbContext).Assembly);
    }
}
