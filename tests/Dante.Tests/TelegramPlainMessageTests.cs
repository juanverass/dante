using System.Diagnostics;
using System.Threading.Channels;
using Dante.Worker.Agents;
using Dante.Worker.Jobs;
using Dante.Worker.Repositories;
using Dante.Worker.Sessions;
using Dante.Worker.Settings;
using Dante.Worker.Telegram;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dante.Tests;

// Session-first conversation (AD-23): plain messages open and reuse an interactive session; /claude and /codex stay
// explicit one-shot jobs.
public sealed class TelegramPlainMessageTests : IAsyncDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "dante-plain-messages-" + Guid.NewGuid().ToString("N"));
    private readonly FakeSessionDriverFactory drivers = new();
    private readonly BotApi api = new();
    private readonly RecordingRunner codex = new();
    private readonly RecordingRunner claude = new();
    private SessionRegistry? sessions;
    private TelegramDeliveryService? delivery;

    [Theory]
    [InlineData(AgentKind.Claude)]
    [InlineData(AgentKind.Codex)]
    public async Task ConsecutivePlainMessagesAreTurnsOfOneSessionWithTheDefaultAgent(AgentKind defaultAgent)
    {
        var store = Settings();
        store.SetDefaultAgent(defaultAgent);
        var general = new GeneralWorkspace(Path.Combine(root, "general"));
        using var service = CreateService(store, general: general);
        await service.StartAsync(CancellationToken.None);
        try
        {
            api.Enqueue("  Explique o padrão Strategy.\n");
            var driver = await SingleDriverAsync();
            await Eventually(() => driver.Calls.Contains("turn:Explique o padrão Strategy."));
            // The mode is said once, when the conversation opens (#76).
            Assert.Equal($"Nova conversa com {defaultAgent} (General), modo manual (aprovação).",
                await api.NextMessageAsync());
            Assert.Equal(general.Path, driver.StartOptions!.WorkingDirectory);
            Assert.True(driver.StartOptions.IsGeneral);
            Assert.Equal(AgentPermissionProfile.Manual, driver.StartOptions.Profile);
            Assert.Equal(defaultAgent, sessions!.GetActive(123)!.Agent);
            driver.Emit(new TurnStartedEvent());
            driver.Emit(new MessageCompletedEvent("m1", "Strategy encapsula algoritmos."));
            driver.Emit(new TurnCompletedEvent(AgentTurnOutcome.Completed));
            Assert.Equal("Strategy encapsula algoritmos.\n", await api.NextMessageAsync());
            await Eventually(() => sessions.GetActive(123)!.State == AgentSessionState.Idle);

            api.Enqueue("E o padrão State?");
            await Eventually(() => driver.Calls.Contains("turn:E o padrão State?"));
            driver.Emit(new TurnStartedEvent());
            driver.Emit(new MessageCompletedEvent("m2", "State troca o comportamento pelo estado."));
            driver.Emit(new TurnCompletedEvent(AgentTurnOutcome.Completed));
            Assert.Equal("State troca o comportamento pelo estado.\n", await api.NextMessageAsync());

            // Both messages reached the same process: the second turn has the first one's context.
            Assert.Equal(["start", "turn:Explique o padrão Strategy.", "turn:E o padrão State?"], driver.Calls);
            Assert.Single(drivers.Created);
            Assert.Empty(codex.Runs);
            Assert.Empty(claude.Runs);
            Assert.True(api.ChatActions > 0);
            await Eventually(() => delivery!.Get("S000001", 123)?.State == TelegramDeliveryState.Delivered);

            api.Enqueue("/status");
            var status = await api.NextMessageAsync();
            Assert.Contains($"S000001 {defaultAgent} General: Idle (ativa)", status);
            Assert.Contains("último turno concluído", status);
            Assert.Contains("S000001/T000002: Delivered", status);
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task MessageDuringATurnIsQueuedWithAShortAcknowledgement()
    {
        using var service = CreateService(Settings());
        await service.StartAsync(CancellationToken.None);
        try
        {
            api.Enqueue("primeira");
            var driver = await SingleDriverAsync();
            await Eventually(() => driver.Calls.Contains("turn:primeira"));
            Assert.StartsWith("Nova conversa com Claude", await api.NextMessageAsync());
            api.Enqueue("não altere o arquivo X");
            Assert.Equal("Recebido; envio ao agente quando a resposta atual terminar.", await api.NextMessageAsync());
            Assert.DoesNotContain("turn:não altere o arquivo X", driver.Calls);

            driver.Emit(new TurnStartedEvent());
            driver.Emit(new TurnCompletedEvent(AgentTurnOutcome.Completed));
            await Eventually(() => driver.Calls.Contains("turn:não altere o arquivo X"));
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Theory]
    [InlineData(AgentKind.Claude, "/codex revise o PR", AgentKind.Codex)]
    [InlineData(AgentKind.Codex, "/claude revise o PR", AgentKind.Claude)]
    public async Task ExplicitOneShotCommandKeepsItsJobAndDoesNotTouchTheConversation(AgentKind defaultAgent,
        string command, AgentKind overrideAgent)
    {
        var file = Path.Combine(root, "settings.json");
        var store = new AssistantSettingsStore(file);
        store.SetDefaultAgent(defaultAgent);
        using var service = CreateService(store);
        await service.StartAsync(CancellationToken.None);
        try
        {
            api.Enqueue(command);
            Assert.Contains($"{overrideAgent} iniciado. Job ID: J000001", await api.NextMessageAsync());
            Assert.Contains($"{overrideAgent} concluído", await api.NextMessageAsync());
            Assert.Empty(drivers.Created);

            api.Enqueue("próxima mensagem");
            var driver = await SingleDriverAsync();
            await Eventually(() => driver.Calls.Contains("turn:próxima mensagem"));
            Assert.Equal(defaultAgent, sessions!.GetActive(123)!.Agent);

            var overridden = overrideAgent == AgentKind.Codex ? codex : claude;
            Assert.Equal("revise o PR", Assert.Single(overridden.Runs).Prompt);
            Assert.Empty((defaultAgent == AgentKind.Codex ? codex : claude).Runs);
            Assert.Equal(defaultAgent, new AssistantSettingsStore(file).Current.DefaultAgent);
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task ChangingAgentOrContextKeepsTheActiveSessionAndSaysSo()
    {
        var registry = new RepositoryRegistry(Path.Combine(root, "config", "repositories.json"));
        registry.Add("@repo", CreateGitRepository("repo"));
        var store = Settings();
        using var service = CreateService(store, registry);
        await service.StartAsync(CancellationToken.None);
        try
        {
            api.Enqueue("olá");
            var driver = await SingleDriverAsync();
            await Eventually(() => driver.Calls.Contains("turn:olá"));
            Assert.StartsWith("Nova conversa com Claude", await api.NextMessageAsync());
            driver.Emit(new TurnStartedEvent());
            driver.Emit(new TurnCompletedEvent(AgentTurnOutcome.Completed));
            await api.NextMessageAsync();
            await Eventually(() => sessions!.GetActive(123)!.State == AgentSessionState.Idle);

            api.Enqueue("/agent set codex");
            var agentReply = await api.NextMessageAsync();
            Assert.Contains("Agente padrão alterado para Codex.", agentReply);
            Assert.Contains("A sessão ativa S000001 (Claude, General) continua; envie /session start para conversar com Codex.",
                agentReply);
            api.Enqueue("/use @repo");
            Assert.Contains("A sessão ativa S000001 (Claude, General) continua; envie /session start para conversar em @repo.",
                await api.NextMessageAsync());

            api.Enqueue("continua comigo?");
            await Eventually(() => driver.Calls.Contains("turn:continua comigo?"));
            Assert.Single(drivers.Created);

            api.Enqueue("/session start");
            Assert.Contains("Sessão S000002 iniciada com Codex (@repo)", await api.NextMessageAsync());
            Assert.False(drivers.Created.Last().StartOptions!.IsGeneral);

            api.Enqueue("/use general");
            Assert.Contains("A sessão ativa S000002 (Codex, @repo) continua", await api.NextMessageAsync());
            api.Enqueue("/agent set codex");
            Assert.Equal("Agente padrão alterado para Codex.", await api.NextMessageAsync());
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task OutputOfASessionThatStopsBeingActiveMidTurnIsIdentified()
    {
        using var service = CreateService(Settings());
        await service.StartAsync(CancellationToken.None);
        try
        {
            api.Enqueue("tarefa longa");
            var first = await SingleDriverAsync();
            await Eventually(() => first.Calls.Contains("turn:tarefa longa"));
            Assert.StartsWith("Nova conversa com Claude", await api.NextMessageAsync());
            first.Emit(new TurnStartedEvent());
            first.Emit(new MessageDeltaEvent("m1", "parte 1\n"));
            Assert.Equal("parte 1\n", await api.NextMessageAsync());

            api.Enqueue("/session start");
            Assert.Contains("Sessão S000002 iniciada", await api.NextMessageAsync());
            first.Emit(new MessageDeltaEvent("m1", "parte 2\n"));
            Assert.Equal("[S000001] parte 2\n", await api.NextMessageAsync());

            api.Enqueue("/session select S000001");
            Assert.Equal("Sessão ativa: S000001.", await api.NextMessageAsync());
            first.Emit(new MessageDeltaEvent("m1", "parte 3\n"));
            Assert.Equal("parte 3\n", await api.NextMessageAsync());

            api.Enqueue("/session close S000001");
            Assert.Equal("Sessão S000001 encerrada.", await api.NextMessageAsync());
            api.Enqueue("/session select S000002");
            await api.NextMessageAsync();
            await Eventually(() => sessions!.GetActive(123)?.Id == "S000002");
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task PlainMessageWithAliasOpensTheSessionInThatRepositoryWithoutChangingTheActiveContext()
    {
        var registry = new RepositoryRegistry(Path.Combine(root, "config", "repositories.json"));
        var path = CreateGitRepository("repo");
        registry.Add("@repo", path);
        var store = Settings();
        using var service = CreateService(store, registry);
        await service.StartAsync(CancellationToken.None);
        try
        {
            api.Enqueue("@repo");
            Assert.Equal("Uso: @alias <mensagem>", await api.NextMessageAsync());
            api.Enqueue("@ghost faça algo");
            Assert.Contains("Repositório @ghost não cadastrado", await api.NextMessageAsync());
            Assert.Empty(drivers.Created);

            api.Enqueue("@repo rode os testes");
            var driver = await SingleDriverAsync();
            await Eventually(() => driver.Calls.Contains("turn:rode os testes"));
            Assert.Equal(path, driver.StartOptions!.WorkingDirectory);
            Assert.Equal("@repo", sessions!.GetActive(123)!.Context.RepositoryAlias);
            Assert.Null(store.GetActiveRepository(123));
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task StaleActiveRepositoryRefusesToOpenASession()
    {
        var store = Settings();
        store.SetActiveRepository(123, "@ghost");
        using var service = CreateService(store, new RepositoryRegistry(Path.Combine(root, "config", "repositories.json")));
        await service.StartAsync(CancellationToken.None);
        try
        {
            api.Enqueue("implemente a issue 500");
            Assert.Contains("O repositório ativo @ghost não está mais cadastrado", await api.NextMessageAsync());
            Assert.Empty(drivers.Created);
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task FailuresAreShortAndDetailsStayInStatus()
    {
        drivers.Configure = driver => driver.StartFailure = new AgentProtocolException("O Claude recusou o initialize.");
        using var service = CreateService(Settings());
        await service.StartAsync(CancellationToken.None);
        try
        {
            api.Enqueue("olá");
            Assert.Equal("Não foi possível iniciar a conversa com Claude. Detalhes em /status.",
                await api.NextMessageAsync());
            Assert.Null(sessions!.GetActive(123));
            api.Enqueue("/status");
            var status = await api.NextMessageAsync();
            Assert.Contains("S000001 Claude General: Failed | modo manual", status);
            Assert.Contains("erro: O Claude recusou o initialize.", status);

            // The next message tries again with a new session.
            drivers.Configure = null;
            api.Enqueue("de novo");
            await Eventually(() => drivers.Created.Count == 2);
            var driver = drivers.Created[1];
            await Eventually(() => driver.Calls.Contains("turn:de novo"));
            Assert.StartsWith("Nova conversa com Claude", await api.NextMessageAsync());

            // A crashed session stays selected (AD-20): the next message is refused with the way out.
            driver.Crash(new AgentProtocolException("O processo do Claude encerrou inesperadamente (código 5)."));
            Assert.Contains("A sessão S000002 foi encerrada: O processo do Claude encerrou inesperadamente (código 5).\n" +
                "Envie /session start para começar outra conversa.", await api.NextMessageAsync());
            api.Enqueue("ainda está aí?");
            Assert.Equal("A sessão S000002 foi encerrada. Envie /session start para começar outra conversa.",
                await api.NextMessageAsync());
            Assert.Equal(2, drivers.Created.Count);

            api.Enqueue("/session start");
            Assert.Contains("Sessão S000003 iniciada", await api.NextMessageAsync());
            api.Enqueue("nova conversa");
            await Eventually(() => drivers.Created[2].Calls.Contains("turn:nova conversa"));
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Theory]
    [InlineData("/unknown faça algo")]
    [InlineData("/Claudee faça algo")]
    [InlineData("/")]
    public async Task UnknownSlashCommandDoesNotStartAgent(string command)
    {
        using var service = CreateService(Settings());
        await service.StartAsync(CancellationToken.None);
        try
        {
            api.Enqueue(command);
            Assert.StartsWith("Comando desconhecido:", await api.NextMessageAsync());
            api.Enqueue("/ping");
            Assert.Equal("pong", await api.NextMessageAsync());
            Assert.Empty(codex.Runs);
            Assert.Empty(claude.Runs);
            Assert.Empty(drivers.Created);
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task UnauthorizedPlainMessageDoesNotStartAgent()
    {
        using var service = CreateService(Settings());
        await service.StartAsync(CancellationToken.None);
        try
        {
            api.Enqueue("qual a capital da Grécia?", 999);
            api.Enqueue("/ping");
            Assert.Equal("pong", await api.NextMessageAsync());
            Assert.Empty(claude.Runs);
            Assert.Empty(drivers.Created);
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    private AssistantSettingsStore Settings() => new(Path.Combine(root, "settings.json"));

    private async Task<FakeSessionDriver> SingleDriverAsync()
    {
        await Eventually(() => drivers.Created.Count == 1);
        return drivers.Created[0];
    }

    private TelegramPollingService CreateService(AssistantSettingsStore settings, RepositoryRegistry? registry = null,
        GeneralWorkspace? general = null)
    {
        var options = Options.Create(new TelegramOptions { BotToken = "test", AllowedUserIds = "123" });
        delivery = new TelegramDeliveryService(api, NullLogger<TelegramDeliveryService>.Instance)
        {
            TypingInterval = TimeSpan.FromMilliseconds(100)
        };
        sessions = new SessionRegistry(drivers, NullLogger<SessionRegistry>.Instance, delivery);
        return new TelegramPollingService(api, options, new TelegramUserAuthorizer(options), codex, claude,
            new JobRegistry(), NullLogger<TelegramPollingService>.Instance, registry,
            general ?? new GeneralWorkspace(Path.Combine(root, "general")), settings, sessions, delivery);
    }

    private string CreateGitRepository(string name)
    {
        var path = Path.Combine(root, name);
        Directory.CreateDirectory(path);
        using var process = Process.Start(new ProcessStartInfo("git")
        {
            WorkingDirectory = path, UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, ArgumentList = { "init" }
        })!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return path;
    }

    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(50);
        Assert.True(condition());
    }

    public async ValueTask DisposeAsync()
    {
        if (sessions is not null) await sessions.DisposeAsync();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed record Run(string Prompt, string WorkingDirectory, bool GeneralMode);

    private sealed class RecordingRunner : ICodexRunner, IClaudeRunner
    {
        private readonly List<Run> runs = [];

        public IReadOnlyList<Run> Runs
        {
            get { lock (runs) return runs.ToArray(); }
        }

        public Task<AgentProcessResult> RunAsync(string prompt, string workingDirectory,
            CancellationToken cancellationToken = default, bool generalMode = false,
            IReadOnlyDictionary<string, string>? environment = null, string? model = null, string? effort = null)
        {
            lock (runs) runs.Add(new Run(prompt, workingDirectory, generalMode));
            return Task.FromResult(new AgentProcessResult(AgentProcessStatus.Succeeded, "done", "", 0,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        }
    }

    private sealed class BotApi : ITelegramBotApi
    {
        private readonly Channel<TelegramUpdate> updates = Channel.CreateUnbounded<TelegramUpdate>();
        private readonly Channel<string> messages = Channel.CreateUnbounded<string>();
        private long nextId;
        private int chatActions;

        public int ChatActions => chatActions;

        public void Enqueue(string command, long senderId = 123) => updates.Writer.TryWrite(new TelegramUpdate(
            Interlocked.Increment(ref nextId), new TelegramMessage(new TelegramChat(1), command, new TelegramUser(senderId))));

        public async Task<string> NextMessageAsync() =>
            await messages.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        public async Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(long offset, CancellationToken cancellationToken) =>
            [await updates.Reader.ReadAsync(cancellationToken)];

        public Task SendMessageAsync(long chatId, string text, CancellationToken cancellationToken)
        {
            messages.Writer.TryWrite(text);
            return Task.CompletedTask;
        }

        public Task SendChatActionAsync(long chatId, string action, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref chatActions);
            return Task.CompletedTask;
        }
    }
}
