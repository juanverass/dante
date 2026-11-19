using Dante.Application.Comum;
using Dante.Domain.Execucoes;

namespace Dante.Application.Execucoes;

// Revisões do plano de uma Coordenada (AD-75, #310). Nenhuma é removida; o conteúdo e o hash nunca mudam (trigger).
public interface IPlanoDaCoordenacaoRepository : IRepository<PlanoDaCoordenacao>
{
    // Todas as revisões, da inicial à última.
    Task<IReadOnlyList<PlanoDaCoordenacao>> ListarDaExecucaoAsync(Guid idExecucao,
        CancellationToken cancellationToken = default);

    Task<PlanoDaCoordenacao?> ObterUltimaRevisaoAsync(Guid idExecucao, CancellationToken cancellationToken = default);
}
