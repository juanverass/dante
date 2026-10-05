using Dante.Application.Comum;
using Dante.Domain.Conhecimentos;

namespace Dante.Application.Conhecimentos;

public interface IConhecimentoAppService : ICrudBasicoAppService<ConhecimentoDto, ConhecimentoSearchDto, Conhecimento>
{
    Task<ConhecimentoDto?> CorrigirAsync(Guid id, ConhecimentoDto dto, CancellationToken cancellationToken = default);
    Task<bool> ConfirmarAsync(Guid id, int revisaoEsperada, ProvenienciaDto proveniencia, CancellationToken cancellationToken = default);
    Task<bool> InvalidarAsync(Guid id, int revisaoEsperada, ProvenienciaDto proveniencia, CancellationToken cancellationToken = default);
    Task<bool> SubstituirAsync(Guid id, Guid idSubstituto, int revisaoEsperada, ProvenienciaDto proveniencia,
        CancellationToken cancellationToken = default);
}
