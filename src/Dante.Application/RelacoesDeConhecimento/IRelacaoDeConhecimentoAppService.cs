using Dante.Application.Conhecimentos;
using Dante.Domain.RelacoesDeConhecimento;
namespace Dante.Application.RelacoesDeConhecimento;

public interface IRelacaoDeConhecimentoAppService
{
    Task<RelacaoDeConhecimentoDto> RelacionarAsync(Guid idEspaco, Guid? idProjeto, Guid idOrigem, Guid idDestino,
        TipoDeRelacao tipo, ProvenienciaDto proveniencia, CancellationToken cancellationToken = default);
    Task<VizinhancaDto> ConsultarVizinhancaAsync(Guid idEspaco, Guid? idProjeto, Guid idRaiz,
        int profundidade = 1, int limite = 50, CancellationToken cancellationToken = default);
}
