using Dante.Application.Comum;
using Dante.Domain.Execucoes;

namespace Dante.Application.Execucoes;

// Snapshot de uma Coordenada (AD-75, #310): um por execução. Só a substituição do objetivo altera a linha (trigger).
public interface ICoordenacaoDaExecucaoRepository : IRepository<CoordenacaoDaExecucao>
{
    Task<CoordenacaoDaExecucao?> ObterDaExecucaoAsync(Guid idExecucao, CancellationToken cancellationToken = default);
}
