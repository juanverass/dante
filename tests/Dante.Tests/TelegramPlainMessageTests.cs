using System.Threading.Channels;
using Dante.Worker.Agents;
using Dante.Worker.Jobs;
using Dante.Worker.Settings;
using Dante.Worker.Telegram;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dante.Tests;

public sealed class TelegramPlainMessageTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "dante-plain-messages-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(AgentKind.Claude)]
    [InlineData(AgentKind.Codex)]
    public async Task PlainMessageStartsExactlyOneJobWithDefaultAgent(AgentKind defaultAgent)
    {
        var store = new AssistantSettingsStore(Path.Combine(root, "settings.json"));
        store.SetDefaultAgent(defaultAgent);
        var api = new BotApi();
        var codex = new RecordingRunner();
        var claude = new RecordingRunner();
        var general = new GeneralWorkspace(Path.Combine(root, "general"));
        using var service = CreateService(api, store, codex, claude, general);
        await service.StartAsync(CancellationToken.None);
        try
        {
            api.Enqueue("  Explique o padrão Strategy.\n");
            Assert.Contains($"{defaultAgent} iniciado", await api.NextMessageAsync());
            Assert.Contains($"{defaultAgent} concluído", await api.NextMessageAsync());

            var selected = defaultAgent == AgentKind.Codex ? codex : claude;
            var other = defaultAgent == AgentKind.Codex ? claude : codex;
            Assert.Equal(new Run("Explique o padrão Strategy.", general.Path, true), Assert.Single(selected.Runs));
            Assert.Empty(other.Runs);
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Theory]
    [InlineData(AgentKind.Claude, "/codex revise o PR", AgentKind.Codex)]
    [InlineData(AgentKind.Codex, "/claude revise o PR", AgentKind.Claude)]
    public async Task ExplicitAgentOverridesDefaultForOneExecutionOnly(AgentKind defaultAgent, string command,
        AgentKind overrideAgent)
    {
        var file = Path.Combine(root, "settings.json");
        var store = new AssistantSettingsStore(file);
        store.SetDefaultAgent(defaultAgent);
        var api = new BotApi();
        var codex = new RecordingRunner();
        var claude = new RecordingRunner();
        using var service = CreateService(api, store, codex, claude);
        await service.StartAsync(CancellationToken.None);
        try
        {
            api.Enqueue(command);
            Assert.Contains($"{overrideAgent} iniciado", await api.NextMessageAsync());
            Assert.Contains($"{overrideAgent} concluído", await api.NextMessageAsync());
            api.Enqueue("próxima mensagem");
            Assert.Contains($"{defaultAgent} iniciado", await api.NextMessageAsync());
            Assert.Contains($"{defaultAgent} concluído", await api.NextMessageAsync());

            var overridden = overrideAgent == AgentKind.Codex ? codex : claude;
            var byDefault = defaultAgent == AgentKind.Codex ? codex : claude;
            Assert.Equal("revise o PR", Assert.Single(overridden.Runs).Prompt);
            Assert.Equal("próxima mensagem", Assert.Single(byDefault.Runs).Prompt);
            Assert.Equal(defaultAgent, store.Current.DefaultAgent);
            Assert.Equal(defaultAgent, new AssistantSettingsStore(file).Current.DefaultAgent);
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Theory]
    [InlineData("/unknown faça algo")]
    [InlineData("/Claudee faça algo")]
    [InlineData("/")]
    public async Task UnknownSlashCommandDoesNotStartAgent(string command)
    {
        var api = new BotApi();
        var codex = new RecordingRunner();
        var claude = new RecordingRunner();
        using var service = CreateService(api, new AssistantSettingsStore(Path.Combine(root, "settings.json")),
            codex, claude);
        await service.StartAsync(CancellationToken.None);
        try
        {
            api.Enqueue(command);
            Assert.StartsWith("Comando desconhecido:", await api.NextMessageAsync());
            api.Enqueue("/ping");
            Assert.Equal("pong", await api.NextMessageAsync());
            Assert.Empty(codex.Runs);
            Assert.Empty(claude.Runs);
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task UnauthorizedPlainMessageDoesNotStartAgent()
    {
        var api = new BotApi();
        var claude = new RecordingRunner();
        using var service = CreateService(api, new AssistantSettingsStore(Path.Combine(root, "settings.json")),
            new RecordingRunner(), claude);
        await service.StartAsync(CancellationToken.None);
        try
        {
            api.Enqueue("qual a capital da Grécia?", 999);
            api.Enqueue("/ping");
            Assert.Equal("pong", await api.NextMessageAsync());
            Assert.Empty(claude.Runs);
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    private TelegramPollingService CreateService(BotApi api, AssistantSettingsStore settings, RecordingRunner codex,
        RecordingRunner claude, GeneralWorkspace? general = null)
    {
        var options = Options.Create(new TelegramOptions { BotToken = "test", AllowedUserIds = "123" });
        return new TelegramPollingService(api, options, new TelegramUserAuthorizer(options), codex, claude,
            new JobRegistry(), NullLogger<TelegramPollingService>.Instance,
            generalWorkspace: general ?? new GeneralWorkspace(Path.Combine(root, "general")), settings: settings);
    }

    public void Dispose()
    {
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
            IReadOnlyDictionary<string, string>? environment = null)
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
    }
}
