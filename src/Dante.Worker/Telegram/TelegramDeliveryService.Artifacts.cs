using Dante.Worker.Artifacts;
using Dante.Worker.Sessions;

namespace Dante.Worker.Telegram;

// Files produced in sessions (#97, AD-29). A file reaches Telegram only through an explicit channel: the session's
// structured event or the user's /send. It is captured as a private copy first, so retry and /resend send the same
// bytes without the agent; an image goes as photo (preview) and as document (original), anything else as document.
// Sessions with bound secrets never upload files (AD-10): binary content cannot be redacted.
public sealed partial class TelegramDeliveryService
{
    private const int MaxArtifacts = 50;
    private readonly Dictionary<string, ArtifactDelivery> artifactDeliveries = new(StringComparer.OrdinalIgnoreCase);

    // /send: a file of the session's working directory, requested by its owner. The upload runs in the background, so
    // a large file never holds the update loop; a failure is reported in the chat with the id for /resend.
    public (string? Id, string? Error) SendFile(string sessionId, long userId, long chatId, string path, string root)
    {
        if (HidesOutput(sessionId))
            return (null, "a sessão tem segredos vinculados ao ambiente, e arquivos não saem dela");
        if (artifacts is null) return (null, "envio de arquivos indisponível");
        Artifact artifact;
        try { artifact = artifacts.Capture(userId, path, root); }
        catch (ArtifactRejectedException exception) { return (null, exception.Message); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return (null, "não foi possível ler o arquivo");
        }
        StartDelivery(Track(artifact, userId, chatId));
        return (artifact.Id, null);
    }

    public IReadOnlyList<TelegramDeliverySnapshot> ListArtifacts(long userId)
    {
        lock (gate)
            return artifactDeliveries.Values.Where(delivery => delivery.UserId == userId)
                .OrderBy(delivery => delivery.Artifact.Id, StringComparer.Ordinal).Select(Snapshot).ToArray();
    }

    private async Task PublishArtifactAsync(AgentSessionSnapshot session, ArtifactProducedEvent produced)
    {
        (long UserId, long ChatId, bool HideOutput) chat;
        lock (gate)
        {
            if (!chats.TryGetValue(session.Id, out chat)) return;
        }

        string? refusal = null;
        Artifact? artifact = null;
        if (chat.HideOutput) refusal = "a sessão tem segredos vinculados ao ambiente";
        else if (artifacts is null) refusal = "envio de arquivos indisponível";
        else
        {
            // Generated images are accepted only from the directory where the CLI saves them.
            try { artifact = artifacts.Capture(chat.UserId, produced.Path, artifacts.GeneratedImagesRoot, imageOnly: true); }
            catch (ArtifactRejectedException exception) { refusal = exception.Message; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                refusal = "não foi possível ler o arquivo";
            }
        }

        if (artifact is null)
        {
            lock (gate)
            {
                var record = GetOrCreate(produced.TurnId is null ? session.Id + "/system" : session.Id + "/" + produced.TurnId,
                    chat.UserId, chat.ChatId, session.Id);
                Append(record, Line(record, $"Arquivo produzido não enviado: {refusal}.\n"));
            }
            return;
        }

        StartDelivery(Track(artifact, chat.UserId, chat.ChatId));
    }

    // Test hook: completes when the background upload of a delivery has finished, delivered or not.
    internal Func<string, Task>? AfterArtifactDeliveryAsync { get; set; }

    private void StartDelivery(ArtifactDelivery delivery)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await DeliverArtifactAsync(delivery, CancellationToken.None);
                bool failed;
                lock (gate) failed = delivery.State == TelegramDeliveryState.Failed;
                if (failed)
                    await botApi.SendMessageAsync(delivery.ChatId, $"Falha ao enviar o arquivo {delivery.Artifact.Id}" +
                        (delivery.Problem is null ? $"; tente /resend {delivery.Artifact.Id}." : $": {delivery.Problem}."),
                        CancellationToken.None);
            }
            catch (Exception exception)
            {
                logger.LogWarning("Falha inesperada ao enviar o arquivo {ArtifactId} ({ErrorType}).", delivery.Artifact.Id,
                    exception.GetType().Name);
                lock (gate) delivery.State = TelegramDeliveryState.Failed;
            }
            Prune();
            if (AfterArtifactDeliveryAsync is { } after) await after(delivery.Artifact.Id);
        });
    }

    private ArtifactDelivery Track(Artifact artifact, long userId, long chatId)
    {
        var delivery = new ArtifactDelivery(artifact, userId, chatId);
        lock (gate) artifactDeliveries[artifact.Id] = delivery;
        Prune();
        return delivery;
    }

    // Keeps the resend window at the most recent MaxArtifacts records, oldest out first. A pending upload still uses its
    // copy, so the pruning stops at it; the prune that runs when it finishes continues from there. The copy lives as
    // long as its record: past the window there is nothing left to send.
    private void Prune()
    {
        List<Artifact> evicted = [];
        lock (gate)
        {
            var excess = artifactDeliveries.Count - MaxArtifacts;
            foreach (var old in artifactDeliveries.Values.OrderBy(old => old.CreatedAtUtc)
                         .ThenBy(old => old.Artifact.Id, StringComparer.Ordinal).ToArray())
            {
                if (excess-- <= 0 || old.State == TelegramDeliveryState.Pending) break;
                artifactDeliveries.Remove(old.Artifact.Id);
                evicted.Add(old.Artifact);
            }
        }
        foreach (var artifact in evicted) DeleteCopy(artifact);
    }

    private async Task<TelegramDeliverySnapshot> RetryArtifactAsync(ArtifactDelivery delivery,
        CancellationToken cancellationToken)
    {
        // Like text deliveries, only the parts not delivered yet are sent again.
        lock (gate) delivery.State = TelegramDeliveryState.Pending;
        await DeliverArtifactAsync(delivery, cancellationToken);
        TelegramDeliverySnapshot snapshot;
        lock (gate) snapshot = Snapshot(delivery);
        Prune();
        return snapshot;
    }

    private async Task DeliverArtifactAsync(ArtifactDelivery delivery, CancellationToken cancellationToken)
    {
        await delivery.SendGate.WaitAsync(cancellationToken);
        try
        {
            while (true)
            {
                TelegramFileUpload upload;
                lock (gate)
                {
                    if (delivery.NextPart >= delivery.Parts.Count)
                    {
                        delivery.State = TelegramDeliveryState.Delivered;
                        return;
                    }
                    var artifact = delivery.Artifact;
                    upload = new TelegramFileUpload(artifact.Path, artifact.Name, artifact.MediaType,
                        delivery.Parts[delivery.NextPart], RedactHostSecrets($"{artifact.Id} · {artifact.Name}"));
                }

                if (!File.Exists(upload.Path))
                {
                    lock (gate)
                    {
                        delivery.State = TelegramDeliveryState.Failed;
                        delivery.Problem = "o arquivo não está mais disponível para reenvio";
                    }
                    return;
                }

                var sent = false;
                for (var attempt = 0; attempt < 4; attempt++)
                {
                    try
                    {
                        await botApi.SendFileAsync(delivery.ChatId, upload, cancellationToken);
                        sent = true;
                        break;
                    }
                    catch (Exception exception) when (IsTransient(exception, cancellationToken) && attempt < 3)
                    {
                        var backoff = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                        if (exception is TelegramRateLimitException { RetryAfter: { } retryAfter } && retryAfter > backoff)
                            backoff = retryAfter;
                        await Task.Delay(backoff, cancellationToken);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException ||
                                                      !cancellationToken.IsCancellationRequested)
                    {
                        logger.LogWarning("Falha ao enviar o arquivo {ArtifactId} ao Telegram ({ErrorType}, HTTP {StatusCode}).",
                            delivery.Artifact.Id, exception.GetType().Name,
                            (exception as HttpRequestException)?.StatusCode is { } status ? (int)status : null);
                        break;
                    }
                }

                lock (gate)
                {
                    if (!sent)
                    {
                        delivery.State = TelegramDeliveryState.Failed;
                        return;
                    }
                    delivery.NextPart++;
                }
            }
        }
        finally { delivery.SendGate.Release(); }
    }

    private void DeleteCopy(Artifact artifact)
    {
        try { artifacts?.Delete(artifact); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Falha ao apagar a cópia do arquivo {ArtifactId} ({ErrorType}).", artifact.Id,
                exception.GetType().Name);
        }
    }

    private static string RedactHostSecrets(string text)
    {
        foreach (var value in new[] { "OPENAI_API_KEY", "ANTHROPIC_API_KEY" }
                     .Select(Environment.GetEnvironmentVariable).OfType<string>().Where(value => value.Length > 0))
            text = text.Replace(value, "[segredo omitido]", StringComparison.Ordinal);
        return text;
    }

    private static TelegramDeliverySnapshot Snapshot(ArtifactDelivery delivery) =>
        new(delivery.Artifact.Id, delivery.State, delivery.NextPart, delivery.Parts.Count, delivery.Problem);

    private sealed class ArtifactDelivery(Artifact artifact, long userId, long chatId)
    {
        public Artifact Artifact { get; } = artifact;
        public long UserId { get; } = userId;
        public long ChatId { get; } = chatId;
        public DateTimeOffset CreatedAtUtc { get; } = DateTimeOffset.UtcNow;
        // An image goes as photo (preview) and as document (original); anything else as document.
        public IReadOnlyList<bool> Parts { get; } = artifact.AsPhoto ? [true, false] : [false];
        public SemaphoreSlim SendGate { get; } = new(1, 1);
        public TelegramDeliveryState State { get; set; } = TelegramDeliveryState.Pending;
        public int NextPart { get; set; }
        public string? Problem { get; set; }
    }
}
