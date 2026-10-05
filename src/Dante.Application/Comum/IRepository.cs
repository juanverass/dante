using Dante.Domain.Comum;

namespace Dante.Application.Comum;

// Porta genérica de persistência (AD-36, AD-39). Repositories específicos herdam e acrescentam consultas próprias;
// a implementação EF fica na Infrastructure (#168). Alterações só são gravadas por IUnitOfWork.
public interface IRepository<TEntity>
    where TEntity : EntidadeBase
{
    Task<TEntity?> ObterPorIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task AdicionarAsync(TEntity entidade, CancellationToken cancellationToken = default);
    void Atualizar(TEntity entidade);
    void Remover(TEntity entidade);
}
