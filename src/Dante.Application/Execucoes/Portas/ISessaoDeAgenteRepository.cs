using Dante.Application.Comum;
using Dante.Domain.Execucoes;

namespace Dante.Application.Execucoes;

public interface ISessaoDeAgenteRepository : IRepository<SessaoDeAgente>
{
    // Próximo número da sequência persistente de sessões (código S…).
    Task<long> ReservarNumeroAsync(CancellationToken cancellationToken = default);
    Task<SessaoDeAgente?> ObterPorNumeroAsync(long numero, CancellationToken cancellationToken = default);
}
