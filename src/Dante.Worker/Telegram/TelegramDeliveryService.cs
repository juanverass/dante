using System.Net;
using System.Text;
using Dante.Worker.Sessions;

namespace Dante.Worker.Telegram;

public enum TelegramDeliveryState { Pending, Delivered, Failed }

public sealed record TelegramDeliverySnapshot(string Id, TelegramDeliveryState State, int DeliveredChunks,
    int TotalChunks);

// Delivery is independent of the agent's outcome. Records stay in memory for recovery without rerunning work.
public sealed class TelegramDeliveryService(ITelegramBotApi botApi, ILogger<TelegramDeliveryService> logger)
    : IAgentSessionEventSink
{
    private const int MaxMessageLength = 4000;
    private const int MaxRecords = 100;
    private readonly object gate = new();
    private readonly Dictionary<string, DeliveryRecord> records = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (long UserId, long ChatId, bool HideOutput)> chats =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> latestTurns = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, StringBuilder> itemText = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> toolDescriptions = new(StringComparer.OrdinalIgnoreCase);

    // Allows the race between draining a batch and clearing Scheduled to be exercised in tests.
    internal Func<Task>? BeforeScheduledDeliveryCleanupAsync { get; set; }

    // Telegram shows "typing" for about five seconds; renewing it a bit earlier keeps it continuous.
    internal TimeSpan TypingInterval { get; set; } = TimeSpan.FromSeconds(4);

    public void RegisterSession(string sessionId, long userId, long chatId, bool hideOutput)
    {
        lock (gate) chats[sessionId] = (userId, chatId, hideOutput);
    }

    // Sessions with bound secrets keep agent-provided details (including errors) out of Telegram.
    public bool HidesOutput(string sessionId)
    {
        lock (gate) return chats.TryGetValue(sessionId, out var chat) && chat.HideOutput;
    }

    public Task PublishAsync(AgentSessionSnapshot session, AgentEvent agentEvent, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!chats.TryGetValue(session.Id, out var chat)) return Task.CompletedTask;
            if (agentEvent is ErrorEvent { TurnId: null } &&
                latestTurns.TryGetValue(session.Id, out var lastId) && records.TryGetValue(lastId, out var last))
            {
                last.Final = true;
                if (last.State != TelegramDeliveryState.Failed) Schedule(last);
            }
            var id = agentEvent.TurnId is null ? session.Id + "/system" :
                session.Id + "/" + agentEvent.TurnId;
            if (agentEvent.TurnId is not null) latestTurns[session.Id] = id;
            // The active session reads like a conversation; output of any other session stays identified.
            var record = GetOrCreate(id, chat.UserId, chat.ChatId, session.IsActive ? string.Empty : $"[{session.Id}] ");
            var formatted = FormatEvent(session, agentEvent, chat.HideOutput, record);
            if (agentEvent is TurnCompletedEvent or ErrorEvent) record.Final = true;
            if (formatted.Length > 0) Append(record, formatted,
                flush: agentEvent is ApprovalRequestedEvent or UserInputRequestedEvent or RequestExpiredEvent);
            else if (record.Final && record.State != TelegramDeliveryState.Failed) Schedule(record);
            record.AwaitingUser = agentEvent is ApprovalRequestedEvent or UserInputRequestedEvent;
            if (agentEvent.TurnId is not null) StartTyping(record);
            if (agentEvent is TurnCompletedEvent or ErrorEvent)
            {
                var prefix = agentEvent.TurnId is null ? session.Id + "/" : id + "/";
                foreach (var key in itemText.Keys.Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                             .ToArray()) itemText.Remove(key);
                foreach (var key in toolDescriptions.Keys
                             .Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                             .ToArray()) toolDescriptions.Remove(key);
            }
        }
        return Task.CompletedTask;
    }

    public async Task DeliverJobAsync(string id, long userId, long chatId, string message,
        CancellationToken cancellationToken)
    {
        DeliveryRecord record;
        lock (gate)
        {
            record = GetOrCreate(id, userId, chatId, string.Empty);
            record.Final = true;
            Append(record, message, schedule: false);
        }
        await DeliverAsync(record, cancellationToken);
    }

    public TelegramDeliverySnapshot? Get(string id, long userId)
    {
        lock (gate)
        {
            var key = latestTurns.GetValueOrDefault(id) ?? id;
            return records.TryGetValue(key, out var record) && record.UserId == userId
                ? Snapshot(record) : null;
        }
    }

    public async Task<TelegramDeliverySnapshot?> RetryAsync(string id, long userId, long chatId,
        CancellationToken cancellationToken)
    {
        DeliveryRecord? record;
        lock (gate)
        {
            var key = latestTurns.GetValueOrDefault(id) ?? id;
            record = records.GetValueOrDefault(key);
            if (record?.UserId != userId || record.ChatId != chatId) return null;
            record.State = TelegramDeliveryState.Pending;
        }
        await DeliverAsync(record, cancellationToken);
        lock (gate) return Snapshot(record);
    }

    // Conversation format: the agent's own text, compact progress lines and only the lifecycle the user must act on
    // (approvals, input, interruptions, failures). Ids stay in requests, /status and output of non-active sessions.
    private string FormatEvent(AgentSessionSnapshot session, AgentEvent agentEvent, bool hideOutput,
        DeliveryRecord record)
    {
        const string omitted = "Saída omitida para proteger segredos do ambiente.";
        if (agentEvent is ErrorEvent { TurnId: null } ended && session.State == AgentSessionState.Failed)
        {
            return (hideOutput ? $"A sessão {session.Id} foi encerrada." :
                    $"A sessão {session.Id} foi encerrada: {ended.Message}") +
                "\nEnvie /session start para começar outra conversa.\n";
        }
        if (hideOutput)
        {
            return agentEvent switch
            {
                TurnCompletedEvent completed => completed.Outcome switch
                {
                    AgentTurnOutcome.Completed => $"Concluído. {omitted}\n",
                    AgentTurnOutcome.Interrupted => $"Resposta interrompida. {omitted}\n",
                    _ => $"A resposta falhou. {omitted}\n"
                },
                ApprovalRequestedEvent approval => ApprovalInstructions(approval, hideDetails: true),
                UserInputRequestedEvent input => InputInstructions(input, hideDetails: true),
                RequestExpiredEvent expired => $"A solicitação {expired.RequestId} expirou.\n",
                _ => string.Empty
            };
        }
        return agentEvent switch
        {
            MessageDeltaEvent delta => Delta(record.Id, delta),
            MessageCompletedEvent completed => CompleteMessage(record.Id, completed),
            ToolStartedEvent tool => Line(record, $"→ {Remember(record.Id, tool)}\n"),
            ToolCompletedEvent { Succeeded: false } tool => Line(record,
                $"✗ {toolDescriptions.GetValueOrDefault(record.Id + "/" + tool.ItemId) ?? "Ferramenta"} falhou.\n"),
            FileChangeEvent change => Line(record, $"Arquivos alterados: {string.Join(", ", change.Paths)}\n"),
            WarningEvent warning => Line(record, $"Aviso: {warning.Message}\n"),
            ErrorEvent error => Line(record, $"Erro: {error.Message}\n"),
            ApprovalRequestedEvent approval => ApprovalInstructions(approval, hideDetails: false),
            UserInputRequestedEvent input => InputInstructions(input, hideDetails: false),
            RequestExpiredEvent expired => Line(record, $"A solicitação {expired.RequestId} expirou.\n"),
            TurnCompletedEvent completed => completed.Outcome switch
            {
                AgentTurnOutcome.Completed => record.RetainedLength == 0 ? "(sem resposta do agente)\n" : string.Empty,
                AgentTurnOutcome.Interrupted => Line(record, "Resposta interrompida.\n"),
                _ => Line(record, "A resposta falhou" + (completed.Error is null ? ".\n" : $": {completed.Error}\n"))
            },
            _ => string.Empty
        };
    }

    private string Remember(string id, ToolStartedEvent tool)
    {
        toolDescriptions[id + "/" + tool.ItemId] = tool.Description;
        return tool.Description;
    }

    // Progress lines never glue onto streamed text that has not ended its line yet.
    private static string Line(DeliveryRecord record, string text) =>
        record.RetainedLength > 0 && !record.EndsWithNewLine ? "\n" + text : text;

    // Keeps Telegram's "typing…" visible while the agent works; it pauses while a request waits for the user.
    private void StartTyping(DeliveryRecord record)
    {
        if (record.Typing || record.Final || record.AwaitingUser) return;
        record.Typing = true;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                lock (gate)
                {
                    if (record.Final || record.AwaitingUser)
                    {
                        record.Typing = false;
                        return;
                    }
                }
                try { await botApi.SendChatActionAsync(record.ChatId, "typing", CancellationToken.None); }
                catch (Exception exception)
                {
                    // Presence is cosmetic: a failure must never affect delivery of the actual output.
                    logger.LogDebug("Falha ao indicar digitação para {DeliveryId} ({ErrorType}).", record.Id,
                        exception.GetType().Name);
                }
                await Task.Delay(TypingInterval);
            }
        });
    }

    private static string ApprovalInstructions(ApprovalRequestedEvent approval, bool hideDetails)
    {
        var ids = $"{approval.SessionId} {approval.TurnId} {approval.RequestId}";
        var details = hideDetails ? "Detalhes omitidos para proteger segredos do ambiente." :
            $"{approval.Action}" + (approval.Reason is null ? "" : $"\nMotivo: {approval.Reason}");
        return $"\nAprovação pendente {ids}: {details}\n/approve {ids}\n" +
            (approval.CanApproveForSession ? $"/approve-session {ids}\n" : "") +
            $"/deny {ids} [motivo]\nExpira em 5 minutos.\n";
    }

    private static string InputInstructions(UserInputRequestedEvent input, bool hideDetails)
    {
        var ids = $"{input.SessionId} {input.TurnId} {input.RequestId}";
        var questions = hideDetails ? "Perguntas omitidas para proteger segredos do ambiente." :
            string.Join("\n", input.Questions.Select((question, index) =>
                $"{index + 1}. {question.Text}" + (question.Options.Count == 0 ? "" :
                    $" (opções: {string.Join(", ", question.Options)})")));
        return $"\nResposta pendente {ids}:\n{questions}\n/input {ids} <resposta1>" +
            (input.Questions.Count > 1 ? " | <resposta2> ..." : "") + "\nExpira em 5 minutos.\n";
    }

    private string Delta(string id, MessageDeltaEvent delta)
    {
        var key = id + "/" + delta.ItemId;
        if (!itemText.TryGetValue(key, out var text)) itemText[key] = text = new StringBuilder();
        if (text.Length < 128_000) text.Append(delta.Text);
        return delta.Text;
    }

    private string CompleteMessage(string id, MessageCompletedEvent completed)
    {
        var key = id + "/" + completed.ItemId;
        if (!itemText.Remove(key, out var partial)) return completed.Text + "\n";
        var seen = partial.ToString();
        return (completed.Text.StartsWith(seen, StringComparison.Ordinal)
            ? completed.Text[seen.Length..] : "\n" + completed.Text) + "\n";
    }

    private DeliveryRecord GetOrCreate(string id, long userId, long chatId, string prefix)
    {
        if (records.TryGetValue(id, out var record)) return record;
        record = new DeliveryRecord(id, userId, chatId, prefix);
        records.Add(id, record);
        if (records.Count > MaxRecords)
        {
            var oldest = records.Values.Where(r => r.State != TelegramDeliveryState.Pending)
                .OrderBy(r => r.CreatedAtUtc).FirstOrDefault();
            if (oldest is not null) records.Remove(oldest.Id);
        }
        return record;
    }

    private void Append(DeliveryRecord record, string text, bool schedule = true, bool flush = false)
    {
        // Keep a bounded recent result; truncation is visible to the user.
        if (record.Truncated)
        {
            if (schedule && record.State != TelegramDeliveryState.Failed) Schedule(record);
            return;
        }
        var room = Math.Max(0, 128_000 - record.RetainedLength);
        if (text.Length > room)
        {
            text = text[..room] + "\n[saída truncada]";
            record.Truncated = true;
        }
        record.RetainedLength += text.Length;
        if (text.Length > 0) record.EndsWithNewLine = text[^1] == '\n';
        record.Buffer.Append(text);
        if (flush && record.Buffer.Length > 0)
        {
            record.Chunks.AddRange(Split(Redact(record.Buffer.ToString(), record), record.Prefix));
            record.Buffer.Clear();
        }
        record.ContentVersion++;
        if (record.State != TelegramDeliveryState.Failed)
        {
            record.State = TelegramDeliveryState.Pending;
            if (schedule) Schedule(record);
        }
    }

    private void Schedule(DeliveryRecord record)
    {
        if (!record.Scheduled)
        {
            record.Scheduled = true;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(750);
                    await DeliverAsync(record, CancellationToken.None);
                    if (BeforeScheduledDeliveryCleanupAsync is { } beforeCleanup)
                        await beforeCleanup();
                }
                catch (Exception exception)
                {
                    logger.LogWarning("Falha inesperada de entrega ao Telegram para {DeliveryId} ({ErrorType}).",
                        record.Id, exception.GetType().Name);
                    lock (gate) record.State = TelegramDeliveryState.Failed;
                }
                finally
                {
                    lock (gate)
                    {
                        record.Scheduled = false;
                        if (record.State != TelegramDeliveryState.Failed &&
                            (record.NextChunk < record.Chunks.Count ||
                             record.ContentVersion != record.DrainedVersion ||
                             record.Final && record.Buffer.Length > 0))
                            Schedule(record);
                    }
                }
            });
        }
    }

    private async Task DeliverAsync(DeliveryRecord record, CancellationToken cancellationToken)
    {
        await record.SendGate.WaitAsync(cancellationToken);
        try
        {
            while (true)
            {
                string? part;
                lock (gate)
                {
                    if (record.State == TelegramDeliveryState.Failed) return;
                    if (record.NextChunk == record.Chunks.Count && record.Buffer.Length > 0)
                    {
                        var flushLength = record.Final ? record.Buffer.Length :
                            Math.Max(0, record.Buffer.Length - SecretHoldbackLength(record));
                        if (!record.Final) flushLength = SafeFlushLength(record.Buffer.ToString(), flushLength, record);
                        if (flushLength > 0 && flushLength < record.Buffer.Length &&
                            char.IsHighSurrogate(record.Buffer[flushLength - 1])) flushLength--;
                        if (flushLength > 0)
                        {
                            var body = Redact(record.Buffer.ToString(0, flushLength), record);
                            record.Buffer.Remove(0, flushLength);
                            record.Chunks.AddRange(Split(body, record.Prefix));
                        }
                    }
                    if (record.NextChunk == record.Chunks.Count)
                    {
                        record.State = record.Buffer.Length == 0
                            ? TelegramDeliveryState.Delivered : TelegramDeliveryState.Pending;
                        record.DrainedVersion = record.ContentVersion;
                        return;
                    }
                    part = record.Chunks[record.NextChunk];
                }

                var sent = false;
                for (var attempt = 0; attempt < 4; attempt++)
                {
                    try
                    {
                        await botApi.SendMessageAsync(record.ChatId, Redact(part, record), cancellationToken);
                        sent = true;
                        break;
                    }
                    catch (Exception exception) when (IsTransient(exception, cancellationToken) && attempt < 3)
                    {
                        var backoff = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                        if (exception is TelegramRateLimitException { RetryAfter: { } retryAfter } &&
                            retryAfter > backoff)
                            backoff = retryAfter;
                        await Task.Delay(backoff, cancellationToken);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                    {
                        logger.LogWarning("Falha de entrega ao Telegram para {DeliveryId} ({ErrorType}).",
                            record.Id, exception.GetType().Name);
                        break;
                    }
                }
                var pauseForNextBatch = false;
                lock (gate)
                {
                    if (!sent)
                    {
                        record.State = TelegramDeliveryState.Failed;
                        return;
                    }
                    record.NextChunk++;
                    pauseForNextBatch = record.NextChunk == record.Chunks.Count && record.Buffer.Length > 0;
                }
                if (pauseForNextBatch) await Task.Delay(750, cancellationToken);
            }
        }
        finally { record.SendGate.Release(); }
    }

    private static bool IsTransient(Exception exception, CancellationToken cancellationToken) =>
        exception is HttpRequestException http &&
            (http.StatusCode is null or HttpStatusCode.TooManyRequests || (int)http.StatusCode >= 500) ||
        exception is TaskCanceledException && !cancellationToken.IsCancellationRequested;

    private static IEnumerable<string> Split(string text, string prefix)
    {
        for (var start = 0; start < text.Length;)
        {
            var length = Math.Min(MaxMessageLength - prefix.Length, text.Length - start);
            if (start + length < text.Length && char.IsHighSurrogate(text[start + length - 1])) length--;
            yield return prefix + text.Substring(start, length);
            start += length;
        }
    }

    private static IEnumerable<string> Secrets(DeliveryRecord record) => record.HostSecrets.Concat(
        new[] { "OPENAI_API_KEY", "ANTHROPIC_API_KEY" }
            .Select(Environment.GetEnvironmentVariable).OfType<string>())
        .Where(secret => secret.Length > 0).Distinct(StringComparer.Ordinal);

    private static string Redact(string message, DeliveryRecord record)
    {
        foreach (var value in Secrets(record))
            message = message.Replace(value, "[segredo omitido]", StringComparison.Ordinal);
        return message;
    }

    private static int SecretHoldbackLength(DeliveryRecord record)
    {
        var max = Secrets(record).Select(secret => secret.Length).DefaultIfEmpty().Max();
        return Math.Max(0, max - 1);
    }

    private static int SafeFlushLength(string text, int length, DeliveryRecord record)
    {
        foreach (var secret in Secrets(record))
        {
            for (var start = text.IndexOf(secret, StringComparison.Ordinal); start >= 0;
                start = text.IndexOf(secret, start + 1, StringComparison.Ordinal))
            {
                if (start < length && start + secret.Length > length) length = start;
            }
        }
        return length;
    }

    private static TelegramDeliverySnapshot Snapshot(DeliveryRecord record) =>
        new(record.Id, record.State, record.NextChunk,
            record.Chunks.Count + (record.Buffer.Length == 0 ? 0 :
                (record.Buffer.Length + MaxMessageLength - record.Prefix.Length - 1) /
                (MaxMessageLength - record.Prefix.Length)));

    private sealed class DeliveryRecord(string id, long userId, long chatId, string prefix)
    {
        public string Id { get; } = id;
        public long UserId { get; } = userId;
        public long ChatId { get; } = chatId;
        public string Prefix { get; } = prefix;
        public DateTimeOffset CreatedAtUtc { get; } = DateTimeOffset.UtcNow;
        public StringBuilder Buffer { get; } = new();
        public List<string> Chunks { get; } = [];
        public SemaphoreSlim SendGate { get; } = new(1, 1);
        public TelegramDeliveryState State { get; set; } = TelegramDeliveryState.Pending;
        public int NextChunk { get; set; }
        public int RetainedLength { get; set; }
        public int ContentVersion { get; set; }
        public int DrainedVersion { get; set; }
        public bool Scheduled { get; set; }
        public bool Final { get; set; }
        public bool Truncated { get; set; }
        public bool EndsWithNewLine { get; set; }
        public bool AwaitingUser { get; set; }
        public bool Typing { get; set; }
        public IReadOnlyList<string> HostSecrets { get; } = new[] { "OPENAI_API_KEY", "ANTHROPIC_API_KEY" }
            .Select(Environment.GetEnvironmentVariable).OfType<string>().Where(value => value.Length > 0).ToArray();
    }
}
