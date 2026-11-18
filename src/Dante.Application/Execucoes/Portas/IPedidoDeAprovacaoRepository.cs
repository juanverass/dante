using Dante.Application.Comum;
using Dante.Domain.Execucoes;

namespace Dante.Application.Execucoes;

// Gate humano do Roteiro (#308): no máximo um pendente por execução (índice único parcial).
public interface IPedidoDeAprovacaoRepository : IRepository<PedidoDeAprovacao>
{
    Task<PedidoDeAprovacao?> ObterPendenteAsync(Guid idExecucao, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PedidoDeAprovacao>> ListarDaExecucaoAsync(Guid idExecucao,
        CancellationToken cancellationToken = default);
}
