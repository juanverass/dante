using Dante.Application.Comum;
using Dante.Domain.Execucoes;

namespace Dante.Application.Execucoes;

public interface IEventoDeOrquestracaoRepository : IRepository<EventoDeOrquestracao>
{
    // Em ordem de sequência global.
    Task<IReadOnlyList<EventoDeOrquestracao>> ListarDaExecucaoAsync(Guid idExecucao,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<EventoDeOrquestracao>> ListarDaSessaoAsync(Guid idSessaoDeAgente,
        CancellationToken cancellationToken = default);
}
