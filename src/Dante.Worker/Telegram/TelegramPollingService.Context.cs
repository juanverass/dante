using System.Globalization;
using Dante.Worker.Sessions;

namespace Dante.Worker.Telegram;

// Context of the active session (Epic #118, AD-32): /clear empties the upstream conversation and /compact replaces it
// with the agent's own summary, both without changing the session's agent, directory, mode, model or effort. Never sent
// as a prompt: the driver performs them.
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

    // /compact (#121): the start is acknowledged at once and the outcome arrives in a second message when the agent
    // confirms or refuses it, so a long compaction never holds the other updates.
    private async Task HandleCompactCommandAsync(TelegramMessage message, string prompt, CancellationToken cancellationToken)
    {
        var userId = message.From!.Id;
        var chatId = message.Chat.Id;
        string? refusal = prompt.Length > 0 ? "Uso: /compact (sem argumentos) — compacta a conversa da sessão ativa." :
            sessions is null ? "Sessão indisponível." : null;
        var active = refusal is null ? sessions!.GetActive(userId) : null;
        refusal ??= active is null
            ? "Nenhuma sessão ativa para compactar. Use /session start para começar uma conversa."
            : active.State is AgentSessionState.Failed or AgentSessionState.Closing or AgentSessionState.Closed
                ? $"A sessão {active.Id} foi encerrada. Envie /session start para começar outra conversa."
                : null;
        if (refusal is not null)
        {
            await SendReplyAsync(chatId, refusal, cancellationToken);
            return;
        }

        var compaction = await sessions!.CompactContextAsync(userId, active!.Id, cancellationToken);
        if (!compaction.Started.Accepted)
        {
            await SendReplyAsync(chatId, compaction.Started.Error ?? "Não foi possível compactar a conversa.",
                cancellationToken);
            return;
        }

        await SendReplyAsync(chatId, $"Compactando a conversa da sessão {active.Id} com o {active.Agent}… Aviso quando " +
            "terminar. Até lá, mensagens para esta sessão são recusadas; /session stop cancela a compactação.",
            cancellationToken);
        var task = NotifyCompactionAsync(chatId, active, compaction.Completion!, cancellationToken);
        lock (runningGate) runningJobs.Add(task);
        _ = task.ContinueWith(completed =>
        {
            lock (runningGate) runningJobs.Remove(completed);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task NotifyCompactionAsync(long chatId, AgentSessionSnapshot session,
        Task<SessionCompactionResult> completion, CancellationToken cancellationToken)
    {
        var result = await completion;
        var portuguese = CultureInfo.GetCultureInfo("pt-BR");
        var text = result.Compacted
            ? $"Conversa da sessão {session.Id} compactada: o {session.Agent} segue a mesma conversa a partir de um " +
              "resumo do que foi feito e das instruções; agente, contexto, modo, modelo e esforço não mudaram.\n" +
              (result is { PreTokens: { } pre, PostTokens: { } post }
                  ? $"Contexto informado pelo {session.Agent}: {pre.ToString("N0", portuguese)} → " +
                    $"{post.ToString("N0", portuguese)} tokens.\n"
                  : $"O {session.Agent} não informa o tamanho do contexto antes e depois da compactação.\n") +
              "Compactar não apaga a conversa (para isso, /clear) nem renova as cotas de uso."
            : $"Compactação da sessão {session.Id} não concluída: {result.Error}";
        try
        {
            await SendReplyAsync(chatId, text, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Falha ao avisar o fim da compactação ({ErrorType}).", exception.GetType().Name);
        }
    }
}
