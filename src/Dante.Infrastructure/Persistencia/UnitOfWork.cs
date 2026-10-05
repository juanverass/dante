using Dante.Application.Comum;
using Microsoft.EntityFrameworkCore;

namespace Dante.Infrastructure.Persistencia;

public sealed class UnitOfWork(DanteDbContext context) : IUnitOfWork
{
    // SaveChanges confirma todas as alterações numa transação; falha não produz commit parcial.
    public async Task SalvarAlteracoesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflitoDeConcorrenciaException();
        }
    }
}
