using Dante.Application.Comum;
using Dante.Domain.Execucoes;

namespace Dante.Application.Execucoes;

public interface IExecucaoRepository : IRepository<Execucao>
{
    // Próximo número da sequência persistente; nunca se repete, nem depois de reiniciar o Worker.
    Task<long> ReservarNumeroAsync(CancellationToken cancellationToken = default);
    Task<Execucao?> ObterPorNumeroAsync(long numero, CancellationToken cancellationToken = default);
}
