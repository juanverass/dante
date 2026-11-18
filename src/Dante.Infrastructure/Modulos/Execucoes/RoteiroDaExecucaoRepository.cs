using Dante.Application.Execucoes;
using Dante.Domain.Execucoes;
using Dante.Infrastructure.Data.Orquestracao;
using Dante.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dante.Infrastructure.Modulos.Execucoes;

public sealed class RoteiroDaExecucaoRepository(OrquestracaoDbContext context)
    : Repository<RoteiroDaExecucao>(context), IRoteiroDaExecucaoRepository
{
    public Task<RoteiroDaExecucao?> ObterDaExecucaoAsync(Guid idExecucao, CancellationToken cancellationToken = default) =>
        DbSet.SingleOrDefaultAsync(x => x.IdExecucao == idExecucao, cancellationToken);
}
