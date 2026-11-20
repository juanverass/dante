using Dante.Application.Comum;
using Dante.Domain.Execucoes;

namespace Dante.Application.Execucoes;

// Síntese validada do coordenador (AD-75, D7, #310 PR 3): uma por execução, imutável e nunca removida (trigger).
public interface ISinteseDaCoordenacaoRepository : IRepository<SinteseDaCoordenacao>
{
    Task<SinteseDaCoordenacao?> ObterDaExecucaoAsync(Guid idExecucao, CancellationToken cancellationToken = default);
}
