using Dante.Worker.Sessions;

namespace Dante.Worker.Telegram;

// Context of the active session (Epic #118, AD-32): /clear empties the upstream conversation without changing the
// session's agent, directory, mode, model or effort. Never sent as a prompt: the driver performs it.
public sealed partial class TelegramPollingService
{
    private async Task<string> HandleClearCommandAsync(long userId, string prompt, CancellationToken cancellationToken)
    {
        if (prompt.Length > 0) return "Uso: /clear (sem argumentos) — limpa a conversa da sessão ativa.";
        if (sessions is null) return "Sessão indisponível.";
        var active = sessions.GetActive(userId);
        if (active is null) return "Nenhuma sessão ativa para limpar. Use /session start para começar uma conversa.";
        if (active.State is AgentSessionState.Failed or AgentSessionState.Closing or AgentSessionState.Closed)
            return $"A sessão {active.Id} foi encerrada. Envie /session start para começar outra conversa.";

        // Pending attachments belong to the conversation being cleared: they are dropped only after a confirmed clear.
        var contextKey = AttachmentContext(userId);
        var result = await sessions.ClearContextAsync(userId, active.Id, cancellationToken);
        if (!result.Accepted) return result.Error ?? "Não foi possível limpar a conversa.";
        var discarded = pending?.Take(userId, contextKey);
        foreach (var item in discarded?.Items ?? []) DeleteQuietly(item);

        var session = result.Session!;
        return $"Conversa da sessão {session.Id} limpa: a próxima mensagem começa do zero com o {session.Agent}, " +
               "sem o histórico anterior.\n" +
               $"Mantidos: {session.Context.Label}, modo {AgentSessionModes.Name(session.Profile)}, " +
               $"modelo {session.ModelLabel}, esforço {session.EffortLabel}.\n" +
               $"Nova conversa upstream: {session.UpstreamSessionId}.\n" +
               (discarded is null ? "" : $"{PendingCount(discarded, "descartada(s)", "descartado(s)")} da conversa anterior.\n") +
               "Arquivos e instruções do repositório continuam disponíveis; limpar a conversa não renova as cotas de uso.";
    }
}
