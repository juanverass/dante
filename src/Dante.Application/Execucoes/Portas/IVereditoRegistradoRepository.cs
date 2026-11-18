using Dante.Application.Comum;
using Dante.Domain.Execucoes;

namespace Dante.Application.Execucoes;

// Vereditos do revisor (#308): um por etapa de revisão, imutáveis.
public interface IVereditoRegistradoRepository : IRepository<VereditoRegistrado>
{
    Task<VereditoRegistrado?> ObterDaEtapaAsync(Guid idEtapa, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<VereditoRegistrado>> ListarDaExecucaoAsync(Guid idExecucao, CancellationToken cancellationToken = default);
}
