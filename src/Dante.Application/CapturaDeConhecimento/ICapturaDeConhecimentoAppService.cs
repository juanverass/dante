using Dante.Application.Conhecimentos;
namespace Dante.Application.CapturaDeConhecimento;

public interface ICapturaDeConhecimentoAppService
{
    Task<CandidatoDeConhecimentoDto> CapturarAsync(CapturaDeConhecimentoDto captura, CancellationToken cancellationToken = default);
    Task<CandidatoDeConhecimentoDto> CorrigirAsync(Guid idEspaco, Guid? idProjeto, Guid idCandidato, int revisaoEsperada,
        CapturaDeConhecimentoDto correcao, CancellationToken cancellationToken = default);
    Task<Guid> ConfirmarAsync(Guid idEspaco, Guid? idProjeto, Guid idCandidato, int revisaoEsperada,
        ProvenienciaDto responsavel, CancellationToken cancellationToken = default);
    Task RejeitarAsync(Guid idEspaco, Guid? idProjeto, Guid idCandidato, int revisaoEsperada, ProvenienciaDto responsavel, CancellationToken cancellationToken = default);
    Task DescartarAsync(Guid idEspaco, Guid? idProjeto, Guid idCandidato, int revisaoEsperada, ProvenienciaDto responsavel, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CandidatoDeConhecimentoDto>> ListarPendentesAsync(Guid idEspaco, Guid? idProjeto, int limite = 50, CancellationToken cancellationToken = default);
}
