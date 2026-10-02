using Dante.Worker.Attachments;

namespace Dante.Tests;

public sealed class AttachmentStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "dante-attachments-" + Guid.NewGuid().ToString("N"));

    public static TheoryData<byte[], string, string, int, int> Images => new()
    {
        { TestImages.Png(1170, 2532), "image/png", ".png", 1170, 2532 },
        { TestImages.Gif(320, 200), "image/gif", ".gif", 320, 200 },
        { TestImages.Jpeg(1280, 960), "image/jpeg", ".jpg", 1280, 960 },
        { TestImages.WebPExtended(4000, 3000), "image/webp", ".webp", 4000, 3000 }
    };

    [Theory]
    [MemberData(nameof(Images))]
    public async Task StoresImagesUnderTheOwnerWithGeneratedNamesAndRealTypes(byte[] content, string mediaType,
        string extension, int width, int height)
    {
        var store = new AttachmentStore(root);

        var attachment = await Save(store, 123, content, "../../etc/passwd");

        Assert.Equal((mediaType, width, height, (long)content.Length), (attachment.MediaType, attachment.Width!.Value,
            attachment.Height!.Value, attachment.Bytes));
        Assert.Equal(Path.Combine(root, "123", "pending", attachment.Id + extension), attachment.Path);
        Assert.Matches("^A[0-9]{6}$", attachment.Id);
        Assert.Equal(content, File.ReadAllBytes(attachment.Path));
        Assert.Single(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(attachment.Path));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(Path.Combine(root, "123")));
        }
    }

    [Theory]
    [InlineData("%PDF-1.7 não é imagem")]
    [InlineData("")]
    public async Task RefusesContentThatIsNotASupportedImageAndLeavesNothingBehind(string content)
    {
        var store = new AttachmentStore(root);

        var error = await Assert.ThrowsAsync<AttachmentRejectedException>(() =>
            Save(store, 123, System.Text.Encoding.UTF8.GetBytes(content)));

        Assert.Contains("formato não suportado", error.Message);
        Assert.Empty(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task RefusesImagesLargerThanTheSideLimitAndDownloadFailures()
    {
        var store = new AttachmentStore(root);

        await Assert.ThrowsAsync<AttachmentRejectedException>(() => Save(store, 123, TestImages.Png(8001, 10)));
        await Assert.ThrowsAsync<IOException>(() => store.SaveImageAsync(123, "pending", null,
            (_, _) => throw new IOException("falha"), CancellationToken.None));

        Assert.Empty(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("../outro")]
    public async Task ScopesCannotLeaveTheOwnerDirectory(string scope)
    {
        var store = new AttachmentStore(root);

        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveImageAsync(123, scope, null,
            (stream, token) => stream.WriteAsync(TestImages.Png(1, 1), token).AsTask(), CancellationToken.None));
    }

    [Fact]
    public async Task OwnersAreIsolatedAndDeleteNeverLeavesTheStore()
    {
        var store = new AttachmentStore(root);
        var first = await Save(store, 123, TestImages.Png(10, 10));
        var second = await Save(store, 456, TestImages.Png(10, 10));
        var outside = Path.Combine(Path.GetTempPath(), "dante-outside-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(outside, "não apagar");
        try
        {
            Assert.NotEqual(Path.GetDirectoryName(first.Path), Path.GetDirectoryName(second.Path));
            store.Delete(first with { Path = outside });
            Assert.True(File.Exists(outside));

            store.Delete(first);
            Assert.False(File.Exists(first.Path));
            Assert.True(File.Exists(second.Path));
        }
        finally { File.Delete(outside); }
    }

    [Fact]
    public async Task AFreshStoreNeverReusesTheNameOfARecentFileFromAPreviousRun()
    {
        // Ids restart with the process, and the startup sweep keeps files younger than 24 h.
        var previous = await Save(new AttachmentStore(root), 123, TestImages.Png(10, 10));
        var restarted = new AttachmentStore(root);
        Assert.Equal(0, restarted.SweepStale(TimeSpan.FromHours(24)));

        var next = await Save(restarted, 123, TestImages.Png(20, 20));
        var other = await Save(restarted, 123, TestImages.Gif(30, 30));

        Assert.NotEqual(previous.Id, next.Id);
        Assert.Equal(3, new[] { previous.Id, next.Id, other.Id }.Distinct().Count());
        Assert.Equal(TestImages.Png(10, 10), File.ReadAllBytes(previous.Path));
        Assert.Equal(TestImages.Png(20, 20), File.ReadAllBytes(next.Path));
        Assert.Equal(3, Directory.GetFiles(Path.Combine(root, "123", "pending")).Length);
    }

    [Fact]
    public async Task SweepRemovesOnlyOldFiles()
    {
        var store = new AttachmentStore(root);
        var old = await Save(store, 123, TestImages.Png(10, 10));
        var recent = await Save(store, 456, TestImages.Png(10, 10));
        File.SetLastWriteTimeUtc(old.Path, DateTime.UtcNow.AddHours(-25));

        Assert.Equal(1, store.SweepStale(TimeSpan.FromHours(24)));

        Assert.False(File.Exists(old.Path));
        Assert.False(Directory.Exists(Path.Combine(root, "123")));
        Assert.True(File.Exists(recent.Path));
        Assert.Equal(0, new AttachmentStore(Path.Combine(root, "inexistente")).SweepStale(TimeSpan.Zero));
    }

    private static Task<Attachment> Save(AttachmentStore store, long owner, byte[] content, string? name = null) =>
        store.SaveImageAsync(owner, "pending", name,
            (stream, token) => stream.WriteAsync(content, token).AsTask(), CancellationToken.None);

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
