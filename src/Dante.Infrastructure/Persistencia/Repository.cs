using Dante.Application.Comum;
using Dante.Domain.Comum;
using Microsoft.EntityFrameworkCore;

namespace Dante.Infrastructure.Persistencia;

public class Repository<TEntity>(DanteDbContext context) : IRepository<TEntity>
    where TEntity : EntidadeBase
{
    protected DanteDbContext Context { get; } = context;
    protected DbSet<TEntity> DbSet { get; } = context.Set<TEntity>();

    public async Task<TEntity?> ObterPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        await DbSet.FindAsync([id], cancellationToken);

    public async Task AdicionarAsync(TEntity entidade, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entidade);
        await DbSet.AddAsync(entidade, cancellationToken);
    }

    public void Atualizar(TEntity entidade)
    {
        ArgumentNullException.ThrowIfNull(entidade);
        // Não anexar cópia sem token original: isso perderia a proteção de concorrência.
        if (Context.Entry(entidade).State == EntityState.Detached)
            throw new InvalidOperationException("Carregue a entidade neste escopo antes de atualizar.");
        Context.Entry(entidade).State = EntityState.Modified;
    }

    public void Remover(TEntity entidade)
    {
        ArgumentNullException.ThrowIfNull(entidade);
        if (Context.Entry(entidade).State == EntityState.Detached)
            throw new InvalidOperationException("Carregue a entidade neste escopo antes de remover.");
        DbSet.Remove(entidade);
    }
}
