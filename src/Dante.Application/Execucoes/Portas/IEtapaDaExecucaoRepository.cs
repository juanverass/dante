using Dante.Application.Comum;
using Dante.Domain.Execucoes;

namespace Dante.Application.Execucoes;

public interface IEtapaDaExecucaoRepository : IRepository<EtapaDaExecucao>
{
    // Em ordem de número.
    Task<IReadOnlyList<EtapaDaExecucao>> ListarDaExecucaoAsync(Guid idExecucao,
        CancellationToken cancellationToken = default);
}
