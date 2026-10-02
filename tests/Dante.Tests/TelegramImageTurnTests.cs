using System.Collections.Concurrent;
using System.Threading.Channels;
using Dante.Worker.Agents;
using Dante.Worker.Attachments;
using Dante.Worker.Jobs;
using Dante.Worker.Sessions;
using Dante.Worker.Settings;
using Dante.Worker.Telegram;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dante.Tests;

// Images reach the agents (#95, AD-29): a caption or the next text takes the pending images of the conversation's
// context into one turn, queue item, steer or one-shot, and their files live with the session or job that uses them.
public sealed class TelegramImageTurnTests : IAsyncDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "dante-image-turns-" + Guid.NewGuid().ToString("N"));
    private readonly FakeSessionDriverFactory drivers = new();
    private readonly BotApi api = new();
    private readonly RecordingRunner runner = new();
    private PendingAttachments? pending;
    private SessionRegistry? sessions;
    private TelegramPollingService? service;

    private string Attachments => Path.Combine(root, "attachments");

    [Fact]
    public async Task PhotoWithCaptionIsTheFirstTurnOfANewSessionAndLeavesWithIt()
    {
        api.Files["a"] = TestImages.Jpeg(40, 30);
        await StartAsync();

        api.Enqueue(Photo("a", caption: "o que há aqui?"));
        var driver = await SingleDriverAsync();
        await Eventually(() => driver.Calls.Contains("turn:o que há aqui? [A000001]"));
        var path = Path.Combine(Attachments, "123", "S000001", "A000001.jpg");
        Assert.True(File.Exists(path));
        Assert.Null(pending!.Get(123));

        // The reply is the agent's, without a receipt for the image (AD-23).
        driver.Emit(new TurnStartedEvent());
        driver.Emit(new MessageCompletedEvent("m1", "Um gráfico."));
        driver.Emit(new TurnCompletedEvent(AgentTurnOutcome.Completed));
        Assert.Equal("Um gráfico.\n", await api.NextMessageAsync());

        api.Enqueue(Text("/session close"));
        Assert.Equal("Sessão S000001 encerrada.", await api.NextMessageAsync());
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
    }

    [Fact]
    public async Task PrintsSentBeforeTheRequestGoWithItInOrder()
    {
        api.Files["a"] = TestImages.Png(10, 10);
        api.Files["b"] = TestImages.Gif(10, 10);
        await StartAsync();

        api.Enqueue(Photo("a"));
        Assert.StartsWith("Recebi 1 imagem.", await api.NextMessageAsync());
        api.Enqueue(Message(document: new TelegramFileInfo("b", 13, "image/gif", "segundo.gif")));
        Assert.StartsWith("Recebi 1 imagem; 2 pendentes", await api.NextMessageAsync());
        Assert.Empty(drivers.Created);

        api.Enqueue(Text("compare os dois"));
        var driver = await SingleDriverAsync();
        await Eventually(() => driver.Calls.Contains("turn:compare os dois [A000001,A000002]"));
        Assert.Null(pending!.Get(123));

        // The next text has nothing pending: text only, as before.
        driver.Emit(new TurnStartedEvent());
        driver.Emit(new TurnCompletedEvent(AgentTurnOutcome.Completed));
        await api.NextMessageAsync();
        await Eventually(() => sessions!.GetActive(123)!.State == AgentSessionState.Idle);
        api.Enqueue(Text("obrigado"));
        await Eventually(() => driver.Calls.Contains("turn:obrigado"));
    }

    [Fact]
    public async Task AlbumWithCaptionIsOneTurnWithEveryImage()
    {
        api.Files["a"] = TestImages.Png(10, 10);
        api.Files["b"] = TestImages.Png(20, 20);
        await StartAsync();

        api.Enqueue(Photo("a", group: "G1", id: 1));
        api.Enqueue(Photo("b", group: "G1", id: 2, caption: "monte um post com estes prints"));

        var driver = await SingleDriverAsync();
        await Eventually(() => driver.Calls.Contains("turn:monte um post com estes prints [A000001,A000002]"));
        Assert.Equal(["start", "turn:monte um post com estes prints [A000001,A000002]"], driver.Calls);
        Assert.False(api.HasMessage);
    }

    [Fact]
    public async Task CaptionDuringAnActiveTurnIsQueuedWithItsImage()
    {
        api.Files["a"] = TestImages.Png(10, 10);
        await StartAsync();
        api.Enqueue(Text("primeira"));
        var driver = await SingleDriverAsync();
        await Eventually(() => driver.Calls.Contains("turn:primeira"));

        api.Enqueue(Photo("a", caption: "e esta tela?"));
        Assert.Equal("Recebido; envio ao agente quando a resposta atual terminar.", await api.NextMessageAsync());
        Assert.Equal(1, sessions!.GetActive(123)!.QueuedCount);

        driver.Emit(new TurnStartedEvent());
        driver.Emit(new TurnCompletedEvent(AgentTurnOutcome.Completed));
        await Eventually(() => driver.Calls.Contains("turn:e esta tela? [A000001]"));
    }

    [Fact]
    public async Task SteerCarriesThePendingImagesOfTheActiveSession()
    {
        api.Files["a"] = TestImages.Png(10, 10);
        await StartAsync(defaultAgent: AgentKind.Codex);
        api.Enqueue(Text("tarefa longa"));
        var driver = await SingleDriverAsync();
        await Eventually(() => driver.Calls.Contains("turn:tarefa longa"));

        api.Enqueue(Photo("a"));
        Assert.StartsWith("Recebi 1 imagem.", await api.NextMessageAsync());
        api.Enqueue(Text("/steer use este layout"));
        Assert.StartsWith("Orientação enviada", await api.NextMessageAsync());
        Assert.Contains("steer:use este layout [A000001]", driver.Calls);
        Assert.True(File.Exists(Path.Combine(Attachments, "123", "S000001", "A000001.png")));
    }

    [Fact]
    public async Task OneShotCaptionRunsWithTheImagesAndRemovesThemWhenTheJobEnds()
    {
        api.Files["a"] = TestImages.Png(10, 10);
        await StartAsync();

        api.Enqueue(Photo("a", caption: "/codex descreva a tela"));
        Assert.Equal("Codex iniciado. Job ID: J000001 (General), 1 imagem.\n" +
            "Override só desta execução: o agente padrão continua Claude.", await api.NextMessageAsync());
        Assert.Contains("Codex concluído", await api.NextMessageAsync());

        var run = Assert.Single(runner.Runs);
        Assert.Equal("descreva a tela", run.Prompt);
        var image = Assert.Single(run.Attachments);
        Assert.Equal(Path.Combine(Attachments, "123", "J000001", "A000001.png"), image.Path);
        Assert.True(run.FilesExisted);
        Assert.False(Directory.Exists(Path.Combine(Attachments, "123", "J000001")));
        Assert.Empty(drivers.Created);
    }

    [Fact]
    public async Task CaptionWithAnotherCommandKeepsTheImagesPending()
    {
        api.Files["a"] = TestImages.Png(10, 10);
        await StartAsync();

        api.Enqueue(Photo("a", caption: "/status"));
        Assert.Equal("Comando desconhecido na legenda: /status. As imagens continuam pendentes; envie o pedido em texto.",
            await api.NextMessageAsync());
        Assert.Single(pending!.Get(123)!.Items);
        Assert.Empty(drivers.Created);
        Assert.Empty(runner.Runs);
    }

    [Fact]
    public async Task ImagesTheAgentCannotReceiveAreRefusedAndDeleted()
    {
        drivers.Configure = driver => driver.Capabilities = driver.Capabilities with { ImageInput = false };
        api.Files["a"] = TestImages.Png(10, 10);
        await StartAsync();

        api.Enqueue(Photo("a", caption: "veja"));
        Assert.Equal("O Claude não recebe imagens nesta sessão; a mensagem não foi enviada.", await api.NextMessageAsync());
        var driver = await SingleDriverAsync();
        Assert.Equal(["start"], driver.Calls);
        Assert.Empty(Directory.GetFiles(Attachments, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task AnotherUsersTextNeverTakesThePendingImages()
    {
        api.Files["a"] = TestImages.Png(10, 10);
        await StartAsync();

        api.Enqueue(Photo("a"));
        await api.NextMessageAsync();
        api.Enqueue(Text("olá", sender: 456));
        await Eventually(() => drivers.Created.Count == 1 && drivers.Created[0].Calls.Contains("turn:olá"));

        Assert.Single(pending!.Get(123)!.Items);
    }

    private async Task StartAsync(AgentKind defaultAgent = AgentKind.Claude)
    {
        var options = Options.Create(new TelegramOptions { BotToken = "test", AllowedUserIds = "123,456" });
        var store = new AttachmentStore(Attachments);
        pending = new PendingAttachments(store);
        var settings = new AssistantSettingsStore(Path.Combine(root, "settings.json"));
        settings.SetDefaultAgent(defaultAgent);
        var delivery = new TelegramDeliveryService(api, NullLogger<TelegramDeliveryService>.Instance);
        sessions = new SessionRegistry(drivers, NullLogger<SessionRegistry>.Instance, delivery, attachments: store);
        service = new TelegramPollingService(api, options, new TelegramUserAuthorizer(options), runner, runner,
            new JobRegistry(), NullLogger<TelegramPollingService>.Instance, null,
            new GeneralWorkspace(Path.Combine(root, "general")), settings, sessions, delivery,
            attachments: store, pendingAttachments: pending);
        await service.StartAsync(CancellationToken.None);
    }

    private async Task<FakeSessionDriver> SingleDriverAsync()
    {
        await Eventually(() => drivers.Created.Count == 1);
        return drivers.Created[0];
    }

    private static TelegramMessage Text(string text, long sender = 123) =>
        new(new TelegramChat(1), text, new TelegramUser(sender));

    private static TelegramMessage Photo(string fileId, string? caption = null, string? group = null, long id = 0) =>
        Message(photo: [new TelegramPhotoSize(fileId, 10, 10)], caption: caption, group: group, id: id);

    private static TelegramMessage Message(IReadOnlyList<TelegramPhotoSize>? photo = null, TelegramFileInfo? document = null,
        string? caption = null, string? group = null, long id = 0) =>
        new(new TelegramChat(1), null, new TelegramUser(123), id, caption, group, photo, document);

    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(50);
        Assert.True(condition());
    }

    public async ValueTask DisposeAsync()
    {
        if (service is not null)
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }
        if (sessions is not null) await sessions.DisposeAsync();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed record Run(string Prompt, IReadOnlyList<Attachment> Attachments, bool FilesExisted);

    private sealed class RecordingRunner : ICodexRunner, IClaudeRunner
    {
        private readonly ConcurrentQueue<Run> runs = new();

        public IReadOnlyList<Run> Runs => runs.ToArray();

        public Task<AgentProcessResult> RunAsync(string prompt, string workingDirectory,
            CancellationToken cancellationToken = default, bool generalMode = false,
            IReadOnlyDictionary<string, string>? environment = null, string? model = null, string? effort = null,
            IReadOnlyList<Attachment>? attachments = null)
        {
            attachments ??= [];
            runs.Enqueue(new Run(prompt, attachments, attachments.All(attachment => File.Exists(attachment.Path))));
            return Task.FromResult(new AgentProcessResult(AgentProcessStatus.Succeeded, "done", "", 0,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        }
    }

    private sealed class BotApi : ITelegramBotApi
    {
        private readonly Channel<TelegramUpdate> updates = Channel.CreateUnbounded<TelegramUpdate>();
        private readonly Channel<string> messages = Channel.CreateUnbounded<string>();
        private long nextId;

        public ConcurrentDictionary<string, byte[]> Files { get; } = new();
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
            if (!Files.TryGetValue(fileId, out var content)) throw new HttpRequestException("404");
            await destination.WriteAsync(content, cancellationToken);
            return content.Length;
        }
    }
}
