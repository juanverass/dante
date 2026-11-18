using Dante.Application.Execucoes;
using Dante.Domain.Execucoes;
using Dante.Infrastructure.Data.Orquestracao;
using Dante.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dante.Infrastructure.Modulos.Execucoes;

public sealed class VereditoRegistradoRepository(OrquestracaoDbContext context)
    : Repository<VereditoRegistrado>(context), IVereditoRegistradoRepository
{
    public Task<VereditoRegistrado?> ObterDaEtapaAsync(Guid idEtapa, CancellationToken cancellationToken = default) =>
        DbSet.SingleOrDefaultAsync(x => x.IdEtapa == idEtapa, cancellationToken);

    public async Task<IReadOnlyList<VereditoRegistrado>> ListarDaExecucaoAsync(Guid idExecucao,
        CancellationToken cancellationToken = default) =>
        await DbSet.Where(x => x.IdExecucao == idExecucao).OrderBy(x => x.RegistradoEm).ToListAsync(cancellationToken);
}
