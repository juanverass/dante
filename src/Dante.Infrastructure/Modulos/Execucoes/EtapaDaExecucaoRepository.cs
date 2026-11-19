using Dante.Application.Execucoes;
using Dante.Domain.Execucoes;
using Dante.Infrastructure.Data.Orquestracao;
using Dante.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dante.Infrastructure.Modulos.Execucoes;

public sealed class EtapaDaExecucaoRepository(OrquestracaoDbContext context)
    : Repository<EtapaDaExecucao>(context), IEtapaDaExecucaoRepository
{
    public async Task<IReadOnlyList<EtapaDaExecucao>> ListarDaExecucaoAsync(Guid idExecucao,
        CancellationToken cancellationToken = default) =>
        await DbSet.Where(x => x.IdExecucao == idExecucao).OrderBy(x => x.Numero).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<EtapaDaExecucao>> ListarDoPlanoAsync(Guid idPlano,
        CancellationToken cancellationToken = default) =>
        await DbSet.Where(x => x.IdPlano == idPlano).OrderBy(x => x.Numero).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<EtapaDaExecucao>> ConsultarDasExecucoesAsync(IReadOnlyCollection<Guid> idsExecucao,
        CancellationToken cancellationToken = default) =>
        idsExecucao.Count == 0 ? [] : await DbSet.AsNoTracking().Where(x => idsExecucao.Contains(x.IdExecucao))
            .OrderBy(x => x.IdExecucao).ThenBy(x => x.Numero).ToListAsync(cancellationToken);
}
