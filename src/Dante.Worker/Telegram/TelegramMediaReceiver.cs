using Dante.Worker.Attachments;

namespace Dante.Worker.Telegram;

// Media messages become pending attachments of their sender (#94, AD-29). Images are downloaded with size and time
// limits and checked by content; audio, video and other files are refused without downloading. The items of an album
// arrive as separate messages and get a single reply once no new item came for the album window.
public sealed class TelegramMediaReceiver(ITelegramBotApi botApi, AttachmentStore store, PendingAttachments pending,
    ILogger logger, TimeSpan? albumWindow = null)
{
    private const string PendingScope = "pending";
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(60);
    private static readonly string[] ImageTypes = ["image/jpeg", "image/png", "image/gif", "image/webp"];
    private readonly TimeSpan albumWindow = albumWindow ?? TimeSpan.FromSeconds(1.5);
    private readonly object gate = new();
    private readonly Dictionary<(long Owner, string Group), Album> albums = [];

    public async Task ReceiveAsync(TelegramMessage message, string contextKey,
        Func<string, CancellationToken, Task> reply, CancellationToken cancellationToken)
    {
        var owner = message.From!.Id;
        if (message.MediaGroupId is not { } group)
        {
            await reply(Summary([await StoreAsync(message, owner, contextKey, cancellationToken)]), cancellationToken);
            return;
        }

        lock (gate)
        {
            if (!albums.TryGetValue((owner, group), out var album))
                albums[(owner, group)] = album = new Album(reply, cancellationToken);
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
            await album.Reply(Summary(album.Outcomes), album.Stopping);
        }
        catch (OperationCanceledException) { /* A newer item restarted the window, or the worker is stopping. */ }
        catch (Exception exception)
        {
            logger.LogWarning("Falha ao confirmar o recebimento de um álbum ({ErrorType}).", exception.GetType().Name);
        }
    }

    private async Task<Outcome> StoreAsync(TelegramMessage message, long owner, string contextKey,
        CancellationToken cancellationToken)
    {
        if ((message.Voice ?? message.Audio ?? message.Video ?? message.VideoNote ?? message.Animation) is not null)
            return Outcome.Rejected("ainda não processo áudio nem vídeo; envie imagens");

        string fileId;
        string? name = null;
        if (message.Photo is { Count: > 0 } sizes)
        {
            // Telegram lists every resolution; the largest one within the limit is the print as sent.
            var best = sizes.Where(size => size.FileSize is null or <= AttachmentStore.MaxImageBytes)
                .MaxBy(size => (long)size.Width * size.Height);
            if (best is null) return Outcome.Rejected(TooLarge);
            fileId = best.FileId;
        }
        else if (message.Document is { } document)
        {
            if (!ImageTypes.Contains(document.MimeType?.ToLowerInvariant()))
                return Outcome.Rejected("formato não suportado; envie JPEG, PNG, GIF ou WebP");
            if (document.FileSize > AttachmentStore.MaxImageBytes) return Outcome.Rejected(TooLarge);
            fileId = document.FileId;
            name = DisplayName(document.FileName);
        }
        else return Outcome.Rejected("formato não suportado; envie JPEG, PNG, GIF ou WebP");

        if (pending.IsFull(owner, contextKey))
            return Outcome.Rejected($"limite de {PendingAttachments.MaxItems} imagens pendentes atingido");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(DownloadTimeout);
            var attachment = await store.SaveImageAsync(owner, PendingScope, name, (destination, token) =>
                botApi.DownloadFileAsync(fileId, destination, AttachmentStore.MaxImageBytes, token), timeout.Token);
            var replaced = pending.Add(owner, message.Chat.Id, contextKey, attachment, message.Caption);
            return new Outcome(attachment, null, replaced);
        }
        catch (TelegramFileTooLargeException) { return Outcome.Rejected(TooLarge); }
        catch (AttachmentRejectedException exception) { return Outcome.Rejected(exception.Message); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            // Exceptions of the download never carry the URL, which contains the bot token.
            logger.LogWarning("Falha ao baixar anexo do Telegram ({ErrorType}).", exception.GetType().Name);
            return Outcome.Rejected("não consegui baixar o arquivo do Telegram");
        }
    }

    private static string TooLarge => $"imagem acima de {AttachmentStore.MaxImageBytes / 1024 / 1024} MB";

    private string Summary(IReadOnlyList<Outcome> outcomes)
    {
        var accepted = outcomes.Count(outcome => outcome.Attachment is not null);
        var lines = new List<string>();
        if (accepted > 0)
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
        if (accepted > 0)
            lines.Add($"Ainda não encaminho imagens aos agentes: elas ficam guardadas por " +
                $"{PendingAttachments.Expiry.TotalMinutes:0} min e depois são apagadas.");
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

    private sealed record Outcome(Attachment? Attachment, string? Rejection, PendingBatch? Replaced)
    {
        public static Outcome Rejected(string reason) => new(null, reason, null);
    }

    private sealed class Album(Func<string, CancellationToken, Task> reply, CancellationToken stopping)
    {
        public Func<string, CancellationToken, Task> Reply { get; } = reply;
        public CancellationToken Stopping { get; } = stopping;
        public HashSet<long> MessageIds { get; } = [];
        public List<Outcome> Outcomes { get; } = [];
        public CancellationTokenSource? Timer { get; set; }
    }
}
