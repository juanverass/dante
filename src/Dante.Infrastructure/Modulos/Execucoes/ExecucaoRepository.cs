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

    public Task<Execucao?> ConsultarPorNumeroAsync(long numero, CancellationToken cancellationToken = default) =>
        DbSet.AsNoTracking().SingleOrDefaultAsync(x => x.Numero == numero, cancellationToken);

    public async Task<Execucao?> ObterParaAtualizarAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (Context.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Travar a execução exige uma transação.");
        return (await DbSet.FromSql($"SELECT *, xmin FROM orquestracao.execucoes WHERE id = {id} FOR UPDATE")
            .ToListAsync(cancellationToken)).SingleOrDefault();
    }

    public async Task<Execucao?> TravarComAncestraisAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (Context.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Travar a execução exige uma transação.");
        // A hierarquia é imutável (AD-70): ler o pai sem trava é seguro, e a raiz é travada primeiro.
        var pai = await DbSet.AsNoTracking().Where(x => x.Id == id).Select(x => x.IdExecucaoPai)
            .SingleOrDefaultAsync(cancellationToken);
        if (pai is { } idPai) await TravarComAncestraisAsync(idPai, cancellationToken);
        return await ObterParaAtualizarAsync(id, cancellationToken);
    }

    public async Task<IReadOnlyList<Execucao>> ListarDoProprietarioAsync(string canal, long idDoUsuarioNoCanal,
        int limiteDeEncerradas, CancellationToken cancellationToken = default)
    {
        var doDono = DbSet.AsNoTracking().Where(x => x.Canal == canal && x.IdDoUsuarioNoCanal == idDoUsuarioNoCanal);
        var ativas = await doDono.Where(x => !Terminais.Contains(x.Estado))
            .OrderByDescending(x => x.CriadaEm).ThenByDescending(x => x.Numero).ToListAsync(cancellationToken);
        var encerradas = limiteDeEncerradas <= 0 ? [] : await doDono.Where(x => Terminais.Contains(x.Estado))
            .OrderByDescending(x => x.CriadaEm).ThenByDescending(x => x.Numero).Take(limiteDeEncerradas)
            .ToListAsync(cancellationToken);
        return [.. ativas.Concat(encerradas).OrderByDescending(x => x.CriadaEm).ThenByDescending(x => x.Numero)];
    }

    private static readonly EstadoDaExecucao[] Terminais =
        [EstadoDaExecucao.Concluida, EstadoDaExecucao.Falhou, EstadoDaExecucao.Cancelada];
}
