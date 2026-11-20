using Dante.Application.Execucoes;
using Dante.Domain.Execucoes;
using Dante.Infrastructure.Data.Orquestracao;
using Dante.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dante.Infrastructure.Modulos.Execucoes;

public sealed class SinteseDaCoordenacaoRepository(OrquestracaoDbContext context)
    : Repository<SinteseDaCoordenacao>(context), ISinteseDaCoordenacaoRepository
{
    public Task<SinteseDaCoordenacao?> ObterDaExecucaoAsync(Guid idExecucao, CancellationToken cancellationToken = default) =>
        DbSet.Where(x => x.IdExecucao == idExecucao).FirstOrDefaultAsync(cancellationToken);
}
