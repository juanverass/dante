using Dante.Application.Comum;
using Dante.Domain.Agentes;

namespace Dante.Application.Agentes;

public interface IAgenteRepository : IRepository<Agente>
{
    // Inclui arquivados: o handle nunca volta a ser usado.
    Task<Agente?> ObterPorHandleAsync(string handle, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Agente>> ListarAsync(bool incluirArquivados, CancellationToken cancellationToken = default);
    // Execuções e sessões não terminais do ledger que usam alguma versão do agente.
    Task<int> ContarTrabalhoAtivoAsync(Guid idAgente, CancellationToken cancellationToken = default);
}
