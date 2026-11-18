using Dante.Application.Comum;
using Dante.Domain.Execucoes;

namespace Dante.Application.Execucoes;

// Estado durável do Roteiro (#308): um por execução. Só a substituição do objetivo altera a linha (trigger).
public interface IRoteiroDaExecucaoRepository : IRepository<RoteiroDaExecucao>
{
    Task<RoteiroDaExecucao?> ObterDaExecucaoAsync(Guid idExecucao, CancellationToken cancellationToken = default);
}
