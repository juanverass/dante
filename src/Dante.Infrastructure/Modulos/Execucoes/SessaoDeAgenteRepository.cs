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

    public async Task<IReadOnlyList<SessaoDeAgente>> ListarNaoTerminaisAsync(CancellationToken cancellationToken = default) =>
        await DbSet.Where(x => x.Estado == EstadoDaSessaoDeAgente.Iniciando || x.Estado == EstadoDaSessaoDeAgente.Ativa)
            .OrderBy(x => x.Numero).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<SessaoDeAgente>> ListarPorUnidadesDeContencaoAsync(IReadOnlyCollection<string> unidades,
        CancellationToken cancellationToken = default) =>
        unidades.Count == 0 ? [] : await DbSet.Where(x => x.UnidadeDeContencao != null && unidades.Contains(x.UnidadeDeContencao))
            .ToListAsync(cancellationToken);
}
