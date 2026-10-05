using Dante.Application.Comum;
using Dante.Domain.Projetos;

namespace Dante.Application.Projetos;

// Criação exige espaço existente e ativo. As operações próprias devolvem false quando não há projeto com o Id e lançam
// InvalidOperationException quando o projeto está arquivado ou já no estado pedido.
public interface IProjetoAppService : ICrudBasicoAppService<ProjetoDto, ProjetoSearchDto, Projeto>
{
    // ArgumentException quando o alias é inválido ou não está cadastrado.
    Task<bool> AssociarRepositorioAsync(Guid id, string aliasDoRepositorio,
        CancellationToken cancellationToken = default);

    Task<bool> DesassociarRepositorioAsync(Guid id, CancellationToken cancellationToken = default);
    Task<bool> ArquivarAsync(Guid id, CancellationToken cancellationToken = default);
    Task<bool> ReativarAsync(Guid id, CancellationToken cancellationToken = default);
}
