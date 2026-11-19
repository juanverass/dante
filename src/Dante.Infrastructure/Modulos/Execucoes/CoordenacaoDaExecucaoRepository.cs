using Dante.Application.Execucoes;
using Dante.Domain.Execucoes;
using Dante.Infrastructure.Data.Orquestracao;
using Dante.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dante.Infrastructure.Modulos.Execucoes;

public sealed class CoordenacaoDaExecucaoRepository(OrquestracaoDbContext context)
    : Repository<CoordenacaoDaExecucao>(context), ICoordenacaoDaExecucaoRepository
{
    public Task<CoordenacaoDaExecucao?> ObterDaExecucaoAsync(Guid idExecucao, CancellationToken cancellationToken = default) =>
        DbSet.SingleOrDefaultAsync(x => x.IdExecucao == idExecucao, cancellationToken);
}
