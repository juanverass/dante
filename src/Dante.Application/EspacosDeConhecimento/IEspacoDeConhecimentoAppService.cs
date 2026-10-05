using Dante.Application.Comum;
using Dante.Domain.EspacosDeConhecimento;

namespace Dante.Application.EspacosDeConhecimento;

public interface IEspacoDeConhecimentoAppService
    : ICrudBasicoAppService<EspacoDeConhecimentoDto, EspacoDeConhecimentoSearchDto, EspacoDeConhecimento>
{
    // False quando não há espaço com o Id; InvalidOperationException quando ele já está no estado pedido.
    Task<bool> ArquivarAsync(Guid id, CancellationToken cancellationToken = default);
    Task<bool> ReativarAsync(Guid id, CancellationToken cancellationToken = default);
}
