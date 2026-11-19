using Dante.Application.Execucoes;
using Dante.Domain.Execucoes;
using Dante.Infrastructure.Data.Orquestracao;
using Dante.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dante.Infrastructure.Modulos.Execucoes;

public sealed class DelegacaoRepository(OrquestracaoDbContext context)
    : Repository<Delegacao>(context), IDelegacaoRepository
{
    public Task<Delegacao?> ObterPorChaveAsync(Guid idExecucaoRaiz, string chaveDeDeduplicacao,
        CancellationToken cancellationToken = default) =>
        DbSet.SingleOrDefaultAsync(x => x.IdExecucaoRaiz == idExecucaoRaiz && x.ChaveDeDeduplicacao == chaveDeDeduplicacao,
            cancellationToken);

    public Task<Delegacao?> ObterDaSubordinadaAsync(Guid idExecucaoSubordinada, CancellationToken cancellationToken = default) =>
        DbSet.SingleOrDefaultAsync(x => x.IdExecucaoSubordinada == idExecucaoSubordinada, cancellationToken);

    public Task<int> ContarDaOrigemAsync(Guid idExecucaoOrigem, CancellationToken cancellationToken = default) =>
        DbSet.CountAsync(x => x.IdExecucaoOrigem == idExecucaoOrigem && x.Estado != EstadoDaDelegacao.Recusada,
            cancellationToken);

    public Task<int> ContarDaRaizAsync(Guid idExecucaoRaiz, CancellationToken cancellationToken = default) =>
        DbSet.CountAsync(x => x.IdExecucaoRaiz == idExecucaoRaiz && x.Estado != EstadoDaDelegacao.Recusada,
            cancellationToken);

    public async Task<IReadOnlyList<Delegacao>> ListarDaRaizAsync(Guid idExecucaoRaiz,
        CancellationToken cancellationToken = default) =>
        await DbSet.Where(x => x.IdExecucaoRaiz == idExecucaoRaiz).OrderBy(x => x.CriadaEm).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Delegacao>> ListarDaOrigemAsync(Guid idExecucaoOrigem,
        CancellationToken cancellationToken = default) =>
        await DbSet.Where(x => x.IdExecucaoOrigem == idExecucaoOrigem).OrderBy(x => x.CriadaEm).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Guid>> ListarOrigensProntasParaRetomarAsync(CancellationToken cancellationToken = default) =>
        await Context.Set<Execucao>().AsNoTracking()
            .Where(e => e.Estado == EstadoDaExecucao.AguardandoDelegacoes &&
                        !DbSet.Any(d => d.IdExecucaoOrigem == e.Id && NaoTerminais.Contains(d.Estado)))
            .OrderBy(e => e.Numero).Select(e => e.Id).ToListAsync(cancellationToken);

    private static readonly EstadoDaDelegacao[] NaoTerminais =
        [EstadoDaDelegacao.Solicitada, EstadoDaDelegacao.Aceita, EstadoDaDelegacao.EmAndamento];
}
