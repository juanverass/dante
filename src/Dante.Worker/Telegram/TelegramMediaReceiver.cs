using Dante.Worker.Attachments;

namespace Dante.Worker.Telegram;

// Media messages become pending attachments of their sender (#94, AD-29). Images are downloaded with size and time
// limits and checked by content; audio, video and other files are refused without downloading. The items of an album
// arrive as separate messages and get a single reply once no new item came for the album window. A caption is the
// request (#95): once the images are pending, it is dispatched as the text that consumes them. An album completes
// outside the update loop, so its reply and dispatch run through exclusive, which serializes them with the updates.
public sealed class TelegramMediaReceiver(ITelegramBotApi botApi, AttachmentStore store, PendingAttachments pending,
    ILogger logger, Func<Func<Task>, CancellationToken, Task> exclusive, TimeSpan? albumWindow = null)
{
    private const string PendingScope = "pending";
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(60);
    private static readonly string[] ImageTypes = ["image/jpeg", "image/png", "image/gif", "image/webp"];
    private readonly TimeSpan albumWindow = albumWindow ?? TimeSpan.FromSeconds(1.5);
    private readonly object gate = new();
    private readonly Dictionary<(long Owner, string Group), Album> albums = [];

    public async Task ReceiveAsync(TelegramMessage message, string contextKey,
        Func<string, CancellationToken, Task> reply, Func<string, CancellationToken, Task> dispatch,
        CancellationToken cancellationToken)
    {
        var owner = message.From!.Id;
        if (message.MediaGroupId is not { } group)
        {
            await CompleteAsync([await StoreAsync(message, owner, contextKey, cancellationToken)], reply, dispatch,
                cancellationToken);
            return;
        }

        lock (gate)
        {
            if (!albums.TryGetValue((owner, group), out var album))
                albums[(owner, group)] = album = new Album(reply, dispatch, cancellationToken);
            // The same message is never stored twice, even if Telegram delivers it again.
            if (!album.MessageIds.Add(message.MessageId)) return;
            album.Timer?.Cancel();
            album.Timer = null;
        }
        var outcome = await StoreAsync(message, owner, contextKey, cancellationToken);
        lock (gate)
        {
            var album = albums[(owner, group)];
            album.Outcomes.Add(outcome);
            album.Timer?.Cancel();
            album.Timer = new CancellationTokenSource();
            _ = FlushLaterAsync((owner, group), album, album.Timer.Token);
        }
    }

    private async Task FlushLaterAsync((long, string) key, Album album, CancellationToken timer)
    {
        try
        {
            await Task.Delay(albumWindow, timer);
            lock (gate)
            {
                if (timer.IsCancellationRequested || !albums.Remove(key)) return;
            }
            await exclusive(() => CompleteAsync(album.Outcomes, album.Reply, album.Dispatch, album.Stopping),
                album.Stopping);
        }
        catch (OperationCanceledException) { /* A newer item restarted the window, or the worker is stopping. */ }
        catch (Exception exception)
        {
            logger.LogWarning("Falha ao confirmar o recebimento de um álbum ({ErrorType}).", exception.GetType().Name);
        }
    }

    // With a caption and at least one image kept, the caption runs as the request and only problems are reported.
    private async Task CompleteAsync(IReadOnlyList<Outcome> outcomes, Func<string, CancellationToken, Task> reply,
        Func<string, CancellationToken, Task> dispatch, CancellationToken cancellationToken)
    {
        var caption = outcomes.Select(outcome => outcome.Caption).FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));
        var request = caption is not null && outcomes.Any(outcome => outcome.Attachment is not null);
        var summary = Summary(outcomes, request);
        if (summary.Length > 0) await reply(summary, cancellationToken);
        if (request) await dispatch(caption!.Trim(), cancellationToken);
    }

    private async Task<Outcome> StoreAsync(TelegramMessage message, long owner, string contextKey,
        CancellationToken cancellationToken)
    {
        if ((message.Voice ?? message.Audio ?? message.Video ?? message.VideoNote ?? message.Animation) is not null)
            return Outcome.Rejected("ainda não processo áudio nem vídeo; envie imagens", message.Caption);

        string fileId;
        string? name = null;
        if (message.Photo is { Count: > 0 } sizes)
        {
            // Telegram lists every resolution; the largest one within the limit is the print as sent.
            var best = sizes.Where(size => size.FileSize is null or <= AttachmentStore.MaxImageBytes)
                .MaxBy(size => (long)size.Width * size.Height);
            if (best is null) return Outcome.Rejected(TooLarge, message.Caption);
            fileId = best.FileId;
        }
        else if (message.Document is { } document)
        {
            if (!ImageTypes.Contains(document.MimeType?.ToLowerInvariant()))
                return Outcome.Rejected("formato não suportado; envie JPEG, PNG, GIF ou WebP", message.Caption);
            if (document.FileSize > AttachmentStore.MaxImageBytes) return Outcome.Rejected(TooLarge, message.Caption);
            fileId = document.FileId;
            name = DisplayName(document.FileName);
        }
        else return Outcome.Rejected("formato não suportado; envie JPEG, PNG, GIF ou WebP", message.Caption);

        if (pending.IsFull(owner, contextKey))
            return Outcome.Rejected($"limite de {PendingAttachments.MaxItems} imagens pendentes atingido", message.Caption);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(DownloadTimeout);
            var attachment = await store.SaveImageAsync(owner, PendingScope, name, (destination, token) =>
                botApi.DownloadFileAsync(fileId, destination, AttachmentStore.MaxImageBytes, token), timeout.Token);
            var replaced = pending.Add(owner, message.Chat.Id, contextKey, attachment, message.Caption);
            return new Outcome(attachment, null, replaced, message.Caption);
        }
        catch (TelegramFileTooLargeException) { return Outcome.Rejected(TooLarge, message.Caption); }
        catch (AttachmentRejectedException exception) { return Outcome.Rejected(exception.Message, message.Caption); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            // Exceptions of the download never carry the URL, which contains the bot token.
            logger.LogWarning("Falha ao baixar anexo do Telegram ({ErrorType}).", exception.GetType().Name);
            return Outcome.Rejected("não consegui baixar o arquivo do Telegram", message.Caption);
        }
    }

    private static string TooLarge => $"imagem acima de {AttachmentStore.MaxImageBytes / 1024 / 1024} MB";

    private string Summary(IReadOnlyList<Outcome> outcomes, bool request)
    {
        var accepted = outcomes.Count(outcome => outcome.Attachment is not null);
        var lines = new List<string>();
        if (accepted > 0 && !request)
        {
            var batch = pending.Get(outcomes.First(outcome => outcome.Attachment is not null).Attachment!.OwnerId);
            lines.Add($"Recebi {Count(accepted, "imagem", "imagens")}" +
                (batch is null || batch.Items.Count == accepted ? "." :
                    $"; {batch.Items.Count} pendentes ({batch.Bytes / 1024} KB)."));
        }
        foreach (var group in outcomes.Where(outcome => outcome.Rejection is not null).GroupBy(outcome => outcome.Rejection))
            lines.Add(accepted == 0 && outcomes.Count == 1 ? $"Não recebi o arquivo: {group.Key}." :
                $"{Count(group.Count(), "arquivo recusado", "arquivos recusados")}: {group.Key}.");
        foreach (var replaced in outcomes.Select(outcome => outcome.Replaced).OfType<PendingBatch>())
            lines.Add($"{Count(replaced.Items.Count, "imagem pendente", "imagens pendentes")} de outro contexto " +
                (replaced.Items.Count == 1 ? "foi descartada." : "foram descartadas."));
        if (accepted > 0 && !request)
            lines.Add($"Envie o pedido em texto: as imagens vão junto com a próxima mensagem. Sem pedido, elas são " +
                $"apagadas em {PendingAttachments.Expiry.TotalMinutes:0} min.");
        return string.Join('\n', lines);
    }

    private static string Count(int count, string singular, string plural) =>
        $"{count} {(count == 1 ? singular : plural)}";

    // The sender's file name is only shown back, never used as a path: no directories, no control characters.
    private static string? DisplayName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var clean = new string(Path.GetFileName(name.Replace('\\', '/')).Where(character => !char.IsControl(character))
            .ToArray()).Trim();
        return clean.Length == 0 ? null : clean.Length <= 100 ? clean : clean[..100];
    }

    private sealed record Outcome(Attachment? Attachment, string? Rejection, PendingBatch? Replaced, string? Caption)
    {
        public static Outcome Rejected(string reason, string? caption) => new(null, reason, null, caption);
    }

    private sealed class Album(Func<string, CancellationToken, Task> reply, Func<string, CancellationToken, Task> dispatch,
        CancellationToken stopping)
    {
        public Func<string, CancellationToken, Task> Reply { get; } = reply;
        public Func<string, CancellationToken, Task> Dispatch { get; } = dispatch;
        public CancellationToken Stopping { get; } = stopping;
        public HashSet<long> MessageIds { get; } = [];
        public List<Outcome> Outcomes { get; } = [];
        public CancellationTokenSource? Timer { get; set; }
    }
}
