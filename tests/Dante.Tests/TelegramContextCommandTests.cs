using System.Collections.Concurrent;
using System.Threading.Channels;
using Dante.Application.Agentes;
using Dante.Application.Anexos;
using Dante.Infrastructure.Contextos;
using Dante.Worker.Attachments;
using Dante.Worker.Jobs;
using Dante.Worker.Sessions;
using Dante.Worker.Telegram;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dante.Tests;

// /clear (#120) and /compact (#121) through the Telegram polling loop (AD-32): only the owner's active, idle session;
// never a prompt. Clear keeps the session and settings with a new upstream conversation and drops the pending
// attachments of the old one; compact is acknowledged at once and its outcome arrives when the agent confirms it.
public sealed class TelegramContextCommandTests : IAsyncDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "dante-context-" + Guid.NewGuid().ToString("N"));
    private readonly FakeSessionDriverFactory drivers = new();
    private readonly BotApi api = new();
    private PendingAttachments? pending;
    private SessionRegistry? sessions;
    private TelegramPollingService? service;

    [Fact]
    public async Task WrongSyntaxOrNoActiveSessionNeverReachesAnAgent()
    {
        await StartAsync();

        api.Enqueue(Text("/clear tudo"));
        Assert.Equal("Uso: /clear (sem argumentos) — limpa a conversa da sessão ativa.", await api.NextMessageAsync());
        api.Enqueue(Text("/clear"));
        Assert.Equal("Nenhuma sessão ativa para limpar. Use /session start para começar uma conversa.",
            await api.NextMessageAsync());
        Assert.Empty(drivers.Created);
    }

    [Fact]
    public async Task ClearKeepsTheSessionAndItsSettingsAndDropsThePendingImages()
    {
        api.Files["a"] = TestImages.Png(10, 10);
        await StartAsync();
        api.Enqueue(Text("/session start claude plan model=opus effort=high"));
        Assert.Contains("S000001", await api.NextMessageAsync());
        var driver = drivers.Created.Single();
        api.Enqueue(Photo("a"));
        Assert.StartsWith("Recebi 1 imagem.", await api.NextMessageAsync());

        api.Enqueue(Text("/clear"));

        Assert.Equal("Conversa da sessão S000001 limpa: a próxima mensagem começa do zero com o Claude, sem o histórico " +
                     "anterior.\nMantidos: General, modo plan, modelo opus, esforço high.\n" +
                     "Nova conversa upstream: upstream-cleared-1.\n" +
                     "1 imagem(ns) pendente(s) descartada(s) da conversa anterior.\n" +
                     "Arquivos e instruções do repositório continuam disponíveis; limpar a conversa não renova as cotas " +
                     "de uso.", await api.NextMessageAsync());
        Assert.Null(pending!.Get(123));
        Assert.Equal(["start", "clear"], driver.Calls);
        api.Enqueue(Text("nova conversa"));
        await Eventually(() => driver.Calls.Contains("turn:nova conversa"));
        Assert.Single(drivers.Created);
    }

    [Fact]
    public async Task BusySessionRefusesAndKeepsTurnAndPendingImages()
    {
        api.Files["a"] = TestImages.Png(10, 10);
        await StartAsync();
        api.Enqueue(Text("tarefa longa"));
        await Eventually(() => drivers.Created.Count == 1 && drivers.Created[0].Calls.Contains("turn:tarefa longa"));
        var driver = drivers.Created[0];
        api.Enqueue(Photo("a"));
        Assert.StartsWith("Recebi 1 imagem.", await api.NextMessageAsync());

        api.Enqueue(Text("/clear"));

        Assert.StartsWith("Só é possível limpar a conversa com a sessão ociosa.", await api.NextMessageAsync());
        Assert.DoesNotContain("clear", driver.Calls);
        Assert.NotNull(pending!.Get(123));
        Assert.Equal("T000001", sessions!.GetActive(123)!.ActiveTurnId);
    }

    [Fact]
    public async Task ClearOfAnotherUserNeverTouchesTheSession()
    {
        await StartAsync();
        api.Enqueue(Text("/session start codex"));
        Assert.Contains("S000001", await api.NextMessageAsync());

        api.Enqueue(Text("/clear", sender: 456));

        Assert.StartsWith("Nenhuma sessão ativa para limpar.", await api.NextMessageAsync());
        Assert.Equal(["start"], drivers.Created.Single().Calls);
    }

    [Fact]
    public async Task UncertainClearEndsTheSessionInsteadOfUsingAnUnknownContext()
    {
        drivers.Configure = driver => driver.ClearFailure = new TimeoutException();
        await StartAsync();
        api.Enqueue(Text("/session start codex"));
        Assert.Contains("S000001", await api.NextMessageAsync());

        api.Enqueue(Text("/clear"));

        var reply = await api.NextMessageAsync();
        while (!reply.Contains("contexto incerto")) reply = await api.NextMessageAsync();
        api.Enqueue(Text("continua"));
        string refused;
        do refused = await api.NextMessageAsync();
        while (!refused.StartsWith("A sessão S000001 foi encerrada", StringComparison.Ordinal));
        Assert.DoesNotContain(drivers.Created.Single().Calls, call => call.StartsWith("turn:"));
    }

    [Fact]
    public async Task CompactIsAcknowledgedAndItsOutcomeReportsTheAgentsTokenCounts()
    {
        await StartAsync();
        var driver = await AnsweredSessionAsync("claude");

        api.Enqueue(Text("/compact"));

        Assert.Equal("Compactando a conversa da sessão S000001 com o Claude… Aviso quando terminar. Até lá, mensagens " +
                     "para esta sessão são recusadas; /session stop cancela a compactação.", await api.NextMessageAsync());
        Assert.Equal("Conversa da sessão S000001 compactada: o Claude segue a mesma conversa a partir de um resumo do " +
                     "que foi feito e das instruções; agente, contexto, modo, modelo e esforço não mudaram.\n" +
                     "Contexto informado pelo Claude: 5.201 → 592 tokens.\n" +
                     "Compactar não apaga a conversa (para isso, /clear) nem renova as cotas de uso.",
            await api.NextMessageAsync());
        Assert.Contains("compact", driver.Calls);
    }

    [Fact]
    public async Task CompactWithoutMetricsSaysSoInsteadOfInventingThem()
    {
        drivers.Configure = driver => driver.Compacted = new AgentContextCompacted();
        await StartAsync();
        await AnsweredSessionAsync("codex");

        api.Enqueue(Text("/compact"));

        await api.NextMessageAsync();
        var done = await api.NextMessageAsync();
        Assert.Contains("O Codex não informa o tamanho do contexto antes e depois da compactação.", done);
        Assert.DoesNotContain("tokens", done);
    }

    [Fact]
    public async Task MessagesWaitForTheCompactionAndStopCancelsIt()
    {
        api.Files["a"] = TestImages.Png(10, 10);
        drivers.Configure = driver =>
            driver.CompactGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await StartAsync();
        var driver = await AnsweredSessionAsync("codex");
        api.Enqueue(Text("/compact"));
        Assert.StartsWith("Compactando", await api.NextMessageAsync());
        api.Enqueue(Photo("a"));
        Assert.StartsWith("Recebi 1 imagem.", await api.NextMessageAsync());

        api.Enqueue(Text("mais uma coisa"));
        Assert.Equal("A conversa da sessão S000001 está sendo compactada. Aguarde o aviso de conclusão ou use " +
                     "/session stop para cancelar a compactação. Os anexos pendentes continuam guardados.",
            await api.NextMessageAsync());
        api.Enqueue(Text("/status"));
        Assert.Contains("| compactando", await api.NextMessageAsync());
        Assert.NotNull(pending!.Get(123));

        api.Enqueue(Text("/session stop"));
        var replies = new[] { await api.NextMessageAsync(), await api.NextMessageAsync() };
        Assert.Contains("Cancelamento da compactação solicitado para S000001; aviso quando terminar.", replies);
        Assert.Contains("Compactação da sessão S000001 não concluída: A compactação foi cancelada; a conversa " +
                        "anterior foi mantida.", replies);
        Assert.DoesNotContain(driver.Calls, call => call == "turn:mais uma coisa");
    }

    [Fact]
    public async Task CompactSyntaxSessionAndNothingToCompactAreRefusedLocally()
    {
        await StartAsync();
        api.Enqueue(Text("/compact agora"));
        Assert.Equal("Uso: /compact (sem argumentos) — compacta a conversa da sessão ativa.", await api.NextMessageAsync());
        api.Enqueue(Text("/compact"));
        Assert.StartsWith("Nenhuma sessão ativa para compactar.", await api.NextMessageAsync());
        api.Enqueue(Text("/session start claude"));
        await api.NextMessageAsync();
        api.Enqueue(Text("/compact"));
        Assert.StartsWith("Nada a compactar", await api.NextMessageAsync());
        Assert.DoesNotContain("compact", drivers.Created.Single().Calls);
    }

    // Opens a session with the agent and lets one turn complete, so the conversation has something to compact.
    private async Task<FakeSessionDriver> AnsweredSessionAsync(string agent)
    {
        api.Enqueue(Text($"/session start {agent}"));
        Assert.Contains("S000001", await api.NextMessageAsync());
        var driver = drivers.Created.Single();
        api.Enqueue(Text("primeira"));
        await Eventually(() => driver.Calls.Contains("turn:primeira"));
        driver.Emit(new TurnStartedEvent());
        driver.Emit(new MessageCompletedEvent("m1", "resposta"));
        driver.Emit(new TurnCompletedEvent(AgentTurnOutcome.Completed));
        Assert.Equal("resposta\n", await api.NextMessageAsync());
        await Eventually(() => sessions!.GetActive(123)!.State == AgentSessionState.Idle);
        return driver;
    }

    private async Task StartAsync()
    {
        var options = Options.Create(new TelegramOptions { BotToken = "test", AllowedUserIds = "123,456" });
        var store = new AttachmentStore(Path.Combine(root, "attachments"));
        pending = new PendingAttachments(store);
        var settings = new AssistantSettingsStore(Path.Combine(root, "settings.json"));
        var delivery = new TelegramDeliveryService(api, NullLogger<TelegramDeliveryService>.Instance);
        sessions = new SessionRegistry(drivers, NullLogger<SessionRegistry>.Instance, delivery, attachments: store);
        service = new TelegramPollingService(api, options, new TelegramUserAuthorizer(options), Runner.Instance,
            Runner.Instance, new JobRegistry(), NullLogger<TelegramPollingService>.Instance, null,
            new GeneralWorkspace(Path.Combine(root, "general")), settings, sessions, delivery, new Catalog(),
            attachments: store, pendingAttachments: pending);
        await service.StartAsync(CancellationToken.None);
    }

    private static TelegramMessage Text(string text, long sender = 123) =>
        new(new TelegramChat(sender), text, new TelegramUser(sender));

    private static TelegramMessage Photo(string fileId) =>
        new(new TelegramChat(123), null, new TelegramUser(123), 0, null, null, [new TelegramPhotoSize(fileId, 10, 10)]);

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

    private sealed class Catalog : IAgentModelCatalog
    {
        public Task<IReadOnlyList<AgentModelInfo>> GetModelsAsync(AgentKind agent,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AgentModelInfo>>([new("opus", "Opus", true, ["low", "high"])]);
    }

    private sealed class Runner : ICodexRunner, IClaudeRunner
    {
        public static Runner Instance { get; } = new();

        public Task<AgentProcessResult> RunAsync(string prompt, string workingDirectory,
            CancellationToken cancellationToken = default, bool generalMode = false,
            IReadOnlyDictionary<string, string>? environment = null, string? model = null, string? effort = null,
            IReadOnlyList<Attachment>? attachments = null) =>
            throw new InvalidOperationException("no one-shot expected");
    }

    private sealed class BotApi : ITelegramBotApi
    {
        private readonly Channel<TelegramUpdate> updates = Channel.CreateUnbounded<TelegramUpdate>();
        private readonly Channel<string> messages = Channel.CreateUnbounded<string>();
        private long nextId;

        public ConcurrentDictionary<string, byte[]> Files { get; } = new();

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
