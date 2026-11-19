using Dante.Application.Execucoes;
using Dante.Domain.Execucoes;
using Dante.Infrastructure.Data.Orquestracao;
using Dante.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dante.Infrastructure.Modulos.Execucoes;

public sealed class PlanoDaCoordenacaoRepository(OrquestracaoDbContext context)
    : Repository<PlanoDaCoordenacao>(context), IPlanoDaCoordenacaoRepository
{
    public async Task<IReadOnlyList<PlanoDaCoordenacao>> ListarDaExecucaoAsync(Guid idExecucao,
        CancellationToken cancellationToken = default) =>
        await DbSet.Where(x => x.IdExecucao == idExecucao).OrderBy(x => x.Revisao).ToListAsync(cancellationToken);

    public Task<PlanoDaCoordenacao?> ObterUltimaRevisaoAsync(Guid idExecucao, CancellationToken cancellationToken = default) =>
        DbSet.Where(x => x.IdExecucao == idExecucao).OrderByDescending(x => x.Revisao).FirstOrDefaultAsync(cancellationToken);
}
