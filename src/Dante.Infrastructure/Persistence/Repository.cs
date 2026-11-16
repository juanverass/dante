using Dante.Application.Comum;
using Dante.Domain.Comum;
using Microsoft.EntityFrameworkCore;
using Dante.Infrastructure.Data;

namespace Dante.Infrastructure.Persistence;

public class Repository<TEntity> : IRepository<TEntity>
    where TEntity : EntidadeBase
{
    public Repository(DanteDbContext context) : this((DbContext)context)
    {
    }

    // Outro contexto do mesmo banco (o ledger de orquestração, AD-66) reutiliza o mesmo contrato e a mesma proteção.
    protected Repository(DbContext context)
    {
        Context = context;
        DbSet = context.Set<TEntity>();
    }

    protected DbContext Context { get; }
    protected DbSet<TEntity> DbSet { get; }

    public async Task<TEntity?> ObterPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        await DbSet.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

    public virtual async Task AdicionarAsync(TEntity entidade, CancellationToken cancellationToken = default)
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
