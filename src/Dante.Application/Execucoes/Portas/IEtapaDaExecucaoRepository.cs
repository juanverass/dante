using Dante.Application.Comum;
using Dante.Domain.Execucoes;

namespace Dante.Application.Execucoes;

public interface IEtapaDaExecucaoRepository : IRepository<EtapaDaExecucao>
{
    // Em ordem de número.
    Task<IReadOnlyList<EtapaDaExecucao>> ListarDaExecucaoAsync(Guid idExecucao,
        CancellationToken cancellationToken = default);

    // Leitura sem rastreamento das etapas de várias execuções (#305), em ordem de execução e número.
    Task<IReadOnlyList<EtapaDaExecucao>> ConsultarDasExecucoesAsync(IReadOnlyCollection<Guid> idsExecucao,
        CancellationToken cancellationToken = default);
}
