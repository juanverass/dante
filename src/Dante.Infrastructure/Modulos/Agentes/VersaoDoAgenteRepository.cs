using Dante.Application.Agentes;
using Dante.Domain.Agentes;
using Dante.Infrastructure.Data.Orquestracao;
using Dante.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dante.Infrastructure.Modulos.Agentes;

public sealed class VersaoDoAgenteRepository(OrquestracaoDbContext context)
    : Repository<VersaoDoAgente>(context), IVersaoDoAgenteRepository
{
    public async Task<IReadOnlyList<VersaoDoAgente>> ListarDoAgenteAsync(Guid idAgente,
        CancellationToken cancellationToken = default) =>
        await DbSet.Where(x => x.IdAgente == idAgente).OrderBy(x => x.Numero).ToListAsync(cancellationToken);
}
