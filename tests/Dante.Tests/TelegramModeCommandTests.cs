using System.Threading.Channels;
using Dante.Worker.Agents;
using Dante.Worker.Jobs;
using Dante.Worker.Sessions;
using Dante.Worker.Settings;
using Dante.Worker.Telegram;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dante.Tests;

// Operational modes (#76): /mode chooses the persisted default for new sessions; a session keeps its mode until closed.
public sealed class TelegramModeCommandTests : IAsyncDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "dante-modes-" + Guid.NewGuid().ToString("N"));
    private readonly FakeSessionDriverFactory drivers = new();
    private readonly BotApi api = new();
    private SessionRegistry? sessions;

    [Fact]
    public async Task ModeShowsTheDefaultAndTheOptionsWithFriendlyNames()
    {
        using var service = CreateService(Settings());
        await service.StartAsync(CancellationToken.None);
        try
        {
            api.Enqueue("/mode");
            var reply = await api.NextMessageAsync();
            Assert.StartsWith("Modo padrão para novas sessões: manual (aprovação).", reply);
            Assert.Contains("- manual (aprovação): pede sua aprovação", reply);
            Assert.Contains("- auto (automático): ", reply);
            Assert.Contains("- plan (planejamento): analisa e planeja sem alterar arquivos", reply);
            Assert.Contains("Suporte: Claude manual, auto, plan | Codex manual, auto, plan", reply);
            Assert.Contains("Acesso irrestrito (full) não é oferecido.", reply);

            api.Enqueue("/mode full");
            Assert.StartsWith("Modo indisponível: full.", await api.NextMessageAsync());
            api.Enqueue("/mode auto plan");
            Assert.Equal("Uso: /mode | /mode manual|auto|plan", await api.NextMessageAsync());
            Assert.Empty(drivers.Created);
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task DefaultModeIsPersistedPerUserAndUsedByNewSessions()
    {
        var file = Path.Combine(root, "settings.json");
        using var service = CreateService(new AssistantSettingsStore(file));
        await service.StartAsync(CancellationToken.None);
        try
        {
            api.Enqueue("/mode auto");
            Assert.StartsWith("Modo padrão para novas sessões: auto (automático).", await api.NextMessageAsync());
            Assert.Equal(AgentPermissionProfile.Auto, new AssistantSettingsStore(file).GetSessionMode(123));
            api.Enqueue("/permissions");
            Assert.StartsWith("Perfil para novas sessões: auto.", await api.NextMessageAsync());

            // Another user keeps the default.
            api.Enqueue("/mode", 456);
            Assert.StartsWith("Modo padrão para novas sessões: manual (aprovação).", await api.NextMessageAsync());
            Assert.Equal(AgentPermissionProfile.Manual, new AssistantSettingsStore(file).GetSessionMode(456));

            api.Enqueue("olá");
            await Eventually(() => drivers.Created.Count == 1 && drivers.Created[0].Calls.Contains("turn:olá"));
            drivers.Created[0].Emit(new MessageCompletedEvent("m1", "Olá!"));
            drivers.Created[0].Emit(new TurnCompletedEvent(AgentTurnOutcome.Completed));
            Assert.Equal("Olá!\n", await api.NextMessageAsync());
            Assert.Equal(AgentPermissionProfile.Auto, drivers.Created.Single().StartOptions!.Profile);

            api.Enqueue("oi", 456);
            await Eventually(() => drivers.Created.Count == 2 && drivers.Created[1].Calls.Contains("turn:oi"));
            drivers.Created[1].Emit(new MessageCompletedEvent("m2", "Oi!"));
            drivers.Created[1].Emit(new TurnCompletedEvent(AgentTurnOutcome.Completed));
            Assert.Equal("Oi!\n", await api.NextMessageAsync());
            Assert.Equal(AgentPermissionProfile.Manual, drivers.Created[1].StartOptions!.Profile);
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task ChangingTheDefaultNeverChangesAnExistingSession()
    {
        var store = Settings();
        using var service = CreateService(store);
        await service.StartAsync(CancellationToken.None);
        try
        {
            api.Enqueue("/session start codex plan");
            Assert.Contains("Sessão S000001 iniciada com Codex (General), modo plan (planejamento).",
                await api.NextMessageAsync());
            // A mode given to /session start is for that session only.
            Assert.Equal(AgentPermissionProfile.Manual, store.GetSessionMode(123));

            api.Enqueue("/mode approval");
            Assert.Contains("A sessão ativa S000001 (Codex, General) continua; envie /session start para usar o modo manual.",
                await api.NextMessageAsync());
            api.Enqueue("/mode");
            Assert.Contains("Sessão ativa S000001: modo plan, fixo até ela ser encerrada.", await api.NextMessageAsync());

            api.Enqueue("continue");
            var driver = drivers.Created.Single();
            await Eventually(() => driver.Calls.Contains("turn:continue"));
            Assert.Equal(AgentPermissionProfile.Plan, driver.StartOptions!.Profile);
            Assert.Equal(AgentPermissionProfile.Plan, sessions!.GetActive(123)!.Profile);
            api.Enqueue("/status");
            Assert.Contains("S000001 Codex General: Running (ativa) | turno T000001 | modo plan",
                await api.NextMessageAsync());

            api.Enqueue("/session start");
            Assert.Contains("Sessão S000002 iniciada com Claude (General), modo manual (aprovação).",
                await api.NextMessageAsync());
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task ModeTheAgentDoesNotSupportIsRefusedBeforeTheSessionStarts()
    {
        drivers.Configure = driver => driver.Capabilities = driver.Capabilities with
        {
            Modes = [AgentPermissionProfile.Manual]
        };
        using var service = CreateService(Settings());
        await service.StartAsync(CancellationToken.None);
        try
        {
            api.Enqueue("/session start codex plan");
            Assert.Equal("O modo plan não é suportado pelo Codex. Modos disponíveis: manual.",
                await api.NextMessageAsync());
            api.Enqueue("/mode auto");
            await api.NextMessageAsync();
            api.Enqueue("olá");
            Assert.Equal("Não foi possível iniciar a conversa com Claude: O modo auto não é suportado pelo Claude. " +
                "Modos disponíveis: manual.", await api.NextMessageAsync());

            Assert.All(drivers.Created, driver => Assert.Empty(driver.Calls));
            Assert.Empty(sessions!.List(123));
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    private AssistantSettingsStore Settings() => new(Path.Combine(root, "settings.json"));

    private TelegramPollingService CreateService(AssistantSettingsStore settings)
    {
        var options = Options.Create(new TelegramOptions { BotToken = "test", AllowedUserIds = "123,456" });
        var delivery = new TelegramDeliveryService(api, NullLogger<TelegramDeliveryService>.Instance);
        sessions = new SessionRegistry(drivers, NullLogger<SessionRegistry>.Instance, delivery);
        var runner = new NoRunner();
        return new TelegramPollingService(api, options, new TelegramUserAuthorizer(options), runner, runner,
            new JobRegistry(), NullLogger<TelegramPollingService>.Instance, null,
            new GeneralWorkspace(Path.Combine(root, "general")), settings, sessions, delivery);
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

    private sealed class NoRunner : ICodexRunner, IClaudeRunner
    {
        public Task<AgentProcessResult> RunAsync(string prompt, string workingDirectory,
            CancellationToken cancellationToken = default, bool generalMode = false,
            IReadOnlyDictionary<string, string>? environment = null) =>
            throw new InvalidOperationException("Modos não executam one-shot.");
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
