using Dante.Domain.Comum;

namespace Dante.Application.Comum;

// Operações CRUD e de pesquisa comuns (AD-39). AppServices específicos herdam este contrato e acrescentam suas
// operações; TEntity amarra o contrato à entidade sem expô-la ao chamador.
public interface ICrudBasicoAppService<TDto, TSearchDto, TEntity>
    where TDto : notnull
    where TSearchDto : notnull
    where TEntity : EntidadeBase
{
    Task<TDto?> ObterPorIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TDto>> PesquisarAsync(TSearchDto filtro, CancellationToken cancellationToken = default);
    Task<TDto> AdicionarAsync(TDto dto, CancellationToken cancellationToken = default);

    // Null quando não há entidade com o Id informado; o Id vem do parâmetro, nunca do DTO.
    Task<TDto?> AtualizarAsync(Guid id, TDto dto, CancellationToken cancellationToken = default);

    Task<bool> RemoverAsync(Guid id, CancellationToken cancellationToken = default);
}
