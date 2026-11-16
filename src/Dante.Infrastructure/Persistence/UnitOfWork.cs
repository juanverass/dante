using Npgsql;
using Dante.Application.Comum;
using Microsoft.EntityFrameworkCore;
using Dante.Infrastructure.Data;

namespace Dante.Infrastructure.Persistence;

public class UnitOfWork : IUnitOfWork
{
    private readonly DbContext context;

    public UnitOfWork(DanteDbContext context) : this((DbContext)context)
    {
    }

    // Outro contexto do mesmo banco (o ledger de orquestração, AD-66) com a mesma tradução de conflitos.
    protected UnitOfWork(DbContext context) => this.context = context;

    // SaveChanges confirma todas as alterações numa transação; falha não produz commit parcial.
    public async Task SalvarAlteracoesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Corrida entre capturas equivalentes: UNIQUE protege a canônica e o erro público não expõe dados/SQL.
            throw new ConflitoDeConcorrenciaException();
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflitoDeConcorrenciaException();
        }
    }
}
