using System.Text.Json;
using Dante.Domain.Execucoes;

namespace Dante.Application.Execucoes;

// Reservas de worktree acompanham a execução (AD-71): quando ela fica terminal, suas reservas ativas deixam de ocupar
// capacidade na mesma transação da transição. A remoção física vem depois, pela limpeza, e só sem alterações.
// Interrompida e bloqueada mantêm a reserva (a worktree é o estado da retomada), mas não contam como escrevendo.
internal static class LiberacaoDeWorktrees
{
    internal static async Task LiberarSeEncerradaAsync(IReservaDeWorktreeRepository reservas,
        IEventoDeOrquestracaoRepository eventos, Execucao execucao, DateTimeOffset agora,
        CancellationToken cancellationToken)
    {
        if (!execucao.Terminal || !execucao.Nova) return;
        foreach (var reserva in (await reservas.ListarDaExecucaoAsync(execucao.Id, cancellationToken)).Where(r => r.Ativa))
        {
            reserva.Liberar(agora);
            await eventos.AdicionarAsync(Evento(execucao, reserva, TipoDeEventoDeOrquestracao.WorktreeLiberada, agora),
                cancellationToken);
        }
    }

    // Metadados apenas: alias, branch e estado. Caminho nunca vai a evento (política de eventos da AD-66).
    internal static EventoDeOrquestracao Evento(Execucao execucao, ReservaDeWorktree reserva,
        TipoDeEventoDeOrquestracao tipo, DateTimeOffset quando) =>
        EventoDeOrquestracao.DaExecucao(execucao, tipo, JsonSerializer.Serialize(new Dictionary<string, string?>
        {
            ["alias"] = reserva.Alias,
            ["branch"] = reserva.Branch,
            ["estado"] = reserva.Estado.ToString()
        }), quando);
}
