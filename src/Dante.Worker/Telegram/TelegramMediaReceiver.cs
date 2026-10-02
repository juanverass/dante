using Dante.Worker.Attachments;

namespace Dante.Worker.Telegram;

// Media messages become pending attachments of their sender (#94, AD-29). Images are downloaded with size and time
// limits and checked by content; audio, video and other files are refused without downloading. The items of an album
// arrive as separate messages and get a single reply once no new item came for the album window. A caption is the
// request (#95): once the images are pending, it is dispatched as the text that consumes them.
// An album is one indivisible batch: its images become pending only when it completes, together with its caption, in
// the context it arrived in. Anything its sender sends next completes it first, so it keeps its place before later
// requests; when the window closes instead, completion runs through exclusive, serialized with the updates.
public sealed class TelegramMediaReceiver(ITelegramBotApi botApi, AttachmentStore store, PendingAttachments pending,
    ILogger logger, Func<long, string> contextOf, Func<Func<Task>, CancellationToken, Task> exclusive,
    TimeSpan? albumWindow = null)
{
    private const string PendingScope = "pending";
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(60);
    private static readonly string[] ImageTypes = ["image/jpeg", "image/png", "image/gif", "image/webp"];
    private readonly TimeSpan albumWindow = albumWindow ?? TimeSpan.FromSeconds(1.5);
    private readonly object gate = new();
    private readonly Dictionary<(long Owner, string Group), Album> albums = [];
    private long nextAlbum;

    public async Task ReceiveAsync(TelegramMessage message, Func<string, CancellationToken, Task> reply,
        Func<string, CancellationToken, Task> dispatch, CancellationToken cancellationToken)
    {
        var owner = message.From!.Id;
        var contextKey = contextOf(owner);
        if (message.MediaGroupId is not { } group)
        {
            var outcome = await StoreAsync(message, owner, contextKey, 0, cancellationToken);
            await CompleteAsync(owner, message.Chat.Id, contextKey, [outcome], reply, dispatch, cancellationToken);
            return;
        }

        Album album;
        lock (gate)
        {
            if (!albums.TryGetValue((owner, group), out album!))
                albums[(owner, group)] = album = new Album(owner, group, message.Chat.Id, contextKey,
                    Interlocked.Increment(ref nextAlbum), reply, dispatch, cancellationToken);
            // The same message is never stored twice, even if Telegram delivers it again.
            if (!album.MessageIds.Add(message.MessageId)) return;
            album.Timer?.Cancel();
            album.Timer = null;
        }
        var stored = await StoreAsync(message, owner, album.ContextKey,
            album.Outcomes.Count(item => item.Attachment is not null), cancellationToken);
        lock (gate)
        {
            album.Outcomes.Add(stored);
            album.Timer?.Cancel();
            album.Timer = new CancellationTokenSource();
            _ = FlushLaterAsync(album, album.Timer.Token);
        }
    }

    // Called before any other update of the owner is handled: the open albums complete first, oldest first. An item of
    // keepGroup is a continuation of that album, which stays open.
    public async Task CompleteAlbumsAsync(long owner, string? keepGroup)
    {
        List<Album> open;
        lock (gate)
        {
            open = albums.Values.Where(album => album.Owner == owner && album.Group != keepGroup)
                .OrderBy(album => album.Sequence).ToList();
            foreach (var album in open)
            {
                albums.Remove((owner, album.Group));
                album.Timer?.Cancel();
            }
        }
        foreach (var album in open) await CompleteAlbumAsync(album);
    }

    private async Task FlushLaterAsync(Album album, CancellationToken timer)
    {
        try
        {
            await Task.Delay(albumWindow, timer);
            // Removed only once exclusive: an update that got there first already completed it.
            await exclusive(async () =>
            {
                lock (gate)
                {
                    if (timer.IsCancellationRequested || !albums.Remove((album.Owner, album.Group))) return;
                }
                await CompleteAlbumAsync(album);
            }, album.Stopping);
        }
        catch (OperationCanceledException) { /* A newer item restarted the window, or the worker is stopping. */ }
    }

    private async Task CompleteAlbumAsync(Album album)
    {
        try
        {
            await CompleteAsync(album.Owner, album.ChatId, album.ContextKey, album.Outcomes, album.Reply, album.Dispatch,
                album.Stopping);
        }
        catch (OperationCanceledException) when (album.Stopping.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger.LogWarning("Falha ao confirmar o recebimento de um álbum ({ErrorType}).", exception.GetType().Name);
        }
    }

    // The images become pending in the context they arrived in, or are dropped with their caption if it changed. With a
    // caption and at least one image kept, the caption runs as the request and only problems are reported.
    private async Task CompleteAsync(long owner, long chatId, string contextKey, IReadOnlyList<Outcome> stored,
        Func<string, CancellationToken, Task> reply, Func<string, CancellationToken, Task> dispatch,
        CancellationToken cancellationToken)
    {
        if (contextOf(owner) != contextKey)
        {
            var dropped = stored.Where(outcome => outcome.Attachment is not null).ToArray();
            foreach (var outcome in dropped) store.Delete(outcome.Attachment!);
            if (dropped.Length > 0)
                await reply($"{Count(dropped.Length, "imagem descartada", "imagens descartadas")} com a legenda: o " +
                    "contexto da conversa mudou antes de o álbum terminar.", cancellationToken);
            return;
        }

        var outcomes = stored.Select(outcome => outcome.Attachment is null ? outcome
            : Register(owner, chatId, contextKey, outcome)).ToArray();
        var caption = outcomes.Select(outcome => outcome.Caption).FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));
        var request = caption is not null && outcomes.Any(outcome => outcome.Attachment is not null);
        var summary = Summary(outcomes, request);
        if (summary.Length > 0) await reply(summary, cancellationToken);
        if (request) await dispatch(caption!.Trim(), cancellationToken);
    }

    private Outcome Register(long owner, long chatId, string contextKey, Outcome outcome)
    {
        try
        {
            return outcome with
            {
                Replaced = pending.Add(owner, chatId, contextKey, outcome.Attachment!, outcome.Caption)
            };
        }
        catch (AttachmentRejectedException exception)
        {
            return Outcome.Rejected(exception.Message, outcome.Caption);
        }
    }

    // queued: images of the same album already stored but not pending yet, counted against the limit.
    private async Task<Outcome> StoreAsync(TelegramMessage message, long owner, string contextKey, int queued,
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

        if (pending.IsFull(owner, contextKey, queued))
            return Outcome.Rejected($"limite de {PendingAttachments.MaxItems} imagens pendentes atingido", message.Caption);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(DownloadTimeout);
            var attachment = await store.SaveImageAsync(owner, PendingScope, name, (destination, token) =>
                botApi.DownloadFileAsync(fileId, destination, AttachmentStore.MaxImageBytes, token), timeout.Token);
            return new Outcome(attachment, null, null, message.Caption);
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

    private sealed class Album(long owner, string group, long chatId, string contextKey, long sequence,
        Func<string, CancellationToken, Task> reply, Func<string, CancellationToken, Task> dispatch,
        CancellationToken stopping)
    {
        public long Owner { get; } = owner;
        public string Group { get; } = group;
        public long ChatId { get; } = chatId;
        public string ContextKey { get; } = contextKey;
        public long Sequence { get; } = sequence;
        public Func<string, CancellationToken, Task> Reply { get; } = reply;
        public Func<string, CancellationToken, Task> Dispatch { get; } = dispatch;
        public CancellationToken Stopping { get; } = stopping;
        public HashSet<long> MessageIds { get; } = [];
        public List<Outcome> Outcomes { get; } = [];
        public CancellationTokenSource? Timer { get; set; }
    }
}
