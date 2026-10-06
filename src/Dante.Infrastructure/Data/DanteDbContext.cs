using Dante.Application.SegurancaDoBrain;
using Microsoft.EntityFrameworkCore;

namespace Dante.Infrastructure.Data;

public class DanteDbContext(DbContextOptions options, AutorizacaoDoBrain? autorizacao = null) : DbContext(options)
{
    public DbSet<Dante.Domain.EspacosDeConhecimento.EspacoDeConhecimento> EspacosDeConhecimento => Set<Dante.Domain.EspacosDeConhecimento.EspacoDeConhecimento>();
    public DbSet<Dante.Domain.Projetos.Projeto> Projetos => Set<Dante.Domain.Projetos.Projeto>();
    public DbSet<Dante.Domain.Conhecimentos.Conhecimento> Conhecimentos => Set<Dante.Domain.Conhecimentos.Conhecimento>();

    public DbSet<Dante.Domain.RelacoesDeConhecimento.RelacaoDeConhecimento> RelacoesDeConhecimento => Set<Dante.Domain.RelacoesDeConhecimento.RelacaoDeConhecimento>();

    public DbSet<Dante.Domain.CapturaDeConhecimento.CandidatoDeConhecimento> CandidatosDeConhecimento => Set<Dante.Domain.CapturaDeConhecimento.CandidatoDeConhecimento>();

    // Contextos construídos diretamente são reservados a migrations/admin/fixtures. DI sempre injeta autorização.
    public bool Administracao => autorizacao is null;
    public Guid IdTenant => autorizacao?.Identidade?.IdTenant ?? Guid.Empty;
    public Guid IdUsuario => autorizacao?.Identidade?.IdUsuario ?? Guid.Empty;
    public Guid IdEspaco => autorizacao?.Escopo?.IdEspacoDeConhecimento ?? Guid.Empty;
    public Guid? IdProjeto => autorizacao?.Escopo?.IdProjeto;
    public bool Confidencial => autorizacao?.Escopo?.PermitirConfidencial == true;
    public bool Secreto => autorizacao?.Escopo?.PermitirSecreto == true;

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => SaveChangesAsync(true, cancellationToken);

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        await ValidacaoDeEscritaDoBrain.ValidarAsync(this, cancellationToken);
        return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    public override int SaveChanges() => SaveChangesAsync().GetAwaiter().GetResult();
    public override int SaveChanges(bool acceptAllChangesOnSuccess) => SaveChangesAsync(acceptAllChangesOnSuccess).GetAwaiter().GetResult();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasDefaultSchema("brain_data");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(DanteDbContext).Assembly);
        FiltrosDoBrain.Aplicar(modelBuilder, this);
    }
}
