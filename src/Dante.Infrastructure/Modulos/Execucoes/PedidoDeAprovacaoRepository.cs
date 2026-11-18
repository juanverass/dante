using Dante.Application.Execucoes;
using Dante.Domain.Execucoes;
using Dante.Infrastructure.Data.Orquestracao;
using Dante.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dante.Infrastructure.Modulos.Execucoes;

public sealed class PedidoDeAprovacaoRepository(OrquestracaoDbContext context)
    : Repository<PedidoDeAprovacao>(context), IPedidoDeAprovacaoRepository
{
    public Task<PedidoDeAprovacao?> ObterPendenteAsync(Guid idExecucao, CancellationToken cancellationToken = default) =>
        DbSet.SingleOrDefaultAsync(x => x.IdExecucao == idExecucao && x.Estado == EstadoDoPedidoDeAprovacao.Pendente,
            cancellationToken);

    public async Task<IReadOnlyList<PedidoDeAprovacao>> ListarDaExecucaoAsync(Guid idExecucao,
        CancellationToken cancellationToken = default) =>
        await DbSet.Where(x => x.IdExecucao == idExecucao).OrderBy(x => x.CriadoEm).ToListAsync(cancellationToken);
}
