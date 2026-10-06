using Npgsql;
using Dante.Application.Comum;
using Microsoft.EntityFrameworkCore;
using Dante.Infrastructure.Data;

namespace Dante.Infrastructure.Persistence;

public sealed class UnitOfWork(DanteDbContext context) : IUnitOfWork
{
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
