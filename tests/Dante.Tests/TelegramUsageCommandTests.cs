using System.Collections.Concurrent;
using System.Threading.Channels;
using Dante.Worker.Agents;
using Dante.Worker.Jobs;
using Dante.Worker.Sessions;
using Dante.Worker.Settings;
using Dante.Worker.Telegram;
using Dante.Worker.Usage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dante.Tests;

// /uso claude|codex (#116) through the Telegram polling loop: one command, never a prompt, and no effect on sessions.
public sealed class TelegramUsageCommandTests : IAsyncDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "dante-uso-" + Guid.NewGuid().ToString("N"));
    private readonly FakeSessionDriverFactory drivers = new();
    private readonly BotApi api = new();
    private readonly Reader reader = new();
    private readonly JobRegistry jobs = new();
    private SessionRegistry? sessions;

    [Theory]
    [InlineData("/uso")]
    [InlineData("/uso gemini")]
    [InlineData("/uso codex agora")]
    [InlineData("/uso @exemplo")]
    public async Task WrongSyntaxShowsTheSingleCommandWithoutReachingAnAgent(string command)
    {
        using var service = CreateService();
        await service.StartAsync(CancellationToken.None);
        try
        {
            api.Enqueue(command);
            Assert.Equal("Uso: /uso claude|codex", await api.NextMessageAsync());
            Assert.Empty(reader.Queries);
            Assert.Empty(drivers.Created);
            Assert.Empty(jobs.GetVisible());
            Assert.Empty(sessions!.List(123));
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task EachArgumentQueriesItsOwnAgentCaseInsensitively()
    {
        using var service = CreateService();
        await service.StartAsync(CancellationToken.None);
        try
        {
            api.Enqueue("/USO Codex");
            var text = await api.NextMessageAsync();
            Assert.StartsWith("Uso — Codex\nJanela de sessão (5h): 37% do limite utilizado\nSemana: 62% do limite utilizado\n" +
                              "Janela de sessão renova em: 2h ", text);
            Assert.Contains("Consultado agora", text);

            api.Enqueue("/uso claude");
            Assert.Equal("Uso — Claude\nNão foi possível consultar as cotas: " +
                         "a consulta de cotas do Claude ainda não está disponível no D.A.N.T.E.", await api.NextMessageAsync());
            Assert.Equal([AgentKind.Codex, AgentKind.Claude], reader.Queries);
            Assert.Empty(drivers.Created);
            Assert.Empty(jobs.GetVisible());
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task QueryFailureIsActionableAndDoesNotFallBackToAConversation()
    {
        reader.Failure = new UsageQueryException(UsageQueryFailure.NotAuthenticated,
            "o Codex não está autenticado neste host; entre com `codex login` na máquina do D.A.N.T.E.");
        using var service = CreateService();
        await service.StartAsync(CancellationToken.None);
        try
        {
            api.Enqueue("/uso codex");
            Assert.Equal("Uso — Codex\nNão foi possível consultar as cotas: o Codex não está autenticado neste host; " +
                         "entre com `codex login` na máquina do D.A.N.T.E.", await api.NextMessageAsync());
            Assert.Empty(drivers.Created);
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task ActiveSessionTurnAndPendingRequestAreLeftUntouched()
    {
        using var service = CreateService();
        await service.StartAsync(CancellationToken.None);
        try
        {
            api.Enqueue("/session start codex");
            Assert.Contains("S000001", await api.NextMessageAsync());
            api.Enqueue("trabalho longo");
            await Eventually(() => drivers.Created[0].Calls.Contains("turn:trabalho longo"));
            var before = sessions!.GetActive(123)!;
            var calls = drivers.Created[0].Calls;

            api.Enqueue("/uso codex");
            Assert.StartsWith("Uso — Codex", await api.NextMessageAsync());

            var after = sessions.GetActive(123)!;
            Assert.Equal(before.Id, after.Id);
            Assert.Equal(before.State, after.State);
            Assert.Equal(before.ModelLabel, after.ModelLabel);
            Assert.Equal(before.Profile, after.Profile);
            Assert.Equal(calls, drivers.Created[0].Calls);
            Assert.Single(drivers.Created);
            Assert.Empty(jobs.GetVisible());
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task UnauthorizedUserGetsNoAnswerAndNoQuery()
    {
        using var service = CreateService();
        await service.StartAsync(CancellationToken.None);
        try
        {
            api.Enqueue("/uso codex", 999);
            api.Enqueue("/ping");
            Assert.Equal("pong", await api.NextMessageAsync());
            Assert.Empty(reader.Queries);
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    private TelegramPollingService CreateService()
    {
        var options = Options.Create(new TelegramOptions { BotToken = "test", AllowedUserIds = "123" });
        var delivery = new TelegramDeliveryService(api, NullLogger<TelegramDeliveryService>.Instance);
        sessions = new SessionRegistry(drivers, NullLogger<SessionRegistry>.Instance, delivery);
        return new TelegramPollingService(api, options, new TelegramUserAuthorizer(options), Runner.Instance,
            Runner.Instance, jobs, NullLogger<TelegramPollingService>.Instance, null,
            new GeneralWorkspace(Path.Combine(root, "general")), new AssistantSettingsStore(Path.Combine(root, "s.json")),
            sessions, delivery, usage: reader);
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

    // Answers Codex like the provider would and refuses Claude like the reader of #116.
    private sealed class Reader : IUsageQuotaReader
    {
        private readonly ConcurrentQueue<AgentKind> queries = new();
        public UsageQueryException? Failure { get; set; }
        public IReadOnlyList<AgentKind> Queries => queries.ToArray();

        public Task<UsageReport> ReadAsync(AgentKind agent, CancellationToken cancellationToken = default)
        {
            queries.Enqueue(agent);
            if (agent == AgentKind.Claude)
                throw new UsageQueryException(UsageQueryFailure.Unsupported,
                    "a consulta de cotas do Claude ainda não está disponível no D.A.N.T.E.");
            if (Failure is not null) throw Failure;
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new UsageReport(agent, now,
                QuotaMetric.Of(new QuotaWindow(37, now + new TimeSpan(2, 13, 50), TimeSpan.FromHours(5))),
                QuotaMetric.Of(new QuotaWindow(62, now + TimeSpan.FromDays(4), TimeSpan.FromDays(7))), []));
        }
    }

    private sealed class Runner : ICodexRunner, IClaudeRunner
    {
        public static Runner Instance { get; } = new();

        public Task<AgentProcessResult> RunAsync(string prompt, string workingDirectory,
            CancellationToken cancellationToken = default, bool generalMode = false,
            IReadOnlyDictionary<string, string>? environment = null, string? model = null, string? effort = null,
            IReadOnlyList<Dante.Worker.Attachments.Attachment>? attachments = null) =>
            throw new InvalidOperationException("no one-shot expected");
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
