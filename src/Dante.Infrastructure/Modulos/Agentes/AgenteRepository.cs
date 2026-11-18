using Dante.Application.Agentes;
using Dante.Domain.Agentes;
using Dante.Domain.Execucoes;
using Dante.Infrastructure.Data.Orquestracao;
using Dante.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dante.Infrastructure.Modulos.Agentes;

public sealed class AgenteRepository(OrquestracaoDbContext context) : Repository<Agente>(context), IAgenteRepository
{
    private static readonly EstadoDaExecucao[] ExecucoesTerminais =
        [EstadoDaExecucao.Concluida, EstadoDaExecucao.Falhou, EstadoDaExecucao.Cancelada];

    private static readonly EstadoDaSessaoDeAgente[] SessoesTerminais =
        [EstadoDaSessaoDeAgente.Encerrada, EstadoDaSessaoDeAgente.Falhou, EstadoDaSessaoDeAgente.Interrompida];

    public Task<Agente?> ObterPorHandleAsync(string handle, CancellationToken cancellationToken = default) =>
        DbSet.SingleOrDefaultAsync(x => x.Handle == handle, cancellationToken);

    public async Task<IReadOnlyList<Agente>> ListarAsync(bool incluirArquivados,
        CancellationToken cancellationToken = default) =>
        await DbSet.Where(x => incluirArquivados || x.Estado != EstadoDoAgente.Arquivado)
            .OrderBy(x => x.Handle).ToListAsync(cancellationToken);

    public async Task<int> ContarTrabalhoAtivoAsync(Guid idAgente, CancellationToken cancellationToken = default)
    {
        var versoes = Context.Set<VersaoDoAgente>().Where(x => x.IdAgente == idAgente).Select(x => (Guid?)x.Id);
        var execucoes = await Context.Set<Execucao>()
            .CountAsync(x => versoes.Contains(x.IdVersaoDoAgente) && !ExecucoesTerminais.Contains(x.Estado), cancellationToken);
        // Roteiros (#308) em que o agente participa com alguma das versões congeladas.
        var roteiros = await (from participante in Context.Set<ParticipanteDaExecucao>()
                join execucao in Context.Set<Execucao>() on participante.IdExecucao equals execucao.Id
                where versoes.Contains(participante.IdVersaoDoAgente) && !ExecucoesTerminais.Contains(execucao.Estado)
                select execucao.Id).Distinct().CountAsync(cancellationToken);
        var sessoes = await Context.Set<SessaoDeAgente>()
            .CountAsync(x => versoes.Contains(x.IdVersaoDoAgente) && !SessoesTerminais.Contains(x.Estado), cancellationToken);
        return execucoes + roteiros + sessoes;
    }
}
