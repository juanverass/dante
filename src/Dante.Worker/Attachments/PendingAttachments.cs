namespace Dante.Worker.Attachments;

public sealed record PendingBatch(long OwnerId, long ChatId, string ContextKey, IReadOnlyList<Attachment> Items,
    string? Caption, DateTimeOffset ExpiresAtUtc)
{
    public long Bytes => Items.Sum(item => item.Bytes);
}

// Media received without a request waits here, per user, for the next text in the same context (AD-29). A batch is
// tied to the context it arrived in: it never moves to another session, context or user, and expires after ten
// minutes. Files of discarded or expired batches are deleted; Take hands the files over to whoever runs the turn.
public sealed class PendingAttachments(AttachmentStore store, TimeProvider? time = null)
{
    public const int MaxItems = 10;
    public const long MaxBytes = 20 * 1024 * 1024;
    public static readonly TimeSpan Expiry = TimeSpan.FromMinutes(10);
    private readonly TimeProvider time = time ?? TimeProvider.System;
    private readonly object gate = new();
    private readonly Dictionary<long, PendingBatch> batches = [];

    public PendingBatch? Get(long ownerId)
    {
        lock (gate) return batches.GetValueOrDefault(ownerId);
    }

    // Refusal before downloading: the batch of this context is already full.
    public bool IsFull(long ownerId, string contextKey)
    {
        lock (gate)
            return batches.TryGetValue(ownerId, out var batch) && batch.ContextKey == contextKey &&
                batch.Items.Count >= MaxItems;
    }

    // Adds a downloaded attachment. A batch from another context is discarded first and returned so the caller can
    // say so. Over the limits, the attachment is deleted and the reason thrown.
    public PendingBatch? Add(long ownerId, long chatId, string contextKey, Attachment attachment, string? caption)
    {
        PendingBatch? replaced = null;
        lock (gate)
        {
            if (batches.TryGetValue(ownerId, out var current) && current.ContextKey != contextKey)
            {
                replaced = current;
                batches.Remove(ownerId);
                current = null;
            }
            var items = current?.Items ?? [];
            string? error = items.Count >= MaxItems ? $"limite de {MaxItems} imagens pendentes atingido" :
                items.Sum(item => item.Bytes) + attachment.Bytes > MaxBytes ? "limite de 20 MB pendentes atingido" : null;
            if (error is not null)
            {
                store.Delete(attachment);
                if (replaced is not null) DeleteFiles(replaced);
                throw new AttachmentRejectedException(error);
            }
            batches[ownerId] = new PendingBatch(ownerId, chatId, contextKey, [.. items, attachment],
                caption ?? current?.Caption, time.GetUtcNow() + Expiry);
        }
        if (replaced is not null) DeleteFiles(replaced);
        return replaced;
    }

    // Hands the batch to the caller, who becomes responsible for the files. Another context gets nothing.
    public PendingBatch? Take(long ownerId, string contextKey)
    {
        lock (gate)
        {
            if (!batches.TryGetValue(ownerId, out var batch) || batch.ContextKey != contextKey) return null;
            batches.Remove(ownerId);
            return batch;
        }
    }

    // Context changes (/use, /agent set, /session start|select|close) drop a batch that no longer matches.
    public PendingBatch? DiscardIfContextChanged(long ownerId, string contextKey)
    {
        PendingBatch? discarded;
        lock (gate)
        {
            if (!batches.TryGetValue(ownerId, out discarded) || discarded.ContextKey == contextKey) return null;
            batches.Remove(ownerId);
        }
        DeleteFiles(discarded);
        return discarded;
    }

    public IReadOnlyList<PendingBatch> RemoveExpired()
    {
        List<PendingBatch> expired;
        lock (gate)
        {
            var now = time.GetUtcNow();
            expired = batches.Values.Where(batch => batch.ExpiresAtUtc <= now).ToList();
            foreach (var batch in expired) batches.Remove(batch.OwnerId);
        }
        foreach (var batch in expired) DeleteFiles(batch);
        return expired;
    }

    private void DeleteFiles(PendingBatch batch)
    {
        foreach (var item in batch.Items) store.Delete(item);
    }
}
