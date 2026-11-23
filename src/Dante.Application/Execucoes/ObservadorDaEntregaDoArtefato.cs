using Dante.Domain.Execucoes;

namespace Dante.Application.Execucoes;

// Estado real da worktree de uma entrega que ficou Incerta (#311, PR 3): HEAD, commits além da base e o index, lidos só
// por consulta. É o que vai ao ledger e ao aviso no lugar de "efeito desconhecido"; nada é repetido nem desfeito.
public sealed class ObservadorDaEntregaDoArtefato(IReservaDeWorktreeRepository reservas, ICommitDeWorktree? commits = null)
    : IObservadorDeAcaoProtegida
{
    public DefinicaoDeAcao Acao => CatalogoDeAcoes.EntregarArtefatoDoRoteiro;

    public async Task<string?> ObservarAsync(AprovacaoDeAcao aprovacao, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(aprovacao);
        if (commits is null) return null;
        var reserva = (await reservas.ListarDaExecucaoAsync(aprovacao.IdExecucao, cancellationToken))
            .SingleOrDefault(r => r.Principal);
        return reserva is null ? "Worktree da execução não registrada."
            : await commits.DescreverEstadoAsync(WorktreeDaExecucaoDto.De(reserva).Planejada, cancellationToken);
    }
}
