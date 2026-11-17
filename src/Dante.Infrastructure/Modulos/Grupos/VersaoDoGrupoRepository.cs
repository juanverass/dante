using Dante.Application.Grupos;
using Dante.Domain.Grupos;
using Dante.Infrastructure.Data.Orquestracao;
using Dante.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dante.Infrastructure.Modulos.Grupos;

public sealed class VersaoDoGrupoRepository(OrquestracaoDbContext context)
    : Repository<VersaoDoGrupo>(context), IVersaoDoGrupoRepository
{
    public Task<VersaoDoGrupo?> ObterComParticipantesAsync(Guid id, CancellationToken cancellationToken = default) =>
        DbSet.Include(x => x.Participantes).SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

    public async Task<IReadOnlyList<VersaoDoGrupo>> ListarDoGrupoAsync(Guid idGrupo,
        CancellationToken cancellationToken = default) =>
        await DbSet.Include(x => x.Participantes).Where(x => x.IdGrupo == idGrupo).OrderBy(x => x.Numero)
            .ToListAsync(cancellationToken);
}
