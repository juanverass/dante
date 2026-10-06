using Dante.Domain.Conhecimentos;
using Microsoft.EntityFrameworkCore;

namespace Dante.Infrastructure.Data;

// Executada automaticamente por todas as variantes de SaveChanges do contexto.
internal static class ValidacaoDeEscritaDoBrain
{
    public static async Task ValidarAsync(DanteDbContext contexto, CancellationToken cancellationToken)
    {
        if (!contexto.Administracao)
        {
            if (contexto.IdUsuario == Guid.Empty) throw new UnauthorizedAccessException("Identidade Brain ausente.");
            foreach (var entrada in contexto.ChangeTracker.Entries().Where(x => x.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                if (entrada.Entity is Dante.Domain.EspacosDeConhecimento.EspacoDeConhecimento espaco)
                {
                    if (espaco.IdUsuario != contexto.IdUsuario || espaco.IdTenant != contexto.IdTenant)
                        throw new UnauthorizedAccessException("Espaço fora da identidade autorizada.");
                    if (entrada.State != EntityState.Added &&
                        ((Guid)entrada.Property("IdUsuario").OriginalValue! != contexto.IdUsuario || (Guid)entrada.Property("IdTenant").OriginalValue! != contexto.IdTenant))
                        throw new UnauthorizedAccessException("Proprietário imutável.");
                }
                else if (entrada.Metadata.FindProperty("IdEspacoDeConhecimento") is not null)
                {
                    var espacoId = (Guid)entrada.Property("IdEspacoDeConhecimento").CurrentValue!;
                    var projetoId = entrada.Metadata.FindProperty("IdProjeto") is not null ? (Guid?)entrada.Property("IdProjeto").CurrentValue :
                        entrada.Entity is Dante.Domain.Projetos.Projeto projeto ? projeto.Id : null;
                    if (espacoId != contexto.IdEspaco || projetoId != contexto.IdProjeto && entrada.Entity is not Dante.Domain.Projetos.Projeto ||
                        !await contexto.EspacosDeConhecimento.AnyAsync(x => x.Id == espacoId && x.Estado == Dante.Domain.EspacosDeConhecimento.EstadoDoEspacoDeConhecimento.Ativo, cancellationToken))
                        throw new UnauthorizedAccessException("Escrita fora do escopo autorizado.");
                    if (entrada.Entity is Dante.Domain.Projetos.Projeto && contexto.IdProjeto is not null && projetoId != contexto.IdProjeto)
                        throw new UnauthorizedAccessException("Projeto fora do escopo.");
                    if (entrada.Entity is not Dante.Domain.Projetos.Projeto && projetoId is not null &&
                        !await contexto.Projetos.AnyAsync(x => x.Id == projetoId && x.Estado == Dante.Domain.Projetos.EstadoDoProjeto.Ativo, cancellationToken))
                        throw new UnauthorizedAccessException("Projeto não gravável.");
                    if (entrada.Metadata.FindProperty("Sensibilidade") is not null)
                    {
                        var classe = (Sensibilidade)entrada.Property("Sensibilidade").CurrentValue!;
                        if (classe == Sensibilidade.Confidencial && !contexto.Confidencial || classe == Sensibilidade.Secreto && !contexto.Secreto)
                            throw new UnauthorizedAccessException("Classificação não autorizada.");
                    }
                }
            }
        }
    }
}
