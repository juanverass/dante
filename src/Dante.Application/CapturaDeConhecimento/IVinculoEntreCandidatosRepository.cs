using Dante.Application.Comum;
using Dante.Domain.CapturaDeConhecimento;
namespace Dante.Application.CapturaDeConhecimento;

public interface IVinculoEntreCandidatosRepository : IRepository<VinculoEntreCandidatos>
{
    Task<VinculoEntreCandidatos?> ObterDoParAsync(Guid idEspaco, Guid idCandidatoIncidente, Guid idCandidatoSolucao, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<VinculoEntreCandidatos>> ListarDoCandidatoAsync(Guid idEspaco, Guid idCandidato, bool somentePendentes, CancellationToken cancellationToken = default);
}
