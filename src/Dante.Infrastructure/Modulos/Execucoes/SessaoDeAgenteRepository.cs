using Dante.Application.Execucoes;
using Dante.Domain.Execucoes;
using Dante.Infrastructure.Data.Orquestracao;
using Dante.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dante.Infrastructure.Modulos.Execucoes;

public sealed class SessaoDeAgenteRepository(OrquestracaoDbContext context) : Repository<SessaoDeAgente>(context), ISessaoDeAgenteRepository
{
    public Task<long> ReservarNumeroAsync(long acimaDe = 0, CancellationToken cancellationToken = default) =>
        SequenciasDoLedger.ReservarAsync(Context, OrquestracaoDbContext.SequenciaDeSessoes, acimaDe, cancellationToken);

    public Task<SessaoDeAgente?> ObterPorNumeroAsync(long numero, CancellationToken cancellationToken = default) =>
        DbSet.SingleOrDefaultAsync(x => x.Numero == numero, cancellationToken);
}
