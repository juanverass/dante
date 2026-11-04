using Dante.Application.CapturaDeConhecimento;
using Dante.Domain.CapturaDeConhecimento;
using Microsoft.EntityFrameworkCore;
using Dante.Infrastructure.Data;
using Dante.Infrastructure.Persistence;
namespace Dante.Infrastructure.Modulos.CapturaDeConhecimento;
public sealed class VinculoEntreCandidatosRepository(DanteDbContext c) : Repository<VinculoEntreCandidatos>(c), IVinculoEntreCandidatosRepository
{
    public Task<VinculoEntreCandidatos?> ObterDoParAsync(Guid idEspaco, Guid idCandidatoIncidente, Guid idCandidatoSolucao, CancellationToken cancellationToken = default) =>
        DbSet.SingleOrDefaultAsync(x => x.IdEspacoDeConhecimento == idEspaco && x.IdCandidatoIncidente == idCandidatoIncidente && x.IdCandidatoSolucao == idCandidatoSolucao, cancellationToken);
    public async Task<IReadOnlyList<VinculoEntreCandidatos>> ListarDoCandidatoAsync(Guid idEspaco, Guid idCandidato, bool somentePendentes, CancellationToken cancellationToken = default) =>
        await DbSet.Where(x => x.IdEspacoDeConhecimento == idEspaco && (x.IdCandidatoIncidente == idCandidato || x.IdCandidatoSolucao == idCandidato) &&
                (!somentePendentes || x.Estado == EstadoDoVinculo.Pendente))
            .OrderBy(x => x.CriadoEm).ThenBy(x => x.Id).Take(100).ToListAsync(cancellationToken);
}
