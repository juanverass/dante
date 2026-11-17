using Dante.Application.Execucoes;
using Dante.Domain.Execucoes;
using Dante.Infrastructure.Data.Orquestracao;
using Dante.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dante.Infrastructure.Modulos.Execucoes;

public sealed class ReservaDeWorktreeRepository(OrquestracaoDbContext context)
    : Repository<ReservaDeWorktree>(context), IReservaDeWorktreeRepository
{
    // Chave fixa da trava transacional de capacidade de worktrees (distinta das travas de sequência do ledger).
    internal const long ChaveDaTravaDeCapacidade = 0x44_57_54_52_45_45_30_37; // "DWTREE07"

    private static readonly EstadoDaReservaDeWorktree[] Ativas =
        [EstadoDaReservaDeWorktree.Reservada, EstadoDaReservaDeWorktree.Pronta];

    // Execuções paradas não têm escritor: só voltam a escrever reservando de novo, sob a mesma trava.
    private static readonly EstadoDaExecucao[] Paradas =
    [
        EstadoDaExecucao.Interrompida, EstadoDaExecucao.Bloqueada, EstadoDaExecucao.Concluida, EstadoDaExecucao.Falhou,
        EstadoDaExecucao.Cancelada
    ];

    public async Task<IReadOnlyList<ReservaDeWorktree>> ListarDaExecucaoAsync(Guid idExecucao,
        CancellationToken cancellationToken = default) =>
        await DbSet.Where(x => x.IdExecucao == idExecucao).OrderByDescending(x => x.Principal).ThenBy(x => x.CriadaEm)
            .ThenBy(x => x.Alias).ToListAsync(cancellationToken);

    public async Task TravarCapacidadeAsync(CancellationToken cancellationToken = default)
    {
        if (Context.Database.CurrentTransaction is null)
            throw new InvalidOperationException("A trava de capacidade de worktrees exige uma transação aberta.");
        await Context.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({ChaveDaTravaDeCapacidade})",
            cancellationToken);
    }

    public Task<int> ContarExecucoesEscrevendoAsync(string maquina, Guid excetoExecucao,
        CancellationToken cancellationToken = default) =>
        Escrevendo(excetoExecucao).Where(x => x.MaquinaDoWorker == maquina).Select(x => x.IdExecucao).Distinct()
            .CountAsync(cancellationToken);

    public Task<int> ContarExecucoesEscrevendoNoRepositorioAsync(string caminhoDoRepositorio, Guid excetoExecucao,
        CancellationToken cancellationToken = default) =>
        Escrevendo(excetoExecucao).Where(x => x.CaminhoDoRepositorio == caminhoDoRepositorio).Select(x => x.IdExecucao)
            .Distinct().CountAsync(cancellationToken);

    public async Task<IReadOnlyList<ReservaDeWorktree>> ListarDoHostAsync(string maquina,
        IReadOnlyCollection<EstadoDaReservaDeWorktree> estados, CancellationToken cancellationToken = default) =>
        await DbSet.Where(x => x.MaquinaDoWorker == maquina && estados.Contains(x.Estado)).OrderBy(x => x.CriadaEm)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<string>> ListarCaminhosDoHostAsync(string maquina,
        CancellationToken cancellationToken = default) =>
        await DbSet.AsNoTracking().Where(x => x.MaquinaDoWorker == maquina).Select(x => x.Caminho).Distinct()
            .ToListAsync(cancellationToken);

    private IQueryable<ReservaDeWorktree> Escrevendo(Guid excetoExecucao) =>
        from reserva in DbSet.AsNoTracking()
        join execucao in Context.Set<Execucao>().AsNoTracking() on reserva.IdExecucao equals execucao.Id
        where Ativas.Contains(reserva.Estado) && reserva.IdExecucao != excetoExecucao && !Paradas.Contains(execucao.Estado)
        select reserva;
}
