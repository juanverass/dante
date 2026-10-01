using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Dante.Worker.Sessions;

namespace Dante.Worker.Telegram;

public enum TelegramDeliveryState { Pending, Delivered, Failed }

public sealed record TelegramDeliverySnapshot(string Id, TelegramDeliveryState State, int DeliveredChunks,
    int TotalChunks);

// Delivery is independent of the agent's outcome. Records stay in memory for recovery without rerunning work.
public sealed partial class TelegramDeliveryService(ITelegramBotApi botApi, ILogger<TelegramDeliveryService> logger)
    : IAgentSessionEventSink
{
    private const int MaxMessageLength = 4000;
    private const int MaxRecords = 100;
    private static readonly TimeSpan BatchDelay = TimeSpan.FromMilliseconds(750);
    private readonly object gate = new();
    private readonly Dictionary<string, DeliveryRecord> records = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (long UserId, long ChatId, bool HideOutput)> chats =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> latestTurns = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, StringBuilder> itemText = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> toolDescriptions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, string> activeSessions = [];
    private readonly Dictionary<string, ApprovalMessage> approvals = new(StringComparer.OrdinalIgnoreCase);

    public bool OwnsApprovalMessage(string requestId, long userId, long chatId, long messageId)
    {
        lock (gate) return approvals.TryGetValue(requestId, out var approval) &&
            approval.Record.UserId == userId && approval.Record.ChatId == chatId &&
            approval.MessageId == messageId && messageId > 0 && approval.Status is null;
    }

    // Allows the race between draining a batch and clearing Scheduled to be exercised in tests.
    internal Func<Task>? BeforeScheduledDeliveryCleanupAsync { get; set; }

    // Telegram shows "typing" for about five seconds; renewing it a bit earlier keeps it continuous.
    internal TimeSpan TypingInterval { get; set; } = TimeSpan.FromSeconds(4);

    // Telegram asks bots to stay around one message per second per chat: consecutive parts of the same session turn are
    // sent at least this far apart, so a long stream arrives in fewer, larger parts. A job's final result is unchanged.
    internal TimeSpan PartInterval { get; set; } = TimeSpan.FromSeconds(1.5);

    public void RegisterSession(string sessionId, long userId, long chatId, bool hideOutput)
    {
        lock (gate) chats[sessionId] = (userId, chatId, hideOutput);
    }

    // The user's current conversation, as selected in the SessionRegistry. Identification is decided when each part is
    // sent, so output of a session that stopped being active mid-turn is prefixed from then on.
    public void SetActiveSession(long userId, string? sessionId)
    {
        lock (gate)
        {
            if (sessionId is null) activeSessions.Remove(userId);
            else activeSessions[userId] = sessionId;
        }
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
            var record = GetOrCreate(id, chat.UserId, chat.ChatId, session.Id);
            if (agentEvent is RequestResolvedEvent resolved && approvals.TryGetValue(resolved.RequestId, out var answered))
                ResolveApproval(answered, TelegramApprovalCallback.DecisionText(resolved.Decision));
            if (agentEvent is RequestExpiredEvent expired && approvals.TryGetValue(expired.RequestId, out var timedOut))
                ResolveApproval(timedOut, "⌛ Solicitação expirada");
            if (agentEvent is TurnCompletedEvent or ErrorEvent)
                foreach (var approval in approvals.Values.Where(a => a.Record.SessionId == session.Id && a.Status is null))
                    ResolveApproval(approval, "Solicitação encerrada.");
            var formatted = FormatEvent(session, agentEvent, chat.HideOutput, record);
            if (agentEvent is TurnCompletedEvent or ErrorEvent) record.Final = true;
            if (agentEvent is ApprovalRequestedEvent request)
            {
                // Keep each keyboard on its own request, even when several approvals arrive in the same batch.
                FlushBuffer(record);
                // Actionable requests remain deliverable after ordinary output has reached its retention limit.
                record.Chunks.AddRange(new TelegramMessageFormatter().Format(Redact(formatted, record), record.PrefixReserve + 80));
                record.ContentVersion++;
                if (record.State != TelegramDeliveryState.Failed)
                {
                    record.State = TelegramDeliveryState.Pending;
                    Schedule(record);
                }
                var approval = new ApprovalMessage(record, TelegramApprovalCallback.Keyboard(request));
                approvals[request.RequestId] = approval;
                record.ApprovalChunks[record.Chunks.Count - 1] = approval;
            }
            else if (agentEvent is ToolStartedEvent { Kind: AgentToolKind.Command } tool && !chat.HideOutput &&
                     !record.Truncated &&
                     Unwrapped(Relative(session, tool.Description)) is { } command &&
                     (command.Contains('\n') || command.Length > 200))
            {
                var room = Math.Max(0, 128_000 - record.RetainedLength);
                if (command.Length > room)
                {
                    command = command[..room] + "\n[saída truncada]";
                    record.Truncated = true;
                }
                record.RetainedLength += command.Length;
                record.PendingCommands.Enqueue(new PendingCommand(record.BufferOffset + record.Buffer.Length, command));
                FlushBuffer(record);
                record.HasVisibleText = true;
                record.ContentVersion++;
                if (record.State != TelegramDeliveryState.Failed)
                {
                    record.State = TelegramDeliveryState.Pending;
                    Schedule(record);
                }
            }
            else if (formatted.Length > 0) Append(record, formatted,
                flush: agentEvent is UserInputRequestedEvent or RequestExpiredEvent);
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
            record = GetOrCreate(id, userId, chatId, sessionId: null);
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
                ApprovalRequestedEvent approval => Line(record, ApprovalInstructions(approval, hideDetails: true)),
                UserInputRequestedEvent input => Line(record, InputInstructions(input, hideDetails: true)),
                RequestExpiredEvent expired => $"A solicitação {expired.RequestId} expirou.\n",
                _ => string.Empty
            };
        }
        return agentEvent switch
        {
            MessageDeltaEvent delta => Delta(record.Id, delta),
            MessageCompletedEvent completed => CompleteMessage(record.Id, completed),
            ToolStartedEvent tool => Line(record,
                $"→ {Remember(record.Id, Unwrapped(Relative(session, tool.Description)), tool)}\n"),
            ToolCompletedEvent { Succeeded: false } tool => Line(record,
                $"✗ {toolDescriptions.GetValueOrDefault(record.Id + "/" + tool.ItemId) ?? "Ferramenta"} falhou.\n"),
            FileChangeEvent change => Line(record,
                $"Arquivos alterados: {Relative(session, string.Join(", ", change.Paths))}\n"),
            WarningEvent warning => Line(record, $"Aviso: {warning.Message}\n"),
            ErrorEvent error => Line(record, $"Erro: {error.Message}\n"),
            ApprovalRequestedEvent approval => Line(record,
                ApprovalInstructions(approval with { Action = Relative(session, approval.Action) }, hideDetails: false)),
            UserInputRequestedEvent input => Line(record, InputInstructions(input, hideDetails: false)),
            RequestExpiredEvent expired => Line(record, $"A solicitação {expired.RequestId} expirou.\n"),
            TurnCompletedEvent completed => completed.Outcome switch
            {
                AgentTurnOutcome.Completed => record.HasVisibleText ? string.Empty : "(sem resposta do agente)\n",
                AgentTurnOutcome.Interrupted => Line(record, "Resposta interrompida.\n"),
                _ => Line(record, "A resposta falhou" + (completed.Error is null ? ".\n" : $": {completed.Error}\n"))
            },
            _ => string.Empty
        };
    }

    private string Remember(string id, string description, ToolStartedEvent tool)
    {
        toolDescriptions[id + "/" + tool.ItemId] = description;
        return description;
    }

    // Progress lines show the command itself, not Codex's "/bin/bash -lc '...'" wrapper. Approvals keep the exact text.
    private static string Unwrapped(string command) =>
        ShellWrapper().Match(command) is { Success: true } match ? match.Groups[1].Value : command;

    [GeneratedRegex(@"^(?:/usr)?/bin/(?:ba)?sh -lc '([^']*)'$")]
    private static partial Regex ShellWrapper();

    // Paths inside the session's directory are shown relative to it: shorter on a phone, same meaning.
    private static string Relative(AgentSessionSnapshot session, string text)
    {
        var root = session.Context.WorkingDirectory.TrimEnd('/', '\\');
        return root.Length == 0 ? text : text.Replace(root + "/", string.Empty, StringComparison.Ordinal)
            .Replace(root + "\\", string.Empty, StringComparison.Ordinal);
    }

    // Progress lines never glue onto streamed text that has not ended its line yet.
    private static string Line(DeliveryRecord record, string text) =>
        record.RetainedLength > 0 && !record.EndsWithNewLine ? "\n" + text : text;

    // The active session reads like a conversation; output of any other session stays identified.
    private string PrefixFor(DeliveryRecord record) =>
        record.SessionId is null || IsActive(record) ? string.Empty : $"[{record.SessionId}] ";

    private bool IsActive(DeliveryRecord record) =>
        record.SessionId is not null && activeSessions.TryGetValue(record.UserId, out var active) &&
        string.Equals(active, record.SessionId, StringComparison.OrdinalIgnoreCase);

    // Keeps Telegram's "typing…" visible while the active conversation works. "typing" belongs to the whole chat, so it
    // never runs for another session; it pauses while a request waits for the user.
    private void StartTyping(DeliveryRecord record)
    {
        if (record.Typing || record.Final || record.AwaitingUser || !IsActive(record)) return;
        record.Typing = true;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                lock (gate)
                {
                    if (record.Final || record.AwaitingUser || !IsActive(record))
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
        return $"Aprovação pendente {ids}: {details}\n/approve {ids}\n" +
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
        return $"Resposta pendente {ids}:\n{questions}\n/input {ids} <resposta1>" +
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

    private DeliveryRecord GetOrCreate(string id, long userId, long chatId, string? sessionId)
    {
        if (records.TryGetValue(id, out var record)) return record;
        record = new DeliveryRecord(id, userId, chatId, sessionId);
        records.Add(id, record);
        if (records.Count > MaxRecords)
        {
            var oldest = records.Values.Where(r => r.State != TelegramDeliveryState.Pending)
                .OrderBy(r => r.CreatedAtUtc).FirstOrDefault();
            if (oldest is not null)
            {
                records.Remove(oldest.Id);
                foreach (var key in approvals.Where(pair => pair.Value.Record == oldest).Select(pair => pair.Key).ToArray())
                    approvals.Remove(key);
            }
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
        if (!string.IsNullOrWhiteSpace(text)) record.HasVisibleText = true;
        record.Buffer.Append(text);
        if (flush) FlushBuffer(record);
        record.ContentVersion++;
        if (record.State != TelegramDeliveryState.Failed)
        {
            record.State = TelegramDeliveryState.Pending;
            if (schedule) Schedule(record);
        }
    }

    private void FlushBuffer(DeliveryRecord record)
    {
        // Commands keep their position in the textual stream. A partial secret may postpone that boundary until
        // its continuation arrives; only the complete redacted value can then be emitted before the command.
        while (record.PendingCommands.TryPeek(out var command))
        {
            var text = record.Buffer.ToString();
            var safeLength = SafeBufferLength(record, text);
            var boundary = Math.Max(0, command.Position - record.BufferOffset);
            foreach (var secret in Secrets(record))
            {
                for (var start = text.IndexOf(secret, StringComparison.Ordinal); start >= 0;
                    start = text.IndexOf(secret, start + 1, StringComparison.Ordinal))
                {
                    if (start < boundary && start + secret.Length > boundary) boundary = start + secret.Length;
                }
            }
            if (boundary > 0 && boundary <= text.Length && char.IsHighSurrogate(text[boundary - 1]))
            {
                if (boundary == text.Length && !record.Final) break;
                if (boundary < text.Length && char.IsLowSurrogate(text[boundary])) boundary++;
            }
            if (boundary > safeLength) break;
            FlushText(record, boundary);
            record.PendingCommands.Dequeue();
            record.Chunks.AddRange(TelegramMessageFormatter.Command(Redact(command.Text, record), record.PrefixReserve));
        }
        var remaining = record.Buffer.ToString();
        var length = SafeBufferLength(record, remaining);
        if (!record.Final) length = LineBoundary(remaining, length, MaxMessageLength - record.PrefixReserve);
        FlushText(record, length);
    }

    private static int SafeBufferLength(DeliveryRecord record, string text) => record.Final ? text.Length :
        SafeFlushLength(text, text.Length - SecretHoldbackLength(record, text), record);

    private static void FlushText(DeliveryRecord record, int length)
    {
        if (length > 0 && (length < record.Buffer.Length || !record.Final) &&
            char.IsHighSurrogate(record.Buffer[length - 1])) length--;
        if (length == 0) return;
        var body = Redact(record.Buffer.ToString(0, length), record);
        record.Chunks.AddRange(record.Formatter.Format(body, record.PrefixReserve));
        record.Buffer.Remove(0, length);
        record.BufferOffset += length;
    }

    private sealed record PendingCommand(int Position, string Text);

    private void ResolveApproval(ApprovalMessage approval, string status)
    {
        approval.Status = status;
        _ = RefreshApprovalAsync(approval);
    }

    private async Task RefreshApprovalAsync(ApprovalMessage approval)
    {
        // Serialize with sending: expiry can happen before Telegram has returned the message id.
        await approval.Record.SendGate.WaitAsync();
        try
        {
            if (approval.MessageId is not { } messageId || approval.Status is not { } status) return;
            for (var attempt = 0; attempt < 4; attempt++)
            {
                try
                {
                    var text = approval.SentText!.TrimEnd() + "\n\n" + status;
                    await botApi.EditApprovalAsync(approval.Record.ChatId, messageId,
                        Redact(text, approval.Record), CancellationToken.None);
                    return;
                }
                catch (Exception exception) when (IsTransient(exception, CancellationToken.None) && attempt < 3)
                {
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                    if (exception is TelegramRateLimitException { RetryAfter: { } retry } && retry > delay) delay = retry;
                    await Task.Delay(delay);
                }
                catch (Exception exception)
                {
                    logger.LogDebug("Falha ao atualizar aprovação ({ErrorType}).", exception.GetType().Name);
                    return;
                }
            }
        }
        finally { approval.Record.SendGate.Release(); }
    }

    private sealed class ApprovalMessage(DeliveryRecord record, TelegramInlineKeyboard keyboard)
    {
        public DeliveryRecord Record { get; } = record;
        public TelegramInlineKeyboard Keyboard { get; } = keyboard;
        public long? MessageId { get; set; }
        public string? SentText { get; set; }
        public string? Status { get; set; }
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
                    await Task.Delay(BatchDelay);
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
                             record.Final && (record.Buffer.Length > 0 || record.PendingCommands.Count > 0)))
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
                TimeSpan wait;
                lock (gate)
                {
                    wait = record.LastSentUtc + PartInterval - DateTimeOffset.UtcNow;
                    if (record.SessionId is null ||
                        record.NextChunk == record.Chunks.Count && record.Buffer.Length == 0) wait = TimeSpan.Zero;
                }
                // Also waiting before a flush lets more of the stream join the next part.
                if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellationToken);

                TelegramFormattedMessage part;
                ApprovalMessage? approval;
                lock (gate)
                {
                    if (record.State == TelegramDeliveryState.Failed) return;
                    if (record.NextChunk == record.Chunks.Count &&
                        (record.Buffer.Length > 0 || record.PendingCommands.Count > 0)) FlushBuffer(record);
                    if (record.NextChunk == record.Chunks.Count)
                    {
                        record.State = record.Buffer.Length == 0
                            ? TelegramDeliveryState.Delivered : TelegramDeliveryState.Pending;
                        record.DrainedVersion = record.ContentVersion;
                        return;
                    }
                    part = record.Chunks[record.NextChunk].WithPrefix(PrefixFor(record));
                    approval = record.ApprovalChunks.GetValueOrDefault(record.NextChunk);
                    if (approval?.Status is { } status)
                        part = new(part.Html + "\n" + WebUtility.HtmlEncode(status), part.PlainText + "\n" + status);
                }

                var sent = false;
                for (var attempt = 0; attempt < 4; attempt++)
                {
                    try
                    {
                        var safeText = Redact(part.PlainText, record);
                        var safePart = safeText == part.PlainText ? part :
                            new TelegramFormattedMessage(WebUtility.HtmlEncode(safeText), safeText);
                        long? messageId;
                        try
                        {
                            messageId = await SendPartAsync(record, safePart, approval, cancellationToken);
                        }
                        catch (TelegramMarkupException)
                        {
                            logger.LogDebug("Formatação recusada para {DeliveryId}; usando texto simples.", record.Id);
                            record.PlainChunks.Add(record.NextChunk);
                            messageId = await SendPartAsync(record, safePart, approval, cancellationToken);
                        }
                        if (approval is not null)
                        {
                            lock (gate)
                            {
                                approval.MessageId = messageId;
                                approval.SentText = safePart.PlainText;
                            }
                            if (approval.Status is not null) _ = RefreshApprovalAsync(approval);
                        }
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
                lock (gate)
                {
                    if (!sent)
                    {
                        record.State = TelegramDeliveryState.Failed;
                        return;
                    }
                    record.NextChunk++;
                    record.LastSentUtc = DateTimeOffset.UtcNow;
                }
            }
        }
        finally { record.SendGate.Release(); }
    }

    private static bool IsTransient(Exception exception, CancellationToken cancellationToken) =>
        exception is HttpRequestException http &&
            (http.StatusCode is null or HttpStatusCode.TooManyRequests || (int)http.StatusCode >= 500) ||
        exception is TaskCanceledException && !cancellationToken.IsCancellationRequested;

    private async Task<long?> SendPartAsync(DeliveryRecord record, TelegramFormattedMessage part,
        ApprovalMessage? approval, CancellationToken cancellationToken)
    {
        var keyboard = approval?.Status is null ? approval?.Keyboard : null;
        if (!record.PlainChunks.Contains(record.NextChunk))
            return await botApi.SendFormattedMessageAsync(record.ChatId, part, keyboard, cancellationToken);
        if (approval is not null)
            return await botApi.SendApprovalAsync(record.ChatId, part.PlainText, keyboard, cancellationToken);
        await botApi.SendMessageAsync(record.ChatId, part.PlainText, cancellationToken);
        return null;
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

    // Only a tail that may still grow into a secret is held back; text before it cannot complete one and goes out now.
    private static int SecretHoldbackLength(DeliveryRecord record, string text)
    {
        var hold = 0;
        foreach (var secret in Secrets(record))
        {
            for (var length = Math.Min(secret.Length - 1, text.Length); length > hold; length--)
            {
                if (text.AsSpan(text.Length - length).SequenceEqual(secret.AsSpan(0, length)))
                {
                    hold = length;
                    break;
                }
            }
        }
        return hold;
    }

    // Streamed text goes out in whole lines, so a part never starts or ends in the middle of one; only a line longer
    // than a whole message is split before the turn ends.
    private static int LineBoundary(string text, int length, int messageLength)
    {
        if (length == 0) return 0;
        var newline = text.LastIndexOf('\n', length - 1);
        return newline >= 0 ? newline + 1 : length >= messageLength ? length : 0;
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
            record.Chunks.Count + record.PendingCommands.Count + (record.Buffer.Length == 0 ? 0 :
                (record.Buffer.Length + MaxMessageLength - record.PrefixReserve - 1) /
                (MaxMessageLength - record.PrefixReserve)));

    private sealed class DeliveryRecord(string id, long userId, long chatId, string? sessionId)
    {
        public string Id { get; } = id;
        public long UserId { get; } = userId;
        public long ChatId { get; } = chatId;
        // Null for jobs, which are never prefixed.
        public string? SessionId { get; } = sessionId;
        public int PrefixReserve { get; } = sessionId is null ? 0 : $"[{sessionId}] ".Length;
        public DateTimeOffset LastSentUtc { get; set; } = DateTimeOffset.MinValue;
        public DateTimeOffset CreatedAtUtc { get; } = DateTimeOffset.UtcNow;
        public StringBuilder Buffer { get; } = new();
        public int BufferOffset { get; set; }
        public Queue<PendingCommand> PendingCommands { get; } = [];
        public TelegramMessageFormatter Formatter { get; } = new();
        public List<TelegramFormattedMessage> Chunks { get; } = [];
        public HashSet<int> PlainChunks { get; } = [];
        public Dictionary<int, ApprovalMessage> ApprovalChunks { get; } = [];
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
        public bool HasVisibleText { get; set; }
        public bool AwaitingUser { get; set; }
        public bool Typing { get; set; }
        public IReadOnlyList<string> HostSecrets { get; } = new[] { "OPENAI_API_KEY", "ANTHROPIC_API_KEY" }
            .Select(Environment.GetEnvironmentVariable).OfType<string>().Where(value => value.Length > 0).ToArray();
    }
}
