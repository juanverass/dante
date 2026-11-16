using Dante.Application.Execucoes;
using Dante.Domain.Execucoes;
using Dante.Infrastructure.Data.Orquestracao;
using Dante.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dante.Infrastructure.Modulos.Execucoes;

public sealed class DependenciaDeEtapaRepository(OrquestracaoDbContext context)
    : Repository<DependenciaDeEtapa>(context), IDependenciaDeEtapaRepository
{
    public async Task<IReadOnlyList<DependenciaDeEtapa>> ListarDaExecucaoAsync(Guid idExecucao,
        CancellationToken cancellationToken = default) =>
        await DbSet.Where(x => x.IdExecucao == idExecucao).OrderBy(x => x.IdEtapa).ThenBy(x => x.IdEtapaRequerida)
            .ToListAsync(cancellationToken);
}
