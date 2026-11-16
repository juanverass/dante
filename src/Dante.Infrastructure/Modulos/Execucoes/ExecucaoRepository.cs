using Dante.Application.Execucoes;
using Dante.Domain.Execucoes;
using Dante.Infrastructure.Data.Orquestracao;
using Dante.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dante.Infrastructure.Modulos.Execucoes;

public sealed class ExecucaoRepository(OrquestracaoDbContext context) : Repository<Execucao>(context), IExecucaoRepository
{
    private const string Proximo =
        $"SELECT nextval('{OrquestracaoDbContext.Schema}.{OrquestracaoDbContext.SequenciaDeExecucoes}') AS \"Value\"";

    public async Task<long> ReservarNumeroAsync(CancellationToken cancellationToken = default) =>
        await Context.Database.SqlQueryRaw<long>(Proximo).SingleAsync(cancellationToken);

    public Task<Execucao?> ObterPorNumeroAsync(long numero, CancellationToken cancellationToken = default) =>
        DbSet.SingleOrDefaultAsync(x => x.Numero == numero, cancellationToken);
}
