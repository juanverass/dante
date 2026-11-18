using Dante.Application.Grupos;
using Dante.Domain.Grupos;
using Dante.Infrastructure.Data.Orquestracao;
using Dante.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dante.Infrastructure.Modulos.Grupos;

public sealed class AtivacaoDeVersaoDoGrupoRepository(OrquestracaoDbContext context)
    : Repository<AtivacaoDeVersaoDoGrupo>(context), IAtivacaoDeVersaoDoGrupoRepository
{
    public async Task<IReadOnlyList<AtivacaoDeVersaoDoGrupo>> ListarDoGrupoAsync(Guid idGrupo,
        CancellationToken cancellationToken = default) =>
        await DbSet.Where(x => x.IdGrupo == idGrupo).OrderBy(x => x.Em).ToListAsync(cancellationToken);
}
