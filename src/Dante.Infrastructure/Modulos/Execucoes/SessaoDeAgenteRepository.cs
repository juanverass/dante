using Dante.Application.Execucoes;
using Dante.Domain.Execucoes;
using Dante.Infrastructure.Data.Orquestracao;
using Dante.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dante.Infrastructure.Modulos.Execucoes;

public sealed class SessaoDeAgenteRepository(OrquestracaoDbContext context) : Repository<SessaoDeAgente>(context), ISessaoDeAgenteRepository
{
    private const string Proximo =
        $"SELECT nextval('{OrquestracaoDbContext.Schema}.{OrquestracaoDbContext.SequenciaDeSessoes}') AS \"Value\"";

    public async Task<long> ReservarNumeroAsync(CancellationToken cancellationToken = default) =>
        await Context.Database.SqlQueryRaw<long>(Proximo).SingleAsync(cancellationToken);

    public Task<SessaoDeAgente?> ObterPorNumeroAsync(long numero, CancellationToken cancellationToken = default) =>
        DbSet.SingleOrDefaultAsync(x => x.Numero == numero, cancellationToken);
}
