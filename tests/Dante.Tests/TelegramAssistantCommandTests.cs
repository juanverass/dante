using System.Threading.Channels;
using Dante.Worker.Agents;
using Dante.Worker.Jobs;
using Dante.Worker.Settings;
using Dante.Worker.Telegram;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dante.Tests;

public sealed class TelegramAssistantCommandTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "dante-assistant-commands-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task AgentCommandShowsAndPersistsDefaultWithoutStartingJob()
    {
        var file = Path.Combine(root, "settings.json");
        var api = new BotApi();
        var runner = new RecordingRunner();
        using var service = CreateService(api, new AssistantSettingsStore(file), runner);
        await service.StartAsync(CancellationToken.None);
        try
        {
            api.Enqueue("/agent");
            Assert.Equal("Agente padrão: Claude", await api.NextMessageAsync());
            api.Enqueue("/agent set codex");
            Assert.Equal("Agente padrão alterado para Codex.", await api.NextMessageAsync());
            api.Enqueue("/AGENT");
            Assert.Equal("Agente padrão: Codex", await api.NextMessageAsync());
            Assert.Equal(AgentKind.Codex, new AssistantSettingsStore(file).Current.DefaultAgent);
            api.Enqueue("/Agent SET Claude");
            Assert.Equal("Agente padrão alterado para Claude.", await api.NextMessageAsync());
            Assert.Equal(AgentKind.Claude, new AssistantSettingsStore(file).Current.DefaultAgent);
            Assert.Equal(0, runner.Calls);
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Theory]
    [InlineData("/agent set gemini", "Agente desconhecido")]
    [InlineData("/agent set", "Uso:")]
    [InlineData("/agent codex", "Uso:")]
    [InlineData("/agent set codex now", "Uso:")]
    public async Task InvalidAgentCommandKeepsPreviousDefault(string command, string expected)
    {
        var file = Path.Combine(root, "settings.json");
        var store = new AssistantSettingsStore(file);
        store.SetDefaultAgent(AgentKind.Codex);
        var saved = File.ReadAllText(file);
        var api = new BotApi();
        using var service = CreateService(api, store, new RecordingRunner());
        await service.StartAsync(CancellationToken.None);
        try
        {
            api.Enqueue(command);
            Assert.Contains(expected, await api.NextMessageAsync());
            Assert.Equal(AgentKind.Codex, store.Current.DefaultAgent);
            Assert.Equal(saved, File.ReadAllText(file));
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task UnauthorizedUserCannotChangeDefaultAgent()
    {
        var file = Path.Combine(root, "settings.json");
        var store = new AssistantSettingsStore(file);
        var api = new BotApi();
        using var service = CreateService(api, store, new RecordingRunner());
        await service.StartAsync(CancellationToken.None);
        try
        {
            api.Enqueue("/agent set codex", 999);
            api.Enqueue("/ping");
            Assert.Equal("pong", await api.NextMessageAsync());
            Assert.Equal(AgentKind.Claude, store.Current.DefaultAgent);
            Assert.False(File.Exists(file));
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task ExplicitAgentCommandDoesNotChangeDefault()
    {
        var store = new AssistantSettingsStore(Path.Combine(root, "settings.json"));
        var api = new BotApi();
        var runner = new RecordingRunner();
        using var service = CreateService(api, store, runner);
        await service.StartAsync(CancellationToken.None);
        try
        {
            api.Enqueue("/codex explain");
            Assert.Contains("Codex iniciado", await api.NextMessageAsync());
            Assert.Contains("concluído", await api.NextMessageAsync());
            api.Enqueue("/agent");
            Assert.Equal("Agente padrão: Claude", await api.NextMessageAsync());
            Assert.Equal(1, runner.Calls);
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task FailedWriteReportsErrorAndKeepsPreviousDefault()
    {
        Directory.CreateDirectory(root);
        var blocker = Path.Combine(root, "blocker");
        File.WriteAllText(blocker, "not a directory");
        var store = new AssistantSettingsStore(Path.Combine(blocker, "settings.json"));
        var api = new BotApi();
        using var service = CreateService(api, store, new RecordingRunner());
        await service.StartAsync(CancellationToken.None);
        try
        {
            api.Enqueue("/agent set codex");
            Assert.Contains("Não foi possível salvar", await api.NextMessageAsync());
            Assert.Equal(AgentKind.Claude, store.Current.DefaultAgent);
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    private TelegramPollingService CreateService(BotApi api, AssistantSettingsStore settings, RecordingRunner runner)
    {
        var options = Options.Create(new TelegramOptions { BotToken = "test", AllowedUserIds = "123" });
        return new TelegramPollingService(api, options, new TelegramUserAuthorizer(options), runner, runner,
            new JobRegistry(), NullLogger<TelegramPollingService>.Instance,
            generalWorkspace: new GeneralWorkspace(Path.Combine(root, "general")), settings: settings);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed class RecordingRunner : ICodexRunner, IClaudeRunner
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);

        public Task<AgentProcessResult> RunAsync(string prompt, string workingDirectory,
            CancellationToken cancellationToken = default, bool generalMode = false,
            IReadOnlyDictionary<string, string>? environment = null, string? model = null, string? effort = null,
            IReadOnlyList<Dante.Worker.Attachments.Attachment>? attachments = null)
        {
            Interlocked.Increment(ref calls);
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
