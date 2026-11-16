using Dante.Application.Execucoes;
using Dante.Domain.Execucoes;
using Dante.Infrastructure.Data.Orquestracao;
using Dante.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dante.Infrastructure.Modulos.Execucoes;

public sealed class ExecucaoRepository(OrquestracaoDbContext context) : Repository<Execucao>(context), IExecucaoRepository
{
    public Task<long> ReservarNumeroAsync(long acimaDe = 0, CancellationToken cancellationToken = default) =>
        SequenciasDoLedger.ReservarAsync(Context, OrquestracaoDbContext.SequenciaDeExecucoes, acimaDe, cancellationToken);

    public Task<Execucao?> ObterPorNumeroAsync(long numero, CancellationToken cancellationToken = default) =>
        DbSet.SingleOrDefaultAsync(x => x.Numero == numero, cancellationToken);
}
