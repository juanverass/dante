using System.Threading.Channels;
using Dante.Worker.Agents;
using Dante.Worker.Jobs;
using Dante.Worker.Sessions;
using Dante.Worker.Settings;
using Dante.Worker.Telegram;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dante.Tests;

// Model selection (#77) through the Telegram polling loop, including refusal before execution.
public sealed class TelegramModelCommandTests : IAsyncDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "dante-modes-" + Guid.NewGuid().ToString("N"));
    private readonly FakeSessionDriverFactory drivers = new();
    private readonly BotApi api = new();
    private SessionRegistry? sessions;
    private readonly Catalog catalog = new();
    private readonly JobRegistry jobs = new();
    private readonly RecordingRunner runner = new();

    [Fact]
    public async Task DefaultsAreIndependentPerAgentAndUserAndCanBeReset()
    {
        var store = Settings();
        using var service = CreateService(store);
        await service.StartAsync(CancellationToken.None);
        try
        {
            api.Enqueue("/model");
            Assert.Contains("- Claude: padrão da CLI", await api.NextMessageAsync());
            api.Enqueue("/model claude");
            Assert.Contains("- opus", await api.NextMessageAsync());
            api.Enqueue("/model claude OPUS");
            Assert.Contains("Modelo padrão do Claude: opus", await api.NextMessageAsync());
            api.Enqueue("/model codex fake-mini");
            Assert.Contains("Modelo padrão do Codex: fake-mini", await api.NextMessageAsync());
            var loaded = Settings();
            Assert.Equal("opus", loaded.GetModel(123, AgentKind.Claude));
            Assert.Equal("fake-mini", loaded.GetModel(123, AgentKind.Codex));
            Assert.Null(loaded.GetModel(456, AgentKind.Claude));
            api.Enqueue("/model", 456);
            Assert.Contains("- Codex: padrão da CLI", await api.NextMessageAsync());
            api.Enqueue("/model claude fake-mini");
            Assert.Contains("não é oferecido pelo Claude", await api.NextMessageAsync());
            Assert.Equal("opus", store.GetModel(123, AgentKind.Claude));
            api.Enqueue("/model claude default");
            Assert.Contains("Modelo padrão do Claude: padrão da CLI", await api.NextMessageAsync());
            Assert.Null(Settings().GetModel(123, AgentKind.Claude));
            Assert.Equal("fake-mini", Settings().GetModel(123, AgentKind.Codex));
            Assert.Empty(drivers.Created);
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task SessionOverrideIsFixedWhileDefaultsApplyOnlyToFutureSessions()
    {
        var store = Settings();
        store.SetModel(123, AgentKind.Claude, "opus");
        using var service = CreateService(store);
        await service.StartAsync(CancellationToken.None);
        try
        {
            api.Enqueue("/session start claude model=sonnet");
            Assert.Contains("modelo sonnet", await api.NextMessageAsync());
            Assert.Equal("opus", store.GetModel(123, AgentKind.Claude));
            api.Enqueue("/model claude opus");
            Assert.Contains("A sessão ativa S000001", await api.NextMessageAsync());
            api.Enqueue("primeiro");
            await Eventually(() => drivers.Created[0].Calls.Contains("turn:primeiro"));
            api.Enqueue("segundo");
            Assert.StartsWith("Recebido;", await api.NextMessageAsync());
            Assert.Equal("sonnet", drivers.Created[0].StartOptions!.ModelSelection!.Model);
            Assert.Equal("sonnet", sessions!.GetActive(123)!.ModelLabel);
            api.Enqueue("/status");
            Assert.Contains("modelo sonnet", await api.NextMessageAsync());
            api.Enqueue("/session start claude");
            Assert.Contains("modelo opus", await api.NextMessageAsync());
            Assert.Equal("opus", drivers.Created[1].StartOptions!.ModelSelection!.Model);
            api.Enqueue("/session start claude model=default");
            await api.NextMessageAsync();
            Assert.Null(drivers.Created[2].StartOptions!.ModelSelection!.Model);
            api.Enqueue("olá", 456);
            Assert.Contains("Nova conversa com Claude", await api.NextMessageAsync());
            Assert.Null(drivers.Created[3].StartOptions!.ModelSelection!.Model);
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task InvalidUnavailableAndUnverifiableModelsNeverStartExecution()
    {
        var store = Settings();
        using var service = CreateService(store);
        await service.StartAsync(CancellationToken.None);
        try
        {
            api.Enqueue("/session start codex model=opus");
            Assert.Contains("não é oferecido pelo Codex", await api.NextMessageAsync());
            api.Enqueue("/session start claude model=-m");
            Assert.Contains("Nome de modelo inválido", await api.NextMessageAsync());
            store.SetModel(123, AgentKind.Claude, "retired-model");
            api.Enqueue("olá");
            Assert.Contains("não é mais oferecido", await api.NextMessageAsync());
            api.Enqueue("/claude olá");
            Assert.Contains("/model claude default", await api.NextMessageAsync());
            catalog.Failure = true;
            api.Enqueue("/session start codex model=fake-mini");
            Assert.Contains("Não foi possível validar", await api.NextMessageAsync());
            api.Enqueue("/model claude default");
            await api.NextMessageAsync();
            api.Enqueue("/session start claude");
            Assert.Contains("Sessão S000001 iniciada", await api.NextMessageAsync());
            Assert.Single(drivers.Created);
            Assert.Empty(jobs.GetVisible());
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task OneShotUsesTheNamedAgentsDefaultWithoutChangingTheConversation()
    {
        var store = Settings();
        store.SetModel(123, AgentKind.Claude, "opus");
        store.SetModel(123, AgentKind.Codex, "fake-mini");
        using var service = CreateService(store);
        await service.StartAsync(CancellationToken.None);
        try
        {
            api.Enqueue("/session start claude model=sonnet");
            await api.NextMessageAsync();
            api.Enqueue("/codex teste");
            Assert.Contains("modelo fake-mini", await api.NextMessageAsync());
            await Eventually(() => runner.Models.Count == 1);
            Assert.Equal("fake-mini", runner.Models[0]);
            Assert.Equal("sonnet", sessions!.GetActive(123)!.ModelLabel);
            Assert.Equal("fake-mini", jobs.GetVisible().Single().ModelSelection!.Model);
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    private AssistantSettingsStore Settings() => new(Path.Combine(root, "settings.json"));

    private TelegramPollingService CreateService(AssistantSettingsStore settings)
    {
        var options = Options.Create(new TelegramOptions { BotToken = "test", AllowedUserIds = "123,456" });
        var delivery = new TelegramDeliveryService(api, NullLogger<TelegramDeliveryService>.Instance);
        sessions = new SessionRegistry(drivers, NullLogger<SessionRegistry>.Instance, delivery);
        return new TelegramPollingService(api, options, new TelegramUserAuthorizer(options), runner, runner,
            jobs, NullLogger<TelegramPollingService>.Instance, null,
            new GeneralWorkspace(Path.Combine(root, "general")), settings, sessions, delivery, catalog);
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

    private sealed class Catalog : IAgentModelCatalog
    {
        public bool Failure { get; set; }
        public Task<IReadOnlyList<AgentModelInfo>> GetModelsAsync(AgentKind agent,
            CancellationToken cancellationToken = default)
        {
            if (Failure) throw new AgentModelCatalogException("catalog unavailable");
            IReadOnlyList<AgentModelInfo> models = agent == AgentKind.Claude
                ? [new("opus", "Opus", true, []), new("sonnet", "Sonnet", false, [])]
                : [new("fake-mini", "Mini", true, [])];
            return Task.FromResult(models);
        }
    }

    private sealed class RecordingRunner : ICodexRunner, IClaudeRunner
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string?> models = new();
        public IReadOnlyList<string?> Models => models.ToArray();
        public Task<AgentProcessResult> RunAsync(string prompt, string workingDirectory,
            CancellationToken cancellationToken = default, bool generalMode = false,
            IReadOnlyDictionary<string, string>? environment = null, string? model = null)
        {
            models.Enqueue(model);
            throw new InvalidOperationException("recorded");
        }
    }

    private sealed class BotApi : ITelegramBotApi
    {
        private readonly Channel<TelegramUpdate> updates = Channel.CreateUnbounded<TelegramUpdate>();
        private readonly Channel<string> messages = Channel.CreateUnbounded<string>();
        private long nextId;

        public void Enqueue(string command, long senderId = 123) => updates.Writer.TryWrite(new TelegramUpdate(
            Interlocked.Increment(ref nextId), new TelegramMessage(new TelegramChat(senderId), command,
                new TelegramUser(senderId))));

        public async Task<string> NextMessageAsync() =>
            await messages.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        public async Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(long offset, CancellationToken cancellationToken) =>
            [await updates.Reader.ReadAsync(cancellationToken)];

        public Task SendMessageAsync(long chatId, string text, CancellationToken cancellationToken)
        {
            messages.Writer.TryWrite(text);
            return Task.CompletedTask;
        }

        public Task SendChatActionAsync(long chatId, string action, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
