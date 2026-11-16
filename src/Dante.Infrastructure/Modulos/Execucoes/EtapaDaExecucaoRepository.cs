using Dante.Application.Execucoes;
using Dante.Domain.Execucoes;
using Dante.Infrastructure.Data.Orquestracao;
using Dante.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dante.Infrastructure.Modulos.Execucoes;

public sealed class EtapaDaExecucaoRepository(OrquestracaoDbContext context)
    : Repository<EtapaDaExecucao>(context), IEtapaDaExecucaoRepository
{
    public async Task<IReadOnlyList<EtapaDaExecucao>> ListarDaExecucaoAsync(Guid idExecucao,
        CancellationToken cancellationToken = default) =>
        await DbSet.Where(x => x.IdExecucao == idExecucao).OrderBy(x => x.Numero).ToListAsync(cancellationToken);
}
