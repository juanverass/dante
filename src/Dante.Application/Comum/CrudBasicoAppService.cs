using Dante.Application.Mapeamento;
using Dante.Domain.Comum;

namespace Dante.Application.Comum;

// Base que elimina boilerplate, não semântica (AD-39). Criação usa o mapping explícito TDto → TEntity, que constrói a
// entidade pelo domínio (AD-37); atualização carrega a entidade e delega ao AppService específico, que chama métodos
// do domínio; pesquisa é uma consulta do repository específico, nunca SearchDto convertido por mapping.
public abstract class CrudBasicoAppService<TDto, TSearchDto, TEntity>
    : ICrudBasicoAppService<TDto, TSearchDto, TEntity>
    where TDto : notnull
    where TSearchDto : notnull
    where TEntity : EntidadeBase
{
    protected readonly IMapsterTypeAdapter TypeAdapter;
    protected readonly IRepository<TEntity> Repository;
    protected readonly IUnitOfWork UnitOfWork;

    protected CrudBasicoAppService(IRepository<TEntity> repository, IUnitOfWork unitOfWork,
        IMapsterTypeAdapter typeAdapter)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(typeAdapter);
        Repository = repository;
        UnitOfWork = unitOfWork;
        TypeAdapter = typeAdapter;
    }

    public virtual async Task<TDto?> ObterPorIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entidade = await Repository.ObterPorIdAsync(id, cancellationToken);
        return entidade is null ? default : ParaDto(entidade);
    }

    public virtual async Task<IReadOnlyList<TDto>> PesquisarAsync(TSearchDto filtro,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filtro);
        var entidades = await ConsultarAsync(filtro, cancellationToken);
        return entidades.Select(ParaDto).ToArray();
    }

    public virtual async Task<TDto> AdicionarAsync(TDto dto, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dto);
        var entidade = CriarEntidade(dto);
        await Repository.AdicionarAsync(entidade, cancellationToken);
        await UnitOfWork.SalvarAlteracoesAsync(cancellationToken);
        return ParaDto(entidade);
    }

    public virtual async Task<TDto?> AtualizarAsync(Guid id, TDto dto, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dto);
        var entidade = await Repository.ObterPorIdAsync(id, cancellationToken);
        if (entidade is null) return default;
        AplicarAlteracoes(entidade, dto);
        Repository.Atualizar(entidade);
        await UnitOfWork.SalvarAlteracoesAsync(cancellationToken);
        return ParaDto(entidade);
    }

    public virtual async Task<bool> RemoverAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entidade = await Repository.ObterPorIdAsync(id, cancellationToken);
        if (entidade is null) return false;
        Repository.Remover(entidade);
        await UnitOfWork.SalvarAlteracoesAsync(cancellationToken);
        return true;
    }

    // Filtros e limites do SearchDto são validados e traduzidos pelo AppService específico.
    protected abstract Task<IReadOnlyList<TEntity>> ConsultarAsync(TSearchDto filtro,
        CancellationToken cancellationToken);

    // Chama métodos do domínio; nunca copia propriedades sobre a entidade nem altera o Id.
    protected abstract void AplicarAlteracoes(TEntity entidade, TDto dto);

    protected virtual TEntity CriarEntidade(TDto dto) => TypeAdapter.Mapear<TDto, TEntity>(dto);

    protected virtual TDto ParaDto(TEntity entidade) => TypeAdapter.Mapear<TEntity, TDto>(entidade);
}
