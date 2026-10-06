using Dante.Application.ContextosDeTrabalho;
using Dante.Domain.ContextosDeTrabalho;
using Microsoft.EntityFrameworkCore;
using Dante.Infrastructure.Data;
using Dante.Infrastructure.Persistence;
namespace Dante.Infrastructure.Modulos.ContextosDeTrabalho;
public sealed class ContextoDeTrabalhoRepository(DanteDbContext context) : Repository<ContextoDeTrabalho>(context), IContextoDeTrabalhoRepository
{
    public Task<ContextoDeTrabalho?> ObterDoEscopoAsync(Guid idEspaco, Guid? idProjeto, CancellationToken cancellationToken = default) =>
        DbSet.SingleOrDefaultAsync(x => x.IdEspacoDeConhecimento == idEspaco && x.IdProjeto == idProjeto, cancellationToken);
}
