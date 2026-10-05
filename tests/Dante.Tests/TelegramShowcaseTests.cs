using System.Collections.Concurrent;
using System.Threading.Channels;
using Dante.Application.Agentes;
using Dante.Application.Anexos;
using Dante.Infrastructure.Contextos;
using Dante.Worker.Artifacts;
using Dante.Worker.Attachments;
using Dante.Worker.Jobs;
using Dante.Worker.Sessions;
using Dante.Worker.Telegram;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dante.Tests;

// Showcase images for LinkedIn (#98): /vitrine asks the conversation's agent only for texts and arrangement, the
// D.A.N.T.E. renders the prints unchanged and sends a PNG; adjustments in the same conversation return new versions.
public sealed class TelegramShowcaseTests : IAsyncDisposable
{
    private const string Spec = """
        ```json
        {"format":"landscape","title":"Alto contraste","subtitle":"Dois modos de leitura.","accent":"#1B4D3E",
         "prints":[{"id":"A000001","label":"Modo normal"},{"id":"A000002","label":"Alto contraste","highlight":true}],
         "footer":"site.example"}
        ```
        """;
    private readonly string root = Path.Combine(Path.GetTempPath(), "dante-showcase-" + Guid.NewGuid().ToString("N"));
    private readonly FakeSessionDriverFactory drivers = new();
    private readonly BotApi api = new();
    private readonly FakeRenderer renderer = new();
    private PendingAttachments? pending;
    private SessionRegistry? sessions;
    private TelegramPollingService? service;

    private string Attachments => Path.Combine(root, "attachments");

    [Fact]
    public async Task PrintsThenVitrineSendTheRenderedImageInsteadOfTheAgentsJson()
    {
        await StartAsync();
        await SendPrintsAsync();

        api.Enqueue(Text("/vitrine mostre o modo alto contraste"));
        var driver = await SingleDriverAsync();
        await Eventually(() => driver.Calls.Count == 2);
        var turn = driver.Calls[1];
        Assert.StartsWith("turn:mostre o modo alto contraste\n\n[Pedido de vitrine do D.A.N.T.E.] Não gere nem edite imagens", turn);
        Assert.Contains("\n- A000001 (normal.png), 64×40\n- A000002, 64×40 [A000001,A000002]", turn);

        driver.Emit(new TurnStartedEvent());
        driver.Emit(new MessageDeltaEvent("m1", "```json\n{\"format\":"));
        driver.Emit(new MessageCompletedEvent("m1", Spec));
        driver.Emit(new TurnCompletedEvent(AgentTurnOutcome.Completed));

        var reply = await api.NextMessageAsync();
        Assert.Equal("Vitrine v1 (F000001, paisagem 1600×900): \"Alto contraste\". Os prints foram colados sem " +
            "alteração. Para ajustar, use /vitrine <instruções>.\n", reply);
        await Eventually(() => api.Uploads.Count == 2);
        Assert.Equal([("vitrine-T000001-v1.png", true, "F000001 · vitrine-T000001-v1.png"),
            ("vitrine-T000001-v1.png", false, "F000001 · vitrine-T000001-v1.png")], api.Uploads);
        var (spec, prints) = Assert.Single(renderer.Renders);
        Assert.Equal(["A000001", "A000002"], spec.Prints.Select(print => print.Id));
        Assert.All(prints, print => Assert.StartsWith(Path.Combine(Attachments, "123", "S000001"), print.Path));
        Assert.Null(pending!.Get(123));
    }

    [Fact]
    public async Task AnAdjustmentInTheSameConversationReturnsANewVersion()
    {
        await StartAsync();
        await SendPrintsAsync();
        api.Enqueue(Text("/vitrine mostre o modo alto contraste"));
        var driver = await SingleDriverAsync();
        await CompleteAsync(driver, 2, Spec);
        Assert.StartsWith("Vitrine v1", await api.NextMessageAsync());
        await Eventually(() => sessions!.GetActive(123)!.State == AgentSessionState.Idle);

        api.Enqueue(Text("/vitrine título mais curto, formato quadrado"));
        await Eventually(() => driver.Calls.Count == 3);
        Assert.StartsWith("turn:título mais curto, formato quadrado\n\n[Pedido de vitrine", driver.Calls[2]);
        Assert.Contains("\n- A000001, 64×40\n- A000002, 64×40", driver.Calls[2]);
        await CompleteAsync(driver, 3, Spec.Replace("landscape", "square").Replace("Alto contraste\",\"subtitle", "Contraste\",\"subtitle"));

        Assert.Equal("Vitrine v2 (F000002, quadrado 1400×1400): \"Contraste\". Os prints foram colados sem alteração. " +
            "Para ajustar, use /vitrine <instruções>.\n", await api.NextMessageAsync());
        await Eventually(() => api.Uploads.Any(upload => upload.Name == "vitrine-T000002-v2.png"));
        Assert.Single(drivers.Created);
    }

    [Fact]
    public async Task AnAnswerWithoutASpecIsShownWithWhatWasMissing()
    {
        await StartAsync();
        await SendPrintsAsync();
        api.Enqueue(Text("/vitrine faça algo bonito"));
        var driver = await SingleDriverAsync();

        await CompleteAsync(driver, 2, "Qual dos dois prints deve ficar em destaque?");

        Assert.Equal("Qual dos dois prints deve ficar em destaque?\n\nNão montei a imagem: a resposta do agente não " +
            "trouxe o JSON da vitrine. Peça um ajuste com /vitrine <instruções>.\n", await api.NextMessageAsync());
        Assert.Empty(renderer.Renders);
        Assert.Empty(api.Uploads);
    }

    [Fact]
    public async Task ARenderFailureIsReportedWithoutUpload()
    {
        await StartAsync();
        await SendPrintsAsync();
        renderer.Failure = "o ffmpeg terminou com código 1";
        api.Enqueue(Text("/vitrine mostre o modo alto contraste"));
        var driver = await SingleDriverAsync();

        await CompleteAsync(driver, 2, Spec);

        Assert.Equal("Não montei a imagem: o ffmpeg terminou com código 1.\n", await api.NextMessageAsync());
        Assert.Empty(api.Uploads);
    }

    [Fact]
    public async Task VitrineAsTheCaptionOfAPrintIsTheRequest()
    {
        await StartAsync();
        api.Files["a"] = TestImages.Png(64, 40);

        api.Enqueue(Photo("a", caption: "/vitrine destaque o cadastro"));

        var driver = await SingleDriverAsync();
        await Eventually(() => driver.Calls.Count == 2);
        Assert.StartsWith("turn:destaque o cadastro\n\n[Pedido de vitrine", driver.Calls[1]);
        Assert.EndsWith("- A000001, 64×40 [A000001]", driver.Calls[1]);
    }

    [Fact]
    public async Task WithoutPrintsOrToolsOrWhileBusyNothingIsSentToTheAgent()
    {
        await StartAsync();

        api.Enqueue(Text("/vitrine mostre o site"));
        Assert.StartsWith("Nenhum print nesta conversa", await api.NextMessageAsync());
        Assert.Empty(drivers.Created);

        renderer.Missing = "ffmpeg não encontrado no PATH; instale o ffmpeg (sudo apt install ffmpeg)";
        await SendPrintsAsync();
        api.Enqueue(Text("/vitrine mostre o site"));
        Assert.Equal("Vitrine indisponível: ffmpeg não encontrado no PATH; instale o ffmpeg (sudo apt install ffmpeg).",
            await api.NextMessageAsync());
        Assert.Empty(drivers.Created);
        renderer.Missing = null;

        api.Enqueue(Text("tarefa longa"));
        var driver = await SingleDriverAsync();
        await Eventually(() => driver.Calls.Any(call => call.StartsWith("turn:tarefa longa")));
        api.Enqueue(Text("/vitrine mostre o site"));
        Assert.Equal("A sessão S000001 ainda está respondendo. Aguarde terminar (ou use /session stop) e peça a vitrine " +
            "de novo; os prints pendentes continuam guardados.", await api.NextMessageAsync());
        Assert.Equal(2, driver.Calls.Count);
    }

    private async Task SendPrintsAsync()
    {
        api.Files["a"] = TestImages.Png(64, 40);
        api.Files["b"] = TestImages.Png(64, 40);
        api.Enqueue(Message(document: new TelegramFileInfo("a", 33, "image/png", "normal.png")));
        Assert.StartsWith("Recebi 1 imagem.", await api.NextMessageAsync());
        api.Enqueue(Photo("b"));
        Assert.StartsWith("Recebi 1 imagem; 2 pendentes", await api.NextMessageAsync());
    }

    private static async Task CompleteAsync(FakeSessionDriver driver, int calls, string reply)
    {
        await Eventually(() => driver.Calls.Count == calls);
        driver.Emit(new TurnStartedEvent());
        driver.Emit(new MessageCompletedEvent("m", reply));
        driver.Emit(new TurnCompletedEvent(AgentTurnOutcome.Completed));
    }

    private async Task StartAsync()
    {
        var options = Options.Create(new TelegramOptions { BotToken = "test", AllowedUserIds = "123" });
        var store = new AttachmentStore(Attachments);
        pending = new PendingAttachments(store);
        var artifacts = new ArtifactStore(Path.Combine(root, "artifacts"), Path.Combine(root, "codex", "generated_images"));
        var delivery = new TelegramDeliveryService(api, NullLogger<TelegramDeliveryService>.Instance, artifacts);
        var showcase = new TelegramShowcase(delivery, delivery, renderer, store, NullLogger<TelegramShowcase>.Instance);
        sessions = new SessionRegistry(drivers, NullLogger<SessionRegistry>.Instance, showcase, attachments: store);
        service = new TelegramPollingService(api, options, new TelegramUserAuthorizer(options), new NoRunner(),
            new NoRunner(), new JobRegistry(), NullLogger<TelegramPollingService>.Instance, null,
            new GeneralWorkspace(Path.Combine(root, "general")), new AssistantSettingsStore(Path.Combine(root, "settings.json")),
            sessions, delivery, attachments: store, pendingAttachments: pending, artifacts: artifacts, showcase: showcase);
        await service.StartAsync(CancellationToken.None);
    }

    private async Task<FakeSessionDriver> SingleDriverAsync()
    {
        await Eventually(() => drivers.Created.Count == 1);
        return drivers.Created[0];
    }

    private static TelegramMessage Text(string text) => new(new TelegramChat(1), text, new TelegramUser(123));

    private static TelegramMessage Photo(string fileId, string? caption = null) =>
        Message(photo: [new TelegramPhotoSize(fileId, 64, 40)], caption: caption);

    private static TelegramMessage Message(IReadOnlyList<TelegramPhotoSize>? photo = null, TelegramFileInfo? document = null,
        string? caption = null) =>
        new(new TelegramChat(1), null, new TelegramUser(123), 0, caption, null, photo, document);

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

    // Writes a real PNG header where the image would go, so the delivery accepts and uploads it.
    private sealed class FakeRenderer : IShowcaseRenderer
    {
        private readonly ConcurrentQueue<(ShowcaseSpec, IReadOnlyList<ShowcaseImage>)> renders = new();

        public string? Missing { get; set; }
        public string? Failure { get; set; }
        public IReadOnlyList<(ShowcaseSpec Spec, IReadOnlyList<ShowcaseImage> Prints)> Renders => renders.ToArray();

        public string? Unavailable() => Missing;

        public Task RenderAsync(ShowcaseSpec spec, IReadOnlyList<ShowcaseImage> prints, string outputPath,
            CancellationToken cancellationToken)
        {
            if (Failure is not null) throw new ShowcaseRenderException(Failure);
            renders.Enqueue((spec, prints));
            var (width, height) = ShowcaseLayoutBuilder.Canvas(spec.Format);
            File.WriteAllBytes(outputPath, TestImages.Png(width, height));
            return Task.CompletedTask;
        }
    }

    private sealed class NoRunner : ICodexRunner, IClaudeRunner
    {
        public Task<AgentProcessResult> RunAsync(string prompt, string workingDirectory,
            CancellationToken cancellationToken = default, bool generalMode = false,
            IReadOnlyDictionary<string, string>? environment = null, string? model = null, string? effort = null,
            IReadOnlyList<Attachment>? attachments = null) =>
            throw new InvalidOperationException("Nenhum agente one-shot deve rodar nestes testes.");
    }

    private sealed class BotApi : ITelegramBotApi
    {
        private readonly Channel<TelegramUpdate> updates = Channel.CreateUnbounded<TelegramUpdate>();
        private readonly Channel<string> messages = Channel.CreateUnbounded<string>();
        private readonly ConcurrentQueue<(string, bool, string?)> uploads = new();
        private long nextId;

        public ConcurrentDictionary<string, byte[]> Files { get; } = new();
        public IReadOnlyList<(string Name, bool AsPhoto, string? Caption)> Uploads => uploads.ToArray();

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

        public Task SendFileAsync(long chatId, TelegramFileUpload upload, CancellationToken cancellationToken)
        {
            Assert.True(File.Exists(upload.Path));
            uploads.Enqueue((upload.FileName, upload.AsPhoto, upload.Caption));
            return Task.CompletedTask;
        }
    }
}
