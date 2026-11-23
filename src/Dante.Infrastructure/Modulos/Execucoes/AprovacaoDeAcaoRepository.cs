using Dante.Application.Execucoes;
using Dante.Domain.Execucoes;
using Dante.Infrastructure.Data.Orquestracao;
using Dante.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dante.Infrastructure.Modulos.Execucoes;

public sealed class AprovacaoDeAcaoRepository(OrquestracaoDbContext context)
    : Repository<AprovacaoDeAcao>(context), IAprovacaoDeAcaoRepository
{
    public async Task<IReadOnlyList<AprovacaoDeAcao>> ListarDaExecucaoAsync(Guid idExecucao,
        CancellationToken cancellationToken = default) =>
        await DbSet.Where(x => x.IdExecucao == idExecucao).OrderBy(x => x.CriadaEm).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<AprovacaoDeAcao>> ListarEmAbertoDoDonoAsync(string canal, long idDoDono,
        CancellationToken cancellationToken = default) =>
        await DbSet.Where(x => x.CanalDoDono == canal && x.IdDoDono == idDoDono &&
                (x.Estado == EstadoDaAprovacaoDeAcao.Pendente || x.Estado == EstadoDaAprovacaoDeAcao.Aprovada))
            .OrderBy(x => x.CriadaEm).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<AprovacaoDeAcao>> ListarVencidasAsync(DateTimeOffset agora, int limite,
        CancellationToken cancellationToken = default) =>
        await DbSet.Where(x => x.ExpiraEm <= agora &&
                (x.Estado == EstadoDaAprovacaoDeAcao.Pendente || x.Estado == EstadoDaAprovacaoDeAcao.Aprovada))
            .OrderBy(x => x.ExpiraEm).Take(limite).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<AprovacaoDeAcao>> ListarEmExecucaoAsync(int limite,
        CancellationToken cancellationToken = default) =>
        await DbSet.Where(x => x.Estado == EstadoDaAprovacaoDeAcao.EmExecucao).OrderBy(x => x.IniciadaEm).Take(limite)
            .ToListAsync(cancellationToken);

    public Task<AprovacaoDeAcao?> ObterVigentePorHashAsync(string hash, CancellationToken cancellationToken = default) =>
        DbSet.Where(x => x.Hash == hash && AprovacaoDeAcaoDbMapping.Vigentes.Contains(x.Estado))
            .FirstOrDefaultAsync(cancellationToken);
}
