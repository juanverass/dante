using Dante.Worker.Attachments;

namespace Dante.Tests;

public sealed class PendingAttachmentsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "dante-pending-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime time = new();
    private readonly AttachmentStore store;
    private readonly PendingAttachments pending;

    public PendingAttachmentsTests()
    {
        store = new AttachmentStore(root);
        pending = new PendingAttachments(store, time);
    }

    [Fact]
    public async Task ABatchIsPerUserAndContextAndOnlyItsContextCanTakeIt()
    {
        var first = await Image(123);
        var second = await Image(123);
        pending.Add(123, 1, "Claude:General", first, "legenda");
        pending.Add(123, 1, "Claude:General", second, null);
        pending.Add(456, 2, "Claude:General", await Image(456), null);

        Assert.Equal([first, second], pending.Get(123)!.Items);
        Assert.Equal("legenda", pending.Get(123)!.Caption);
        Assert.Null(pending.Take(123, "Codex:General"));
        var taken = pending.Take(123, "Claude:General");

        Assert.Equal(2, taken!.Items.Count);
        Assert.Null(pending.Get(123));
        Assert.Single(pending.Get(456)!.Items);
        // Taking hands the files over: they stay on disk for whoever runs the turn.
        Assert.True(File.Exists(first.Path));
    }

    [Fact]
    public async Task AnotherContextReplacesTheBatchAndDeletesItsFiles()
    {
        var old = await Image(123);
        pending.Add(123, 1, "Claude:General", old, null);

        var replaced = pending.Add(123, 1, "session:S000001", await Image(123), null);

        Assert.Equal([old], replaced!.Items);
        Assert.False(File.Exists(old.Path));
        Assert.Equal("session:S000001", pending.Get(123)!.ContextKey);
    }

    [Fact]
    public async Task ContextChangesDiscardOnlyAStaleBatch()
    {
        var item = await Image(123);
        pending.Add(123, 1, "Claude:General", item, null);

        Assert.Null(pending.DiscardIfContextChanged(123, "Claude:General"));
        Assert.True(File.Exists(item.Path));
        Assert.Single(pending.DiscardIfContextChanged(123, "Codex:General")!.Items);
        Assert.False(File.Exists(item.Path));
        Assert.Null(pending.Get(123));
    }

    [Fact]
    public async Task LimitsRefuseAndDeleteTheExtraFile()
    {
        for (var index = 0; index < PendingAttachments.MaxItems; index++)
            pending.Add(123, 1, "k", await Image(123), null);
        var extra = await Image(123);

        Assert.True(pending.IsFull(123, "k"));
        Assert.False(pending.IsFull(123, "outro"));
        Assert.Contains("limite de 10", Assert.Throws<AttachmentRejectedException>(() =>
            pending.Add(123, 1, "k", extra, null)).Message);
        Assert.False(File.Exists(extra.Path));

        var huge = await Image(456) with { Bytes = PendingAttachments.MaxBytes + 1 };
        Assert.Contains("20 MB", Assert.Throws<AttachmentRejectedException>(() =>
            pending.Add(456, 2, "k", huge, null)).Message);
        Assert.False(File.Exists(huge.Path));
    }

    [Fact]
    public async Task ExpiryCountsFromTheLastItemAndDeletesTheFiles()
    {
        var first = await Image(123);
        pending.Add(123, 1, "k", first, null);
        time.Advance(TimeSpan.FromMinutes(9));
        pending.Add(123, 1, "k", await Image(123), null);
        time.Advance(TimeSpan.FromMinutes(9));
        Assert.Empty(pending.RemoveExpired());

        time.Advance(TimeSpan.FromMinutes(1));
        var expired = Assert.Single(pending.RemoveExpired());

        Assert.Equal((123L, 1L, 2), (expired.OwnerId, expired.ChatId, expired.Items.Count));
        Assert.False(File.Exists(first.Path));
        Assert.Null(pending.Get(123));
    }

    private Task<Attachment> Image(long owner) => store.SaveImageAsync(owner, "pending", null,
        (stream, token) => stream.WriteAsync(TestImages.Png(10, 10), token).AsTask(), CancellationToken.None);

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        public void Advance(TimeSpan delta) => now += delta;
        public override DateTimeOffset GetUtcNow() => now;
    }
}
