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
// Audio and video (#96) follow the same path and are prepared inside the turn or job, never in a steer.
public sealed class TelegramImageTurnTests : IAsyncDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "dante-image-turns-" + Guid.NewGuid().ToString("N"));
    private readonly FakeSessionDriverFactory drivers = new();
    private readonly BotApi api = new();
    private readonly RecordingRunner runner = new();
    private readonly FakeMediaTools tools = new();
    private PendingAttachments? pending;
    private SessionRegistry? sessions;
    private AssistantSettingsStore? settings;
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

    // Review of #103: an album and its caption are one batch that keeps its place before what the sender sent next.
    [Fact]
    public async Task AlbumKeepsItsCaptionAndImagesTogetherBeforeALaterText()
    {
        api.Files["a"] = TestImages.Png(10, 10);
        await StartAsync();

        api.Enqueue(Photo("a", group: "G1", id: 1, caption: "pedido do album"));
        api.Enqueue(Text("pedido seguinte"));

        var driver = await SingleDriverAsync();
        await Eventually(() => driver.Calls.Contains("turn:pedido do album [A000001]"));
        Assert.Equal("Recebido; envio ao agente quando a resposta atual terminar.", await api.NextMessageAsync());
        driver.Emit(new TurnStartedEvent());
        driver.Emit(new TurnCompletedEvent(AgentTurnOutcome.Completed));
        await Eventually(() => driver.Calls.Contains("turn:pedido seguinte"));
        Assert.Equal(["start", "turn:pedido do album [A000001]", "turn:pedido seguinte"], driver.Calls);
    }

    [Fact]
    public async Task ConsecutiveAlbumsKeepTheirOwnCaptionsAndImages()
    {
        api.Files["a"] = TestImages.Png(10, 10);
        api.Files["b"] = TestImages.Png(20, 20);
        api.Files["c"] = TestImages.Png(30, 30);
        await StartAsync();

        api.Enqueue(Photo("a", group: "G1", id: 1, caption: "primeiro"));
        api.Enqueue(Photo("b", group: "G1", id: 2));
        api.Enqueue(Photo("c", group: "G2", id: 3, caption: "segundo"));

        var driver = await SingleDriverAsync();
        await Eventually(() => driver.Calls.Contains("turn:primeiro [A000001,A000002]"));
        Assert.Equal("Recebido; envio ao agente quando a resposta atual terminar.", await api.NextMessageAsync());
        driver.Emit(new TurnStartedEvent());
        driver.Emit(new TurnCompletedEvent(AgentTurnOutcome.Completed));
        await Eventually(() => driver.Calls.Contains("turn:segundo [A000003]"));
    }

    // Review of #103: a context command sent after an album runs after it, so the caption never runs elsewhere
    // without its images.
    [Theory]
    [InlineData("/agent set codex", "Agente padrão alterado para Codex.")]
    [InlineData("/session start codex", "Sessão S000002 iniciada com Codex")]
    public async Task ContextCommandAfterAnAlbumRunsAfterItsRequest(string command, string reply)
    {
        api.Files["a"] = TestImages.Png(10, 10);
        await StartAsync();

        api.Enqueue(Photo("a", group: "G1", id: 1, caption: "analise esta imagem"));
        api.Enqueue(Text(command));

        Assert.StartsWith(reply, await api.NextMessageAsync());
        var first = drivers.Created[0];
        Assert.Equal(["start", "turn:analise esta imagem [A000001]"], first.Calls);
        Assert.Equal(AgentKind.Claude, sessions!.List(123).Single(session => session.Id == "S000001").Agent);
        await Task.Delay(2200);
        Assert.DoesNotContain(drivers.Created, driver => driver.Calls.Contains("turn:analise esta imagem"));
        Assert.Null(pending!.Get(123));
    }

    // A context that changes by other means before the album window closes drops the images and the caption together.
    [Fact]
    public async Task AlbumWhoseContextChangedIsDroppedWithItsCaption()
    {
        api.Files["a"] = TestImages.Png(10, 10);
        await StartAsync();

        api.Enqueue(Photo("a", group: "G1", id: 1, caption: "analise esta imagem"));
        await Eventually(() => Directory.Exists(Attachments) &&
            Directory.GetFiles(Attachments, "*.png", SearchOption.AllDirectories).Length == 1);
        settings!.SetDefaultAgent(AgentKind.Codex);

        Assert.Equal("1 imagem descartada com a legenda: o contexto da conversa mudou antes de o álbum terminar.",
            await api.NextMessageAsync());
        Assert.Empty(drivers.Created);
        Assert.Null(pending!.Get(123));
        Assert.Empty(Directory.GetFiles(Attachments, "*", SearchOption.AllDirectories));
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

    // #128: a steer starting with "/" would run as a Claude command. It is refused before taking the pending images,
    // interrupting the turn or touching the queue, during a turn and while idle.
    [Fact]
    public async Task ClaudeSteerWithACommandIsRefusedKeepingTurnQueueAndPendingImages()
    {
        api.Files["a"] = TestImages.Png(10, 10);
        await StartAsync();
        api.Enqueue(Text("tarefa longa"));
        var driver = await SingleDriverAsync();
        await Eventually(() => driver.Calls.Contains("turn:tarefa longa"));
        api.Enqueue(Photo("a"));
        Assert.StartsWith("Recebi 1 imagem.", await api.NextMessageAsync());

        foreach (var steer in new[] { "/steer /clear", "/steer    /compact" })
        {
            api.Enqueue(Text(steer));
            Assert.Equal(AgentInput.CommandRefusal(AgentKind.Claude) + " Os anexos pendentes continuam guardados.",
                await api.NextMessageAsync());
        }

        Assert.Equal(["start", "turn:tarefa longa"], driver.Calls);
        Assert.NotNull(pending!.Get(123));
        var session = sessions!.GetActive(123)!;
        Assert.Equal(("T000001", 0), (session.ActiveTurnId, session.QueuedCount));

        driver.Emit(new TurnStartedEvent());
        driver.Emit(new TurnCompletedEvent(AgentTurnOutcome.Completed));
        await Eventually(() => sessions.GetActive(123)!.State == AgentSessionState.Idle);
        api.Enqueue(Text("/steer /clear"));
        Assert.StartsWith(AgentInput.CommandRefusal(AgentKind.Claude), await api.NextMessageAsync());
        Assert.Equal(["start", "turn:tarefa longa"], driver.Calls);

        api.Enqueue(Text("use este print"));
        await Eventually(() => driver.Calls.Contains("turn:use este print [A000001]"));
    }

    [Fact]
    public async Task CodexSteerKeepsTextStartingWithASlash()
    {
        await StartAsync(defaultAgent: AgentKind.Codex);
        api.Enqueue(Text("tarefa longa"));
        var driver = await SingleDriverAsync();
        await Eventually(() => driver.Calls.Contains("turn:tarefa longa"));

        api.Enqueue(Text("/steer /clear"));

        Assert.StartsWith("Orientação enviada", await api.NextMessageAsync());
        Assert.Contains("steer:/clear", driver.Calls);
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
    public async Task VoiceWithAOneShotCaptionRunsWithItsTranscriptInsideTheJob()
    {
        api.Files["v"] = TestMedia.Ogg();
        await StartAsync(media: true);

        api.Enqueue(Voice("v", caption: "/codex resuma o áudio"));
        Assert.StartsWith("Codex iniciado. Job ID: J000001 (General), 1 áudio.", await api.NextMessageAsync());
        Assert.Contains("Codex concluído", await api.NextMessageAsync());

        var run = Assert.Single(runner.Runs);
        Assert.StartsWith("resuma o áudio\n\n[Anexos processados localmente pelo D.A.N.T.E.", run.Prompt);
        Assert.Contains("Áudio 1, duração 0:42. Transcrição (whisper.cpp, idioma detectado: pt):\n" +
            "[0:00–0:04] A palavra secreta é girassol.", run.Prompt);
        Assert.Empty(run.Attachments);
        Assert.Equal(["probe:A000001.ogg", "audio:A000001.ogg:42", "transcribe:A000001-audio.wav"], tools.Calls);
        Assert.False(Directory.Exists(Path.Combine(Attachments, "123", "J000001")));
    }

    [Fact]
    public async Task CancellingTheJobStopsTheTranscriptionAndTheAgentNeverRuns()
    {
        api.Files["v"] = TestMedia.Ogg();
        tools.Gate = new TaskCompletionSource();
        await StartAsync(media: true);

        api.Enqueue(Voice("v", caption: "/claude resuma"));
        Assert.StartsWith("Claude iniciado. Job ID: J000001 (General), 1 áudio.", await api.NextMessageAsync());
        await Eventually(() => tools.Calls.Count > 0);
        api.Enqueue(Text("/cancel J000001"));

        var replies = new[] { await api.NextMessageAsync(), await api.NextMessageAsync() };
        Assert.Contains(replies, reply => reply.StartsWith("Claude cancelado. Job J000001"));
        Assert.Empty(runner.Runs);
        Assert.False(Directory.Exists(Path.Combine(Attachments, "123", "J000001")));
    }

    [Fact]
    public async Task AMissingToolFailsTheJobWithTheInstallHint()
    {
        api.Files["v"] = TestMedia.Ogg();
        await StartAsync(media: true);
        api.Enqueue(Voice("v"));
        Assert.StartsWith("Recebi 1 áudio.", await api.NextMessageAsync());
        // Installed when it arrived, missing when the request came.
        tools.Missing[AttachmentKind.Audio] = "whisper-cli não encontrado no PATH; instale o whisper.cpp";

        api.Enqueue(Text("/codex resuma"));
        await api.NextMessageAsync();
        var result = await api.NextMessageAsync();

        Assert.StartsWith("Codex falhou. Job J000001 (General).", result);
        Assert.Contains("Não processei o áudio: whisper-cli não encontrado no PATH; instale o whisper.cpp.", result);
        Assert.Empty(runner.Runs);
    }

    [Fact]
    public async Task VoiceWithoutCaptionWaitsAndTheNextMessageTakesItIntoAPreparedTurn()
    {
        api.Files["v"] = TestMedia.Ogg();
        await StartAsync(media: true);

        api.Enqueue(Voice("v"));
        var receipt = await api.NextMessageAsync();
        Assert.StartsWith("Recebi 1 áudio.\nEnvie o pedido em texto: os arquivos vão junto com a próxima mensagem", receipt);
        Assert.Equal(AttachmentKind.Audio, Assert.Single(pending!.Get(123)!.Items).Kind);

        api.Enqueue(Text("o que eu disse?"));
        var driver = await SingleDriverAsync();
        await Eventually(() => driver.Calls.Any(call => call.StartsWith("turn:o que eu disse?\n\n[Anexos processados")));
        Assert.True(File.Exists(Path.Combine(Attachments, "123", "S000001", "A000001.ogg")));
        Assert.False(File.Exists(Path.Combine(Attachments, "123", "S000001", "A000001-audio.wav")));
        Assert.Contains("→ Processando 1 áudio localmente (transcrição)\n", await api.NextMessageAsync());
    }

    [Fact]
    public async Task SteerWithPendingAudioIsRefusedAndTheAudioStaysPending()
    {
        api.Files["v"] = TestMedia.Ogg();
        await StartAsync(AgentKind.Codex, media: true);
        api.Enqueue(Text("tarefa longa"));
        var driver = await SingleDriverAsync();
        await Eventually(() => driver.Calls.Contains("turn:tarefa longa"));

        api.Enqueue(Voice("v"));
        Assert.StartsWith("Recebi 1 áudio.", await api.NextMessageAsync());
        api.Enqueue(Text("/steer use isto"));

        Assert.Equal("Orientação não enviada: há áudio ou vídeo pendente, que vai ao agente só como mensagem comum " +
            "(entra na fila da sessão). Envie o pedido sem /steer.", await api.NextMessageAsync());
        Assert.DoesNotContain(driver.Calls, call => call.StartsWith("steer:"));
        Assert.Single(pending!.Get(123)!.Items);
    }

    [Fact]
    public async Task CaptionWithAnotherCommandKeepsTheImagesPending()
    {
        api.Files["a"] = TestImages.Png(10, 10);
        await StartAsync();

        api.Enqueue(Photo("a", caption: "/status"));
        Assert.Equal("Comando desconhecido na legenda: /status. As imagens continuam pendentes; envie o pedido em texto. Use /help para ver os comandos.",
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

    private async Task StartAsync(AgentKind defaultAgent = AgentKind.Claude, bool media = false)
    {
        var preparer = media ? new MediaPreparer(tools) : null;
        drivers.Media = preparer;
        var options = Options.Create(new TelegramOptions { BotToken = "test", AllowedUserIds = "123,456" });
        var store = new AttachmentStore(Attachments);
        pending = new PendingAttachments(store);
        settings = new AssistantSettingsStore(Path.Combine(root, "settings.json"));
        settings.SetDefaultAgent(defaultAgent);
        var delivery = new TelegramDeliveryService(api, NullLogger<TelegramDeliveryService>.Instance);
        sessions = new SessionRegistry(drivers, NullLogger<SessionRegistry>.Instance, delivery, attachments: store);
        service = new TelegramPollingService(api, options, new TelegramUserAuthorizer(options), runner, runner,
            new JobRegistry(), NullLogger<TelegramPollingService>.Instance, null,
            new GeneralWorkspace(Path.Combine(root, "general")), settings, sessions, delivery,
            attachments: store, pendingAttachments: pending, media: preparer);
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

    private static TelegramMessage Voice(string fileId, string? caption = null) =>
        Message(caption: caption) with { Voice = new TelegramFileInfo(fileId, 16, "audio/ogg") };

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
