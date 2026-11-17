using Dante.Application.Comum;
using Dante.Domain.Execucoes;

namespace Dante.Application.Execucoes;

public sealed class ReservasDeWorktreeAppService(
    IFilaDeTrabalho fila,
    IExecucaoRepository execucoes,
    IEtapaDaExecucaoRepository etapas,
    IReservaDeWorktreeRepository reservas,
    IEventoDeOrquestracaoRepository eventos,
    IUnitOfWorkDaOrquestracao unidade,
    IGerenciadorDeWorktrees gerenciador,
    OpcoesDeWorktrees? opcoesDoHost = null) : IReservasDeWorktreeAppService
{
    // Sem opções do host, os limites operacionais (global e por repositório = 1).
    private readonly OpcoesDeWorktrees opcoes = opcoesDoHost ?? new OpcoesDeWorktrees();

    public async Task<IReadOnlyList<WorktreeDaExecucaoDto>> ReservarAsync(PosseDoTrabalho posse, Guid idEtapa,
        IReadOnlyList<WorktreePlanejada> planejadas, string maquina, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(planejadas);
        ArgumentException.ThrowIfNullOrWhiteSpace(maquina);
        if (planejadas.Count == 0) throw new ArgumentException("Nenhuma worktree a reservar.", nameof(planejadas));
        if (planejadas.Select(p => p.Alias).Distinct(StringComparer.OrdinalIgnoreCase).Count() != planejadas.Count ||
            planejadas.Select(p => p.Caminho).Distinct(StringComparer.OrdinalIgnoreCase).Count() != planejadas.Count)
            throw new WorktreeInvalidaException("Repositórios repetidos na mesma execução.");
        IReadOnlyList<WorktreeDaExecucaoDto> resultado = [];
        await fila.GravarComPosseAsync(posse, async token =>
        {
            var etapa = await etapas.ObterPorIdAsync(idEtapa, token) ??
                throw new InvalidOperationException("Etapa não registrada no ledger.");
            var execucao = await execucoes.ObterPorIdAsync(etapa.IdExecucao, token) ??
                throw new InvalidOperationException("Execução não registrada no ledger.");
            if (!etapa.Escrita || !execucao.Nova || execucao.Terminal || etapa.Terminal)
                throw new InvalidOperationException("Só etapa de escrita de execução nova em andamento reserva worktree.");
            // Até o commit: nenhum outro processo conta ou insere reservas entre a contagem e a inserção.
            await reservas.TravarCapacidadeAsync(token);
            var existentes = await reservas.ListarDaExecucaoAsync(execucao.Id, token);
            var ativas = existentes.Where(r => r.Ativa).ToArray();
            await ConferirCapacidadeAsync(execucao.Id, maquina, planejadas.Select(p => p.CaminhoDoRepositorio), token);
            if (ativas.Length > 0)
            {
                // Outra tentativa da mesma execução (inclusive de outra geração depois de um lease vencido): as mesmas
                // reservas, nunca uma segunda worktree. O planejamento novo precisa coincidir exatamente.
                if (!Coincidem(ativas, planejadas))
                    throw new WorktreeInvalidaException("As worktrees já reservadas para esta execução diferem das planejadas.");
                resultado = ativas.Select(WorktreeDaExecucaoDto.De).ToArray();
                return;
            }
            // Caminho de reserva encerrada (falhou, preservada…) nunca é reutilizado.
            if (existentes.Any(r => planejadas.Any(p => string.Equals(p.Caminho, r.Caminho, StringComparison.OrdinalIgnoreCase))))
                throw new WorktreeInvalidaException("Uma worktree anterior desta execução ocupa o caminho; confira-a antes de tentar de novo.");
            var agora = fila.Agora;
            var novas = planejadas.Select((p, indice) => new ReservaDeWorktree(execucao, etapa, p.Alias, indice == 0,
                p.CaminhoDoRepositorio, p.Caminho, p.Branch, p.ShaBase, maquina, agora)).ToArray();
            foreach (var reserva in novas)
            {
                await reservas.AdicionarAsync(reserva, token);
                await eventos.AdicionarAsync(LiberacaoDeWorktrees.Evento(execucao, reserva,
                    TipoDeEventoDeOrquestracao.WorktreeReservada, agora), token);
            }
            await unidade.SalvarAlteracoesAsync(token);
            resultado = novas.Select(WorktreeDaExecucaoDto.De).ToArray();
        }, null, cancellationToken: cancellationToken);
        return resultado;
    }

    public async Task<IReadOnlyList<WorktreeDaExecucaoDto>> MarcarProntasAsync(PosseDoTrabalho posse, Guid idExecucao,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<WorktreeDaExecucaoDto> resultado = [];
        await fila.GravarComPosseAsync(posse, async token =>
        {
            var execucao = await ExecucaoAsync(idExecucao, token);
            var ativas = (await reservas.ListarDaExecucaoAsync(idExecucao, token)).Where(r => r.Ativa).ToArray();
            if (ativas.Length == 0) throw new EscritaSemWorktreeException("nenhuma reserva ativa da execução");
            var agora = fila.Agora;
            foreach (var reserva in ativas.Where(r => r.Estado == EstadoDaReservaDeWorktree.Reservada))
            {
                reserva.MarcarPronta(agora);
                await eventos.AdicionarAsync(LiberacaoDeWorktrees.Evento(execucao, reserva,
                    TipoDeEventoDeOrquestracao.WorktreeCriada, agora), token);
            }
            await unidade.SalvarAlteracoesAsync(token);
            resultado = ativas.Select(WorktreeDaExecucaoDto.De).ToArray();
        }, null, cancellationToken: cancellationToken);
        return resultado;
    }

    public Task FalharAsync(PosseDoTrabalho posse, Guid idExecucao, string alias, string motivo,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(motivo);
        return fila.GravarComPosseAsync(posse, async token =>
        {
            var execucao = await ExecucaoAsync(idExecucao, token);
            var agora = fila.Agora;
            foreach (var reserva in (await reservas.ListarDaExecucaoAsync(idExecucao, token)).Where(r => r.Ativa))
            {
                if (string.Equals(reserva.Alias, alias, StringComparison.OrdinalIgnoreCase))
                {
                    reserva.Falhar(motivo, agora);
                    await eventos.AdicionarAsync(LiberacaoDeWorktrees.Evento(execucao, reserva,
                        TipoDeEventoDeOrquestracao.WorktreeFalhou, agora), token);
                }
                else
                {
                    reserva.Liberar(agora);
                    await eventos.AdicionarAsync(LiberacaoDeWorktrees.Evento(execucao, reserva,
                        TipoDeEventoDeOrquestracao.WorktreeLiberada, agora), token);
                }
            }
            await unidade.SalvarAlteracoesAsync(token);
        }, null, cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyList<WorktreeDaExecucaoDto>> ListarDaExecucaoAsync(Guid idExecucao,
        CancellationToken cancellationToken = default) =>
        (await reservas.ListarDaExecucaoAsync(idExecucao, cancellationToken)).Select(WorktreeDaExecucaoDto.De).ToArray();

    public async Task<RelatorioDeWorktreesDto> LimparAsync(string maquina, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(maquina);
        var liberadas = 0;
        // Coerência: reserva ativa de execução encerrada (a transição terminal normalmente já a liberou).
        foreach (var reserva in await reservas.ListarDoHostAsync(maquina,
                     [EstadoDaReservaDeWorktree.Reservada, EstadoDaReservaDeWorktree.Pronta], cancellationToken))
        {
            var execucao = await execucoes.ObterPorIdAsync(reserva.IdExecucao, cancellationToken);
            if (execucao is not { Terminal: true }) continue;
            if (await GravarAsync(() =>
                {
                    reserva.Liberar(fila.Agora);
                    return eventos.AdicionarAsync(LiberacaoDeWorktrees.Evento(execucao, reserva,
                        TipoDeEventoDeOrquestracao.WorktreeLiberada, fila.Agora), cancellationToken);
                }, cancellationToken)) liberadas++;
        }
        var (removidas, preservadas, falhas) = (0, 0, 0);
        foreach (var reserva in await reservas.ListarDoHostAsync(maquina, [EstadoDaReservaDeWorktree.Liberada],
                     cancellationToken))
        {
            DesfechoDaLimpeza desfecho;
            try
            {
                desfecho = await gerenciador.RemoverSePossivelAsync(new WorktreePlanejada(reserva.Alias,
                    reserva.CaminhoDoRepositorio, reserva.Caminho, reserva.Branch, reserva.ShaBase), cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Remoção que falhou (git indisponível, prazo): continua liberada e a próxima passada tenta de novo.
                falhas++;
                continue;
            }
            var execucao = await ExecucaoAsync(reserva.IdExecucao, cancellationToken);
            var gravada = await GravarAsync(() =>
            {
                var agora = fila.Agora;
                if (desfecho.Removida) reserva.MarcarRemovida(agora);
                else reserva.Preservar(desfecho.MotivoDaPreservacao ?? "Worktree preservada.", agora);
                return eventos.AdicionarAsync(LiberacaoDeWorktrees.Evento(execucao, reserva, desfecho.Removida
                    ? TipoDeEventoDeOrquestracao.WorktreeRemovida : TipoDeEventoDeOrquestracao.WorktreePreservada, agora),
                    cancellationToken);
            }, cancellationToken);
            if (!gravada) falhas++;
            else if (desfecho.Removida) removidas++;
            else preservadas++;
        }
        var desconhecidos = gerenciador.ListarDiretoriosDesconhecidos(
            await reservas.ListarCaminhosDoHostAsync(maquina, cancellationToken));
        return new RelatorioDeWorktreesDto(liberadas, removidas, preservadas, falhas, desconhecidos);
    }

    private async Task ConferirCapacidadeAsync(Guid idExecucao, string maquina, IEnumerable<string> repositorios,
        CancellationToken cancellationToken)
    {
        if (await reservas.ContarExecucoesEscrevendoAsync(maquina, idExecucao, cancellationToken) >= opcoes.LimiteGlobal)
            throw new CapacidadeDeWorktreesIndisponivelException(
                $"Limite global de execuções de escrita atingido ({opcoes.LimiteGlobal}).");
        foreach (var repositorio in repositorios.Distinct(StringComparer.OrdinalIgnoreCase))
            if (await reservas.ContarExecucoesEscrevendoNoRepositorioAsync(repositorio, idExecucao, cancellationToken) >=
                opcoes.LimitePorRepositorio)
                throw new CapacidadeDeWorktreesIndisponivelException(
                    $"Limite de execuções de escrita por repositório atingido ({opcoes.LimitePorRepositorio}).");
    }

    private static bool Coincidem(IReadOnlyCollection<ReservaDeWorktree> ativas, IReadOnlyList<WorktreePlanejada> planejadas) =>
        ativas.Count == planejadas.Count && planejadas.Select((p, indice) => (p, indice)).All(par => ativas.Any(r =>
            string.Equals(r.Alias, par.p.Alias, StringComparison.OrdinalIgnoreCase) && r.Principal == (par.indice == 0) &&
            r.Caminho == par.p.Caminho && r.CaminhoDoRepositorio == par.p.CaminhoDoRepositorio && r.Branch == par.p.Branch));

    private async Task<Execucao> ExecucaoAsync(Guid idExecucao, CancellationToken cancellationToken) =>
        await execucoes.ObterPorIdAsync(idExecucao, cancellationToken) ??
        throw new InvalidOperationException("Execução não registrada no ledger.");

    // Fora de posse (execução já encerrada): xmin decide; conflito significa que outra passada já gravou.
    private async Task<bool> GravarAsync(Func<Task> alteracao, CancellationToken cancellationToken)
    {
        try
        {
            await alteracao();
            await unidade.SalvarAlteracoesAsync(cancellationToken);
            return true;
        }
        catch (ConflitoDeConcorrenciaException)
        {
            return false;
        }
    }
}
