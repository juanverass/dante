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

    // Leitura sem rastreamento dos eventos de um tipo de várias execuções (#305), em ordem de sequência. Dentro de uma
    // transação, enxerga o que outras já confirmaram (read committed) e o que esta mesma já gravou.
    Task<IReadOnlyList<EventoDeOrquestracao>> ConsultarDoTipoAsync(IReadOnlyCollection<Guid> idsExecucao,
        TipoDeEventoDeOrquestracao tipo, CancellationToken cancellationToken = default);
}
