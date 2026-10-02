using System.Collections.Concurrent;
using System.Threading.Channels;
using Dante.Worker.Agents;
using Dante.Worker.Attachments;
using Dante.Worker.Jobs;
using Dante.Worker.Settings;
using Dante.Worker.Telegram;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dante.Tests;

// Media intake (#94, AD-29): files reach a pending batch of their sender, with replies and refusals, and nothing is
// downloaded for unauthorized users, or for audio and video when the tools that prepare them are missing (#96).
public sealed class TelegramMediaIntakeTests : IAsyncDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "dante-media-" + Guid.NewGuid().ToString("N"));
    private readonly BotApi api = new();
    private readonly ManualTime time = new();
    private readonly FakeMediaTools tools = new();
    private PendingAttachments? pending;
    private TelegramPollingService? service;

    private string Attachments => Path.Combine(root, "attachments");

    [Fact]
    public async Task PhotoIsStoredFromTheLargestSizeAndAcknowledged()
    {
        api.Files["small"] = TestImages.Jpeg(90, 60);
        api.Files["large"] = TestImages.Jpeg(1280, 960);
        await StartAsync();

        api.Enqueue(Message(photo: [new TelegramPhotoSize("small", 90, 60, 100), new TelegramPhotoSize("large", 1280, 960, 300)]));

        var reply = await api.NextMessageAsync();
        Assert.StartsWith("Recebi 1 imagem.", reply);
        Assert.Contains("Envie o pedido em texto: as imagens vão junto com a próxima mensagem.", reply);
        Assert.Equal(["large"], api.Downloads);
        var batch = pending!.Get(123)!;
        Assert.Equal("Claude:General", batch.ContextKey);
        var item = Assert.Single(batch.Items);
        Assert.Equal((1280, 960, "image/jpeg"), (item.Width!.Value, item.Height!.Value, item.MediaType));
        Assert.Equal(Path.Combine(Attachments, "123", "pending", item.Id + ".jpg"), item.Path);
    }

    [Fact]
    public async Task UnauthorizedSendersCauseNoDownloadStorageOrReply()
    {
        api.Files["f"] = TestImages.Png(10, 10);
        await StartAsync();

        api.Enqueue(Message(document: new TelegramFileInfo("f", 33, "image/png", "a.png"), sender: 999));
        api.Enqueue(new TelegramMessage(new TelegramChat(1), "/ping", new TelegramUser(123)));

        Assert.Equal("pong", await api.NextMessageAsync());
        Assert.Empty(api.Downloads);
        Assert.False(Directory.Exists(Attachments) && Directory.EnumerateFiles(Attachments, "*", SearchOption.AllDirectories).Any());
    }

    [Theory]
    [InlineData("voice")]
    [InlineData("audio")]
    [InlineData("video")]
    [InlineData("video_note")]
    [InlineData("animation")]
    [InlineData("pdf")]
    public async Task WithoutMediaPreparationAudioVideoAndOtherFilesAreRefusedWithoutDownloading(string kind)
    {
        await StartAsync();
        var file = new TelegramFileInfo("f", 10, kind == "pdf" ? "application/pdf" : "audio/ogg");
        api.Enqueue(kind switch
        {
            "voice" => Message() with { Voice = file },
            "audio" => Message() with { Audio = file },
            "video" => Message() with { Video = file },
            "video_note" => Message() with { VideoNote = file },
            "animation" => Message() with { Animation = file },
            _ => Message(document: file)
        });

        var reply = await api.NextMessageAsync();
        Assert.StartsWith("Não recebi o arquivo: ", reply);
        Assert.Contains(kind switch
        {
            "pdf" => "formato não suportado",
            "animation" => "animações não são processadas; envie como vídeo",
            _ => "ainda não processo áudio nem vídeo"
        }, reply);
        Assert.Empty(api.Downloads);
        Assert.Null(pending!.Get(123));
    }

    [Theory]
    [InlineData("voice", AttachmentKind.Audio, "audio/ogg", ".ogg")]
    [InlineData("audio", AttachmentKind.Audio, "audio/mpeg", ".mp3")]
    [InlineData("video", AttachmentKind.Video, "video/mp4", ".mp4")]
    [InlineData("video_note", AttachmentKind.Video, "video/mp4", ".mp4")]
    [InlineData("audio_document", AttachmentKind.Audio, "audio/mp4", ".m4a")]
    [InlineData("video_document", AttachmentKind.Video, "video/quicktime", ".mov")]
    public async Task AudioAndVideoAreStoredByContentWhenTheToolsAreInstalled(string field, AttachmentKind kind,
        string mediaType, string extension)
    {
        api.Files["f"] = field switch
        {
            "voice" => TestMedia.Ogg(),
            "audio" => TestMedia.Mp3(),
            "video_document" => TestMedia.Mp4("qt  "),
            _ => TestMedia.Mp4()
        };
        await StartAsync(media: true);
        var file = new TelegramFileInfo("f", 20, "application/octet-stream", "gravação.bin");
        api.Enqueue(field switch
        {
            "voice" => Message() with { Voice = file },
            "audio" => Message() with { Audio = file },
            "video" => Message() with { Video = file },
            "video_note" => Message() with { VideoNote = file },
            "audio_document" => Message(document: file with { MimeType = "audio/x-m4a" }),
            _ => Message(document: file with { MimeType = "video/quicktime" })
        });

        var reply = await api.NextMessageAsync();
        Assert.StartsWith($"Recebi 1 {(kind == AttachmentKind.Audio ? "áudio" : "vídeo")}.", reply);
        Assert.Contains("áudio e vídeo são transcritos e amostrados localmente", reply);
        var item = Assert.Single(pending!.Get(123)!.Items);
        Assert.Equal((kind, mediaType, "gravação.bin"), (item.Kind, item.MediaType, item.Name));
        Assert.Equal(Path.Combine(Attachments, "123", "pending", item.Id + extension), item.Path);
        Assert.Empty(tools.Calls);
    }

    [Fact]
    public async Task AMissingToolRefusesAudioBeforeDownloadingWithWhatToInstall()
    {
        tools.Missing[AttachmentKind.Audio] = "whisper-cli não encontrado no PATH; instale o whisper.cpp";
        await StartAsync(media: true);

        api.Enqueue(Message() with { Voice = new TelegramFileInfo("f", 10, "audio/ogg") });

        Assert.Equal("Não recebi o arquivo: áudio indisponível: whisper-cli não encontrado no PATH; instale o whisper.cpp.",
            await api.NextMessageAsync());
        Assert.Empty(api.Downloads);
        Assert.Null(pending!.Get(123));
    }

    [Fact]
    public async Task MediaOverTheDownloadLimitOrWithOtherContentIsRefused()
    {
        api.Files["png"] = TestImages.Png(10, 10);
        await StartAsync(media: true);

        api.Enqueue(Message() with { Video = new TelegramFileInfo("big", AttachmentStore.MaxMediaBytes + 1, "video/mp4") });
        Assert.Equal("Não recebi o arquivo: arquivo acima de 20 MB.", await api.NextMessageAsync());
        api.Enqueue(Message() with { Voice = new TelegramFileInfo("png", 33, "audio/ogg") });
        Assert.StartsWith("Não recebi o arquivo: formato de áudio não suportado", await api.NextMessageAsync());

        Assert.Equal(["png"], api.Downloads);
        Assert.Null(pending!.Get(123));
        Assert.Empty(Directory.EnumerateFiles(Attachments, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task AnAlbumGetsOneReplyKeepsOrderIgnoresRepeatsAndReportsRefusals()
    {
        api.Files["a"] = TestImages.Png(10, 10);
        api.Files["b"] = TestImages.Gif(20, 20);
        api.Files["fake"] = "não é imagem"u8.ToArray();
        await StartAsync();

        api.Enqueue(Message(photo: [new TelegramPhotoSize("a", 10, 10)], group: "G1", id: 1));
        api.Enqueue(Message(photo: [new TelegramPhotoSize("a", 10, 10)], group: "G1", id: 1));
        api.Enqueue(Message(document: new TelegramFileInfo("fake", 20, "image/png", "x.png"), group: "G1", id: 2));
        api.Enqueue(Message(document: new TelegramFileInfo("b", 13, "image/gif", "../../b.gif"), group: "G1", id: 3));

        var reply = await api.NextMessageAsync();
        Assert.StartsWith("Recebi 2 imagens.", reply);
        Assert.Contains("1 arquivo recusado: formato não suportado", reply);
        Assert.Equal(["a", "fake", "b"], api.Downloads);
        var batch = pending!.Get(123)!;
        Assert.Equal(["image/png", "image/gif"], batch.Items.Select(item => item.MediaType));
        Assert.Equal("b.gif", batch.Items[1].Name);
        Assert.Equal(2, Directory.GetFiles(Attachments, "*", SearchOption.AllDirectories).Length);
        await Task.Delay(300);
        Assert.False(api.HasMessage);
    }

    [Fact]
    public async Task DownloadFailuresAndOversizedFilesAreReportedAndLeaveNothingBehind()
    {
        api.Files["big"] = TestImages.Png(10, 10, padding: (int)AttachmentStore.MaxImageBytes);
        await StartAsync();

        api.Enqueue(Message(photo: [new TelegramPhotoSize("missing", 10, 10)]));
        Assert.Equal("Não recebi o arquivo: não consegui baixar o arquivo do Telegram.", await api.NextMessageAsync());
        api.Enqueue(Message(document: new TelegramFileInfo("big", null, "image/png")));
        Assert.Equal("Não recebi o arquivo: imagem acima de 7 MB.", await api.NextMessageAsync());
        api.Enqueue(Message(photo: [new TelegramPhotoSize("never", 10, 10, AttachmentStore.MaxImageBytes + 1)]));
        Assert.Equal("Não recebi o arquivo: imagem acima de 7 MB.", await api.NextMessageAsync());

        Assert.DoesNotContain("never", api.Downloads);
        Assert.Empty(Directory.GetFiles(Attachments, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task PendingImagesAppearInStatusAndAreDiscardedWhenTheContextChanges()
    {
        api.Files["a"] = TestImages.Png(10, 10);
        await StartAsync();
        api.Enqueue(Message(photo: [new TelegramPhotoSize("a", 10, 10)]));
        await api.NextMessageAsync();
        var path = pending!.Get(123)!.Items[0].Path;

        api.Enqueue(new TelegramMessage(new TelegramChat(1), "/status", new TelegramUser(123)));
        Assert.Contains("Anexos pendentes: 1 imagem(ns)", await api.NextMessageAsync());

        api.Enqueue(new TelegramMessage(new TelegramChat(1), "/agent set claude", new TelegramUser(123)));
        Assert.Equal("Agente padrão alterado para Claude.", await api.NextMessageAsync());
        Assert.True(File.Exists(path));

        api.Enqueue(new TelegramMessage(new TelegramChat(1), "/agent set codex", new TelegramUser(123)));
        Assert.Equal("Agente padrão alterado para Codex.", await api.NextMessageAsync());
        Assert.Equal("1 imagem(ns) pendente(s) descartada(s): o contexto da conversa mudou.", await api.NextMessageAsync());
        Assert.False(File.Exists(path));
        Assert.Null(pending.Get(123));
    }

    [Fact]
    public async Task ExpiredImagesAreDeletedAndTheOwnerIsTold()
    {
        api.Files["a"] = TestImages.Png(10, 10);
        await StartAsync();
        api.Enqueue(Message(photo: [new TelegramPhotoSize("a", 10, 10)]));
        await api.NextMessageAsync();
        var path = pending!.Get(123)!.Items[0].Path;

        time.Advance(PendingAttachments.Expiry);
        api.Enqueue(new TelegramMessage(new TelegramChat(1), "/ping", new TelegramUser(123)));

        Assert.Equal("pong", await api.NextMessageAsync());
        Assert.Equal("1 imagem(ns) pendente(s) apagada(s): nenhum pedido chegou em 10 min.", await api.NextMessageAsync());
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task StartupRemovesLeftoversOfAPreviousRun()
    {
        var leftover = Path.Combine(Attachments, "123", "pending", "A000001.png");
        Directory.CreateDirectory(Path.GetDirectoryName(leftover)!);
        File.WriteAllBytes(leftover, TestImages.Png(1, 1));
        File.SetLastWriteTimeUtc(leftover, DateTime.UtcNow.AddDays(-2));

        await StartAsync();
        api.Enqueue(new TelegramMessage(new TelegramChat(1), "/ping", new TelegramUser(123)));
        await api.NextMessageAsync();

        Assert.False(File.Exists(leftover));
    }

    private async Task StartAsync(bool media = false)
    {
        var options = Options.Create(new TelegramOptions { BotToken = "test", AllowedUserIds = "123" });
        var store = new AttachmentStore(Attachments);
        pending = new PendingAttachments(store, time);
        service = new TelegramPollingService(api, options, new TelegramUserAuthorizer(options), new NoRunner(),
            new NoRunner(), new JobRegistry(), NullLogger<TelegramPollingService>.Instance, null,
            new GeneralWorkspace(Path.Combine(root, "general")), new AssistantSettingsStore(Path.Combine(root, "settings.json")),
            attachments: store, pendingAttachments: pending, media: media ? new MediaPreparer(tools) : null);
        await service.StartAsync(CancellationToken.None);
    }

    private static TelegramMessage Message(IReadOnlyList<TelegramPhotoSize>? photo = null, TelegramFileInfo? document = null,
        string? caption = null, string? group = null, long id = 0, long sender = 123) =>
        new(new TelegramChat(1), null, new TelegramUser(sender), id, caption, group, photo, document);

    public async ValueTask DisposeAsync()
    {
        if (service is not null)
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public void Advance(TimeSpan delta) => now += delta;
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class NoRunner : ICodexRunner, IClaudeRunner
    {
        public Task<AgentProcessResult> RunAsync(string prompt, string workingDirectory,
            CancellationToken cancellationToken = default, bool generalMode = false,
            IReadOnlyDictionary<string, string>? environment = null, string? model = null, string? effort = null,
            IReadOnlyList<Dante.Worker.Attachments.Attachment>? attachments = null) =>
            throw new InvalidOperationException("Nenhum agente deve rodar nestes testes.");
    }

    private sealed class BotApi : ITelegramBotApi
    {
        private readonly Channel<TelegramUpdate> updates = Channel.CreateUnbounded<TelegramUpdate>();
        private readonly Channel<string> messages = Channel.CreateUnbounded<string>();
        private readonly ConcurrentQueue<string> downloads = new();
        private long nextId;

        public ConcurrentDictionary<string, byte[]> Files { get; } = new();
        public IReadOnlyList<string> Downloads => downloads.ToArray();
        public bool HasMessage => messages.Reader.TryPeek(out _);

        public void Enqueue(TelegramMessage message) =>
            updates.Writer.TryWrite(new TelegramUpdate(Interlocked.Increment(ref nextId), message));

        public async Task<string> NextMessageAsync() =>
            await messages.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        public async Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(long offset, CancellationToken cancellationToken) =>
            [await updates.Reader.ReadAsync(cancellationToken)];

        public Task SendMessageAsync(long chatId, string text, CancellationToken cancellationToken)
        {
            messages.Writer.TryWrite(text);
            return Task.CompletedTask;
        }

        public async Task<long> DownloadFileAsync(string fileId, Stream destination, long maxBytes,
            CancellationToken cancellationToken)
        {
            downloads.Enqueue(fileId);
            if (!Files.TryGetValue(fileId, out var content)) throw new HttpRequestException("404");
            if (content.Length > maxBytes) throw new TelegramFileTooLargeException();
            await destination.WriteAsync(content, cancellationToken);
            return content.Length;
        }
    }
}
