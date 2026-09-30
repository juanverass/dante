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

    public void RegisterSession(string sessionId, long userId, long chatId, bool hideOutput)
    {
        lock (gate) chats[sessionId] = (userId, chatId, hideOutput);
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
            var record = GetOrCreate(id, chat.UserId, chat.ChatId, $"[{session.Id} {agentEvent.TurnId ?? "sistema"}] ");
            var formatted = FormatEvent(agentEvent, chat.HideOutput, id);
            if (agentEvent is TurnCompletedEvent or ErrorEvent) record.Final = true;
            if (formatted.Length > 0) Append(record, formatted, safe: agentEvent is TurnStartedEvent);
            if (agentEvent is TurnCompletedEvent or ErrorEvent)
                foreach (var key in itemText.Keys.Where(key => key.StartsWith(
                                 agentEvent.TurnId is null ? session.Id + "/" : id + "/",
                                 StringComparison.OrdinalIgnoreCase))
                             .ToArray()) itemText.Remove(key);
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

    private string FormatEvent(AgentEvent agentEvent, bool hideOutput, string id)
    {
        if (hideOutput)
        {
            return agentEvent switch
            {
                TurnStartedEvent => "Turno iniciado. Saída omitida para proteger segredos do ambiente.\n",
                TurnCompletedEvent completed => $"Turno {FormatOutcome(completed.Outcome)}. Saída omitida para proteger segredos do ambiente.\n",
                _ => string.Empty
            };
        }
        return agentEvent switch
        {
            TurnStartedEvent => "Turno iniciado.\n",
            MessageDeltaEvent delta => Delta(id, delta),
            MessageCompletedEvent completed => CompleteMessage(id, completed),
            ToolStartedEvent tool => $"\nFerramenta: {tool.Description}\n",
            ToolCompletedEvent tool => $"Ferramenta {tool.ItemId}: {(tool.Succeeded ? "concluída" : "falhou")}.\n",
            FileChangeEvent change => $"Arquivos alterados: {string.Join(", ", change.Paths)}\n",
            WarningEvent warning => $"Aviso: {warning.Message}\n",
            ErrorEvent error => $"Erro: {error.Message}\n",
            ApprovalRequestedEvent approval => $"Aprovação pendente {approval.RequestId}: {approval.Action}\n",
            UserInputRequestedEvent input => $"Resposta pendente {input.RequestId}: {string.Join("; ", input.Questions.Select(q => q.Text))}\n",
            TurnCompletedEvent completed => $"\nTurno {FormatOutcome(completed.Outcome)}." +
                (completed.Error is null ? "\n" : $" {completed.Error}\n"),
            _ => string.Empty
        };
    }

    private string Delta(string id, MessageDeltaEvent delta)
    {
        var key = id + "/" + delta.ItemId;
        if (!itemText.TryGetValue(key, out var text)) itemText[key] = text = new StringBuilder();
        if (text.Length < 128_000) text.Append(delta.Text);
        return delta.Text;
    }

    private static string FormatOutcome(AgentTurnOutcome outcome) => outcome switch
    {
        AgentTurnOutcome.Completed => "concluído",
        AgentTurnOutcome.Interrupted => "interrompido",
        _ => "falhou"
    };

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

    private void Append(DeliveryRecord record, string text, bool schedule = true, bool safe = false)
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
        if (safe) record.Chunks.AddRange(Split(text, record.Prefix));
        else record.Buffer.Append(text);
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
                await Task.Delay(750);
                await DeliverAsync(record, CancellationToken.None);
                lock (gate) record.Scheduled = false;
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
        public bool Scheduled { get; set; }
        public bool Final { get; set; }
        public bool Truncated { get; set; }
        public IReadOnlyList<string> HostSecrets { get; } = new[] { "OPENAI_API_KEY", "ANTHROPIC_API_KEY" }
            .Select(Environment.GetEnvironmentVariable).OfType<string>().Where(value => value.Length > 0).ToArray();
    }
}
