using Dante.Application.Comum;
using Dante.Domain.Execucoes;

namespace Dante.Application.Execucoes;

public interface IExecucaoRepository : IRepository<Execucao>
{
    // Próximo número da sequência persistente, sempre acima de acimaDe; nunca se repete, nem depois de reiniciar.
    Task<long> ReservarNumeroAsync(long acimaDe = 0, CancellationToken cancellationToken = default);
    Task<Execucao?> ObterPorNumeroAsync(long numero, CancellationToken cancellationToken = default);
}
