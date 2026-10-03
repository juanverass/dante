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

// /uso claude|codex (#116, #117) through the Telegram polling loop: one command, never a prompt, no effect on sessions.
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

            api.Enqueue("/uso CLAUDE");
            Assert.StartsWith("Uso — Claude\nJanela de sessão (5h): 41% do limite utilizado\nSemana: 58% do limite utilizado\n",
                await api.NextMessageAsync());
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
            drivers.Created[0].Emit(new ApprovalRequestedEvent("upstream", AgentToolKind.Command, "dotnet test"));
            await Eventually(() => sessions!.GetActive(123)!.PendingRequestIds.Count == 1);
            var before = sessions!.GetActive(123)!;
            var calls = drivers.Created[0].Calls;

            api.Enqueue("/uso codex");
            string reply;
            do reply = await api.NextMessageAsync();
            while (!reply.StartsWith("Uso — Codex", StringComparison.Ordinal));

            var after = sessions.GetActive(123)!;
            Assert.Equal(before.Id, after.Id);
            Assert.Equal(before.State, after.State);
            Assert.Equal(before.ModelLabel, after.ModelLabel);
            Assert.Equal(before.Profile, after.Profile);
            Assert.Equal(before.PendingRequestIds, after.PendingRequestIds);
            Assert.NotNull(sessions.GetPendingRequest(123, after.PendingRequestIds.Single()));
            Assert.Empty(drivers.Created[0].Responses);
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

    private TelegramPollingService CreateService(IUsageQuotaReader? usage = null)
    {
        var options = Options.Create(new TelegramOptions { BotToken = "test", AllowedUserIds = "123" });
        var delivery = new TelegramDeliveryService(api, NullLogger<TelegramDeliveryService>.Instance);
        sessions = new SessionRegistry(drivers, NullLogger<SessionRegistry>.Instance, delivery);
        return new TelegramPollingService(api, options, new TelegramUserAuthorizer(options), Runner.Instance,
            Runner.Instance, jobs, NullLogger<TelegramPollingService>.Instance, null,
            new GeneralWorkspace(Path.Combine(root, "general")), new AssistantSettingsStore(Path.Combine(root, "s.json")),
            sessions, delivery, usage: usage ?? reader);
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

    [Fact]
    public async Task BothArgumentsReachTheirOwnCliThroughTheRealReaderWithoutMixingResults()
    {
        var launcher = new UsageQuotaReaderTests.ProbeLauncher();
        using var service = CreateService(new UsageQuotaReader(launcher, new GeneralWorkspace(Path.Combine(root, "general"))));
        await service.StartAsync(CancellationToken.None);
        try
        {
            api.Enqueue("/uso claude");
            var claude = await api.NextMessageAsync();
            api.Enqueue("/uso codex");
            var codex = await api.NextMessageAsync();

            Assert.StartsWith("Uso — Claude\nJanela de sessão (5h): 41% do limite utilizado\nSemana: 58% do limite utilizado\n" +
                              "Janela de sessão renova em: ", claude);
            Assert.Contains("A CLI do Claude pode responder com uma leitura própria", claude);
            Assert.StartsWith("Uso — Codex\nJanela de sessão (5h): 37% do limite utilizado\nSemana: 62% do limite utilizado\n" +
                              "Janela de sessão renova em: ", codex);
            Assert.DoesNotContain("37%", claude);
            Assert.DoesNotContain("41%", codex);
            Assert.DoesNotContain("fake@example.invalid", claude + codex);
            Assert.Equal([AgentKind.Claude, AgentKind.Codex], launcher.Requests.Select(request => request.Agent));
            Assert.Empty(drivers.Created);
            Assert.Empty(jobs.GetVisible());
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task UsoIsTheOnlyUsageCommand()
    {
        Assert.Equal(["/uso"], TelegramCommandHelp.Entries
            .Where(entry => entry.Command.Contains("uso") || entry.Command.Contains("usage") ||
                            entry.Description.Contains("cota", StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Command));
        using var service = CreateService();
        await service.StartAsync(CancellationToken.None);
        try
        {
            foreach (var command in new[] { "/usage claude", "/uso-claude", "/uso-codex", "/cota codex" })
            {
                api.Enqueue(command);
                Assert.StartsWith("Comando desconhecido", await api.NextMessageAsync());
            }
            Assert.Empty(reader.Queries);
            Assert.Empty(drivers.Created);
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    // Answers each agent like its provider would, with different values so that results cannot be mixed up.
    private sealed class Reader : IUsageQuotaReader
    {
        private readonly ConcurrentQueue<AgentKind> queries = new();
        public UsageQueryException? Failure { get; set; }
        public IReadOnlyList<AgentKind> Queries => queries.ToArray();

        public Task<UsageReport> ReadAsync(AgentKind agent, CancellationToken cancellationToken = default)
        {
            queries.Enqueue(agent);
            if (Failure is not null) throw Failure;
            var now = DateTimeOffset.UtcNow;
            var (session, week) = agent == AgentKind.Codex ? (37, 62) : (41, 58);
            return Task.FromResult(new UsageReport(agent, now,
                QuotaMetric.Of(new QuotaWindow(session, now + new TimeSpan(2, 13, 50), TimeSpan.FromHours(5))),
                QuotaMetric.Of(new QuotaWindow(week, now + TimeSpan.FromDays(4), TimeSpan.FromDays(7))), []));
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
