using Dante.Application.Comum;
using Dante.Domain.Execucoes;

namespace Dante.Application.Execucoes;

public interface ISessaoDeAgenteRepository : IRepository<SessaoDeAgente>
{
    // Próximo número da sequência persistente de sessões (código S…), sempre acima de acimaDe.
    Task<long> ReservarNumeroAsync(long acimaDe = 0, CancellationToken cancellationToken = default);
    Task<SessaoDeAgente?> ObterPorNumeroAsync(long numero, CancellationToken cancellationToken = default);

    // Recuperação na inicialização (AD-67).
    Task<IReadOnlyList<SessaoDeAgente>> ListarNaoTerminaisAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SessaoDeAgente>> ListarPorUnidadesDeContencaoAsync(IReadOnlyCollection<string> unidades,
        CancellationToken cancellationToken = default);
}
