using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;
using Dante.Application.Agentes;
using Dante.Application.Anexos;
using Dante.Infrastructure.Contextos;
using Dante.Worker.Artifacts;
using Dante.Worker.Jobs;
using Dante.Worker.Sessions;
using Dante.Worker.Telegram;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dante.Tests;

// Files produced by agents reach Telegram (#97, AD-29): only through the session's structured event or the owner's
// /send, validated against the channel's root, uploaded by multipart and recoverable by /resend without the agent.
public sealed class TelegramArtifactDeliveryTests : IAsyncDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "dante-artifact-delivery-" + Guid.NewGuid().ToString("N"));
    private readonly FakeSessionDriverFactory drivers = new();
    private readonly BotApi api = new();
    private ArtifactStore? store;
    private TelegramDeliveryService? delivery;
    private SessionRegistry? sessions;
    private TelegramPollingService? service;

    private string General => Directory.CreateDirectory(Path.Combine(root, "general")).FullName;
    private string Generated => Directory.CreateDirectory(Path.Combine(root, "codex", "generated_images", "t1")).FullName;

    [Fact]
    public async Task GeneratedImageGoesAsPreviewAndOriginalAndProseNeverUploads()
    {
        var image = Path.Combine(Generated, "ig_1.png");
        File.WriteAllBytes(image, TestImages.Png(640, 480));
        File.WriteAllText(Path.Combine(General, "citado.txt"), "x");
        var driver = await StartTurnAsync("gere a imagem");

        driver.Emit(new ArtifactProducedEvent("ig", image));
        driver.Emit(new MessageCompletedEvent("m1", $"Pronto. Também salvei {Path.Combine(General, "citado.txt")}."));
        driver.Emit(new TurnCompletedEvent(AgentTurnOutcome.Completed));

        await Eventually(() => api.Uploads.Count == 2);
        Assert.Equal([(1L, "ig_1.png", "image/png", true, "F000001 · ig_1.png"),
            (1L, "ig_1.png", "image/png", false, "F000001 · ig_1.png")], api.Uploads);
        Assert.StartsWith("Pronto.", await api.NextMessageAsync());
        await Task.Delay(500);
        Assert.Equal(2, api.Uploads.Count);
    }

    [Fact]
    public async Task GeneratedPathOutsideItsRootIsRefusedWithoutUpload()
    {
        var elsewhere = Path.Combine(General, "foto.png");
        File.WriteAllBytes(elsewhere, TestImages.Png(10, 10));
        var driver = await StartTurnAsync("gere a imagem");

        driver.Emit(new ArtifactProducedEvent("ig", elsewhere));
        driver.Emit(new TurnCompletedEvent(AgentTurnOutcome.Completed));

        Assert.Equal("Arquivo produzido não enviado: fora do diretório permitido.\n", await api.NextMessageAsync());
        Assert.Empty(api.Uploads);
    }

    [Fact]
    public async Task SendDeliversAFileOfTheSessionDirectoryAndRefusesEverythingElse()
    {
        File.WriteAllText(Path.Combine(General, "relatorio.csv"), "a,b\n");
        File.WriteAllText(Path.Combine(General, ".env"), "TOKEN=x");
        File.WriteAllText(Path.Combine(root, "fora.txt"), "x");
        await StartAsync();

        api.Enqueue("/send relatorio.csv");
        Assert.Equal("Nenhuma sessão ativa: /send envia arquivos do diretório da sessão ativa.", await api.NextMessageAsync());

        await StartConversationAsync();
        api.Enqueue("/send relatorio.csv");
        await Eventually(() => api.Uploads.Count == 1);
        Assert.Equal((1L, "relatorio.csv", "application/octet-stream", false, "F000001 · relatorio.csv"), api.Uploads[0]);

        api.Enqueue("/send ../fora.txt");
        Assert.Equal("Arquivo não enviado: fora do diretório permitido.", await api.NextMessageAsync());
        api.Enqueue("/send .env");
        Assert.Equal("Arquivo não enviado: arquivo protegido (credenciais ou configuração).", await api.NextMessageAsync());
        api.Enqueue("/send nada.txt");
        Assert.Equal("Arquivo não enviado: arquivo não encontrado.", await api.NextMessageAsync());
        api.Enqueue("/send");
        Assert.Equal("Uso: /send <caminho no diretório da sessão>", await api.NextMessageAsync());
        Assert.Single(api.Uploads);
    }

    [Fact]
    public async Task FailedUploadIsReportedAndResentWithoutTheAgent()
    {
        File.WriteAllText(Path.Combine(General, "relatorio.csv"), "a,b\n");
        var driver = await StartConversationAsync();
        api.FailUploads = HttpStatusCode.BadRequest;

        api.Enqueue("/send relatorio.csv");
        Assert.Equal("Falha ao enviar o arquivo F000001; tente /resend F000001.", await api.NextMessageAsync());
        api.Enqueue("/status");
        Assert.Contains("F000001: Failed (0/1 partes)", await api.NextMessageAsync());

        api.FailUploads = null;
        api.Enqueue("/resend F000001");
        Assert.Equal("Entrega F000001: Delivered (1/1 partes).", await api.NextMessageAsync());
        Assert.Single(api.Uploads);
        Assert.DoesNotContain(driver.Calls, call => call.Contains("relatorio"));
    }

    [Fact]
    public async Task TransientFailuresAreRetried()
    {
        File.WriteAllText(Path.Combine(General, "relatorio.csv"), "a,b\n");
        await StartConversationAsync();
        api.TransientFailures = 1;

        api.Enqueue("/send relatorio.csv");

        await Eventually(() => api.Uploads.Count == 1);
        Assert.Equal(2, api.UploadAttempts);
    }

    [Fact]
    public async Task ACopyThatIsGoneCannotBeResent()
    {
        File.WriteAllText(Path.Combine(General, "relatorio.csv"), "a,b\n");
        await StartConversationAsync();
        api.FailUploads = HttpStatusCode.BadRequest;
        api.Enqueue("/send relatorio.csv");
        await api.NextMessageAsync();

        File.Delete(Path.Combine(store!.Root, "123", "F000001.csv"));
        api.FailUploads = null;
        api.Enqueue("/resend F000001");

        Assert.Equal("Entrega F000001: Failed (0/1 partes): o arquivo não está mais disponível para reenvio.",
            await api.NextMessageAsync());
        Assert.Empty(api.Uploads);
    }

    // Review of #107: pending uploads keep their copies, and once they finish the window is back to the 50 most recent.
    [Fact]
    public async Task ABurstOfUploadsShrinksToTheResendWindowWhenItFinishes()
    {
        for (var index = 1; index <= 60; index++) File.WriteAllText(Path.Combine(General, $"f{index}.txt"), $"{index}");
        store = new ArtifactStore(Path.Combine(root, "artifacts"), Path.Combine(root, "codex", "generated_images"));
        delivery = new TelegramDeliveryService(api, NullLogger<TelegramDeliveryService>.Instance, store);
        delivery.RegisterSession("S000001", 123, 1, hideOutput: false);
        var finished = 0;
        delivery.AfterArtifactDeliveryAsync = _ =>
        {
            Interlocked.Increment(ref finished);
            return Task.CompletedTask;
        };
        api.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        for (var index = 1; index <= 60; index++)
            Assert.Null(delivery.SendFile("S000001", 123, 1, $"f{index}.txt", General).Error);
        Assert.Equal(60, delivery.ListArtifacts(123).Count);
        Assert.Equal(60, Directory.GetFiles(store.Root, "*", SearchOption.AllDirectories).Length);

        api.Gate.SetResult();
        await Eventually(() => Volatile.Read(ref finished) == 60);

        var kept = delivery.ListArtifacts(123);
        Assert.Equal(Enumerable.Range(11, 50).Select(index => $"F{index:D6}"), kept.Select(snapshot => snapshot.Id));
        Assert.All(kept, snapshot => Assert.Equal(TelegramDeliveryState.Delivered, snapshot.State));
        Assert.Equal(kept.Select(snapshot => snapshot.Id + ".txt"),
            Directory.GetFiles(store.Root, "*", SearchOption.AllDirectories).Select(Path.GetFileName).Order());
        Assert.Null(delivery.Get("F000001", 123));
    }

    // AD-10: a session with bound secrets keeps its files, whichever channel asks.
    [Fact]
    public async Task SessionWithBoundSecretsNeverUploads()
    {
        var image = Path.Combine(Generated, "ig_1.png");
        File.WriteAllBytes(image, TestImages.Png(10, 10));
        File.WriteAllText(Path.Combine(General, "relatorio.csv"), "a,b\n");
        store = new ArtifactStore(Path.Combine(root, "artifacts"), Path.Combine(root, "codex", "generated_images"));
        delivery = new TelegramDeliveryService(api, NullLogger<TelegramDeliveryService>.Instance, store);
        delivery.RegisterSession("S000001", 123, 1, hideOutput: true);
        delivery.SetActiveSession(123, "S000001");
        var session = new AgentSessionSnapshot("S000001", AgentKind.Codex, 123, JobExecutionContext.General(General),
            AgentPermissionProfile.Manual, AgentSessionState.Running, "T000001", 0, [], true, DateTimeOffset.UtcNow, null,
            null);

        await delivery.PublishAsync(session, new ArtifactProducedEvent("ig", image) { SessionId = "S000001", TurnId = "T000001" },
            CancellationToken.None);
        var (_, error) = delivery.SendFile("S000001", 123, 1, "relatorio.csv", General);

        Assert.Equal("a sessão tem segredos vinculados ao ambiente, e arquivos não saem dela", error);
        Assert.Equal("Arquivo produzido não enviado: a sessão tem segredos vinculados ao ambiente.\n",
            await api.NextMessageAsync());
        Assert.Empty(api.Uploads);
        Assert.False(Directory.Exists(store.Root));
    }

    // A new turn of the conversation, so the driver's events belong to it.
    private async Task<FakeSessionDriver> StartTurnAsync(string text)
    {
        var driver = await StartConversationAsync();
        api.Enqueue(text);
        await Eventually(() => driver.Calls.Contains("turn:" + text));
        driver.Emit(new TurnStartedEvent());
        return driver;
    }

    private async Task<FakeSessionDriver> StartConversationAsync()
    {
        if (service is null) await StartAsync();
        api.Enqueue("olá");
        await Eventually(() => drivers.Created.Count == 1 && drivers.Created[0].Calls.Contains("turn:olá"));
        var driver = drivers.Created[0];
        driver.Emit(new TurnStartedEvent());
        driver.Emit(new MessageCompletedEvent("m0", "Oi."));
        driver.Emit(new TurnCompletedEvent(AgentTurnOutcome.Completed));
        Assert.Equal("Oi.\n", await api.NextMessageAsync());
        await Eventually(() => sessions!.GetActive(123)!.State == AgentSessionState.Idle);
        return driver;
    }

    private async Task StartAsync()
    {
        var options = Options.Create(new TelegramOptions { BotToken = "test", AllowedUserIds = "123" });
        store = new ArtifactStore(Path.Combine(root, "artifacts"), Path.Combine(root, "codex", "generated_images"));
        delivery = new TelegramDeliveryService(api, NullLogger<TelegramDeliveryService>.Instance, store);
        sessions = new SessionRegistry(drivers, NullLogger<SessionRegistry>.Instance, delivery);
        service = new TelegramPollingService(api, options, new TelegramUserAuthorizer(options), new NoRunner(),
            new NoRunner(), new JobRegistry(), NullLogger<TelegramPollingService>.Instance, null,
            new GeneralWorkspace(General), new AssistantSettingsStore(Path.Combine(root, "settings.json")), sessions,
            delivery, artifacts: store);
        await service.StartAsync(CancellationToken.None);
    }

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

    private sealed class NoRunner : ICodexRunner, IClaudeRunner
    {
        public Task<AgentProcessResult> RunAsync(string prompt, string workingDirectory,
            CancellationToken cancellationToken = default, bool generalMode = false,
            IReadOnlyDictionary<string, string>? environment = null, string? model = null, string? effort = null,
            IReadOnlyList<Dante.Application.Anexos.Attachment>? attachments = null) =>
            throw new InvalidOperationException("Nenhum agente one-shot deve rodar nestes testes.");
    }

    private sealed class BotApi : ITelegramBotApi
    {
        private readonly Channel<TelegramUpdate> updates = Channel.CreateUnbounded<TelegramUpdate>();
        private readonly Channel<string> messages = Channel.CreateUnbounded<string>();
        private readonly ConcurrentQueue<(long, string, string, bool, string?)> uploads = new();
        private long nextId;
        private int uploadAttempts;

        public IReadOnlyList<(long ChatId, string Name, string MediaType, bool AsPhoto, string? Caption)> Uploads =>
            uploads.ToArray();
        public int UploadAttempts => uploadAttempts;
        public HttpStatusCode? FailUploads { get; set; }
        // Holds every upload until the test releases it.
        public TaskCompletionSource? Gate { get; set; }
        public int TransientFailures { get; set; }

        public void Enqueue(string text) => updates.Writer.TryWrite(new TelegramUpdate(Interlocked.Increment(ref nextId),
            new TelegramMessage(new TelegramChat(1), text, new TelegramUser(123))));

        public async Task<string> NextMessageAsync() =>
            await messages.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        public async Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(long offset, CancellationToken cancellationToken) =>
            [await updates.Reader.ReadAsync(cancellationToken)];

        public Task SendMessageAsync(long chatId, string text, CancellationToken cancellationToken)
        {
            messages.Writer.TryWrite(text);
            return Task.CompletedTask;
        }

        public async Task SendFileAsync(long chatId, TelegramFileUpload upload, CancellationToken cancellationToken)
        {
            if (Gate is { } gate) await gate.Task;
            Interlocked.Increment(ref uploadAttempts);
            if (FailUploads is { } status) throw new HttpRequestException("falha simulada", null, status);
            if (TransientFailures > 0)
            {
                TransientFailures--;
                throw new HttpRequestException("falha transitória", null, HttpStatusCode.BadGateway);
            }
            Assert.True(File.Exists(upload.Path));
            uploads.Enqueue((chatId, upload.FileName, upload.MediaType, upload.AsPhoto, upload.Caption));
        }
    }
}
