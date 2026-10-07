using Dante.Application.Comum;
using Dante.Domain.CapturaDeConhecimento;
namespace Dante.Application.CapturaDeConhecimento;

public interface ICandidatoDeConhecimentoRepository : IRepository<CandidatoDeConhecimento>
{
    Task<CandidatoDeConhecimento?> ObterEquivalenteAsync(CandidatoDeConhecimento candidato, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CandidatoDeConhecimento>> ListarPendentesAsync(Guid idEspaco, Guid? idProjeto, int limite, CancellationToken cancellationToken = default, int deslocamento = 0);
}
