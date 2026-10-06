using Dante.Application.SegurancaDoBrain;
using Dante.Domain.Conhecimentos;
using Microsoft.EntityFrameworkCore;

namespace Dante.Infrastructure.Persistencia;

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
        if (!Administracao)
        {
            if (IdUsuario == Guid.Empty) throw new UnauthorizedAccessException("Identidade Brain ausente.");
            foreach (var entrada in ChangeTracker.Entries().Where(x => x.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                if (entrada.Entity is Dante.Domain.EspacosDeConhecimento.EspacoDeConhecimento espaco)
                {
                    if (espaco.IdUsuario != IdUsuario || espaco.IdTenant != IdTenant)
                        throw new UnauthorizedAccessException("Espaço fora da identidade autorizada.");
                    if (entrada.State != EntityState.Added &&
                        ((Guid)entrada.Property("IdUsuario").OriginalValue! != IdUsuario || (Guid)entrada.Property("IdTenant").OriginalValue! != IdTenant))
                        throw new UnauthorizedAccessException("Proprietário imutável.");
                }
                else if (entrada.Metadata.FindProperty("IdEspacoDeConhecimento") is not null)
                {
                    var espacoId = (Guid)entrada.Property("IdEspacoDeConhecimento").CurrentValue!;
                    var projetoId = entrada.Metadata.FindProperty("IdProjeto") is not null ? (Guid?)entrada.Property("IdProjeto").CurrentValue :
                        entrada.Entity is Dante.Domain.Projetos.Projeto projeto ? projeto.Id : null;
                    if (espacoId != IdEspaco || projetoId != IdProjeto && entrada.Entity is not Dante.Domain.Projetos.Projeto ||
                        !await EspacosDeConhecimento.AnyAsync(x => x.Id == espacoId && x.Estado == Dante.Domain.EspacosDeConhecimento.EstadoDoEspacoDeConhecimento.Ativo, cancellationToken))
                        throw new UnauthorizedAccessException("Escrita fora do escopo autorizado.");
                    if (entrada.Entity is Dante.Domain.Projetos.Projeto && IdProjeto is not null && projetoId != IdProjeto)
                        throw new UnauthorizedAccessException("Projeto fora do escopo.");
                    if (entrada.Entity is not Dante.Domain.Projetos.Projeto && projetoId is not null &&
                        !await Projetos.AnyAsync(x => x.Id == projetoId && x.Estado == Dante.Domain.Projetos.EstadoDoProjeto.Ativo, cancellationToken))
                        throw new UnauthorizedAccessException("Projeto não gravável.");
                    if (entrada.Metadata.FindProperty("Sensibilidade") is not null)
                    {
                        var classe = (Sensibilidade)entrada.Property("Sensibilidade").CurrentValue!;
                        if (classe == Sensibilidade.Confidencial && !Confidencial || classe == Sensibilidade.Secreto && !Secreto)
                            throw new UnauthorizedAccessException("Classificação não autorizada.");
                    }
                }
            }
        }
        return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    public override int SaveChanges() => SaveChangesAsync().GetAwaiter().GetResult();
    public override int SaveChanges(bool acceptAllChangesOnSuccess) => SaveChangesAsync(acceptAllChangesOnSuccess).GetAwaiter().GetResult();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasDefaultSchema("brain_data");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(DanteDbContext).Assembly);
        modelBuilder.Entity<Dante.Domain.EspacosDeConhecimento.EspacoDeConhecimento>().HasQueryFilter(x => Administracao || x.IdTenant == IdTenant && x.IdUsuario == IdUsuario && (IdEspaco == Guid.Empty || x.Id == IdEspaco));
        modelBuilder.Entity<Dante.Domain.DocumentosFonte.DocumentoFonte>().HasQueryFilter(x => Administracao || x.IdEspacoDeConhecimento == IdEspaco && x.IdProjeto == IdProjeto && EspacosDeConhecimento.Any(e => e.Id == x.IdEspacoDeConhecimento) && (x.Sensibilidade < Sensibilidade.Confidencial || x.Sensibilidade == Sensibilidade.Confidencial && Confidencial || x.Sensibilidade == Sensibilidade.Secreto && Secreto));
        modelBuilder.Entity<Dante.Domain.Projetos.Projeto>().HasQueryFilter(x => Administracao || x.IdEspacoDeConhecimento == IdEspaco && (IdProjeto == null || x.Id == IdProjeto) && EspacosDeConhecimento.Any(e => e.Id == x.IdEspacoDeConhecimento));
        modelBuilder.Entity<Dante.Domain.Conhecimentos.Conhecimento>().HasQueryFilter(x => Administracao || x.IdEspacoDeConhecimento == IdEspaco && x.IdProjeto == IdProjeto && EspacosDeConhecimento.Any(e => e.Id == x.IdEspacoDeConhecimento) && (x.Sensibilidade < Sensibilidade.Confidencial || x.Sensibilidade == Sensibilidade.Confidencial && Confidencial || x.Sensibilidade == Sensibilidade.Secreto && Secreto));
        modelBuilder.Entity<Dante.Domain.CapturaDeConhecimento.CandidatoDeConhecimento>().HasQueryFilter(x => Administracao || x.IdEspacoDeConhecimento == IdEspaco && x.IdProjeto == IdProjeto && EspacosDeConhecimento.Any(e => e.Id == x.IdEspacoDeConhecimento) && (x.Sensibilidade < Sensibilidade.Confidencial || x.Sensibilidade == Sensibilidade.Confidencial && Confidencial || x.Sensibilidade == Sensibilidade.Secreto && Secreto));
        modelBuilder.Entity<Dante.Domain.ContextosDeTrabalho.ContextoDeTrabalho>().HasQueryFilter(x => Administracao || x.IdEspacoDeConhecimento == IdEspaco && x.IdProjeto == IdProjeto && EspacosDeConhecimento.Any(e => e.Id == x.IdEspacoDeConhecimento) && (x.Sensibilidade < Sensibilidade.Confidencial || x.Sensibilidade == Sensibilidade.Confidencial && Confidencial));
        modelBuilder.Entity<Dante.Domain.RelacoesDeConhecimento.RelacaoDeConhecimento>().HasQueryFilter(x => Administracao || x.IdEspacoDeConhecimento == IdEspaco && x.IdProjeto == IdProjeto && Conhecimentos.Any(c => c.Id == x.IdOrigem) && Conhecimentos.Any(c => c.Id == x.IdDestino));
    }
}
