using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;
using Dante.Worker.Agents;
using Dante.Worker.Jobs;
using Dante.Worker.Sessions;
using Dante.Worker.Telegram;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dante.Tests;

public sealed class TelegramDeliveryServiceTests
{
    [Fact]
    public async Task CompletedJobWithFailedTelegramDeliveryCanBeResentWithoutRerunningAgent()
    {
        var api = new InteractiveApi();
        var delivery = new TelegramDeliveryService(api, NullLogger<TelegramDeliveryService>.Instance);
        var jobs = new JobRegistry();
        var runner = new ImmediateRunner();
        var options = Options.Create(new TelegramOptions { BotToken = "test", AllowedUserIds = "123" });
        using var service = new TelegramPollingService(api, options, new TelegramUserAuthorizer(options),
            runner, runner, jobs, NullLogger<TelegramPollingService>.Instance, delivery: delivery);
        await service.StartAsync(CancellationToken.None);
        try
        {
            api.Enqueue("/codex tarefa");
            Assert.Contains("Job ID: J000001", await api.NextMessageAsync());
            await Eventually(() => delivery.Get("J000001", 123)?.State == TelegramDeliveryState.Failed);
            Assert.Equal(JobStatus.Succeeded, Assert.Single(jobs.GetVisible()).Status);

            api.Enqueue("/status");
            var status = await api.NextMessageAsync();
            Assert.Contains("J000001 Codex General: Succeeded", status);
            Assert.Contains("J000001: Failed", status);

            api.FailResult = false;
            api.Enqueue("/resend J000001");
            var first = await api.NextMessageAsync();
            var second = await api.NextMessageAsync();
            Assert.Contains(new[] { first, second }, text => text.Contains("resultado persistido"));
            Assert.Contains(new[] { first, second }, text => text.Contains("J000001: Delivered"));
            Assert.Equal(1, runner.Calls);
            Assert.Equal(TelegramDeliveryState.Delivered, delivery.Get("J000001", 123)!.State);
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task SessionCommandsRouteQueueSteerInterruptAndNewTurnWhilePollingStaysResponsive()
    {
        var api = new InteractiveApi { FailResult = false };
        var delivery = new TelegramDeliveryService(api, NullLogger<TelegramDeliveryService>.Instance);
        var drivers = new FakeSessionDriverFactory();
        await using var sessions = new SessionRegistry(drivers, NullLogger<SessionRegistry>.Instance, delivery);
        var runner = new ImmediateRunner();
        var options = Options.Create(new TelegramOptions { BotToken = "test", AllowedUserIds = "123" });
        using var service = new TelegramPollingService(api, options, new TelegramUserAuthorizer(options),
            runner, runner, new JobRegistry(), NullLogger<TelegramPollingService>.Instance,
            sessions: sessions, delivery: delivery);
        await service.StartAsync(CancellationToken.None);
        try
        {
            api.Enqueue("/session start codex");
            Assert.Contains("Sessão S000001 iniciada", await api.NextMessageAsync());
            var driver = Assert.Single(drivers.Created);
            api.Enqueue("primeira tarefa");
            Assert.Contains("Turno T000001 iniciado", await api.NextMessageAsync());
            api.Enqueue("segunda tarefa");
            Assert.Contains("enfileirada", await api.NextMessageAsync());
            api.Enqueue("/steer mude o foco");
            Assert.Contains("Orientação enviada", await api.NextMessageAsync());
            api.Enqueue("/ping");
            Assert.Equal("pong", await api.NextMessageAsync());
            Assert.Equal(["start", "turn:primeira tarefa", "steer:mude o foco"], driver.Calls);

            api.Enqueue("/session stop");
            Assert.Contains("1 mensagem(ns) removida(s)", await api.NextMessageAsync());
            Assert.Contains("interrupt", driver.Calls);
            driver.Emit(new TurnCompletedEvent(AgentTurnOutcome.Interrupted));
            await Eventually(() => sessions.GetActive(123)!.State == AgentSessionState.Idle);

            api.Enqueue("novo turno");
            await Eventually(() => driver.Calls.Contains("turn:novo turno"));
            api.Enqueue("/session close");
            await Eventually(() => driver.Calls.Contains("close"));
            Assert.Equal(0, runner.Calls);
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task StatusSeparatesCompletedSessionTurnFromFailedDeliveryAndResendsIt()
    {
        var api = new InteractiveApi();
        var delivery = new TelegramDeliveryService(api, NullLogger<TelegramDeliveryService>.Instance);
        var drivers = new FakeSessionDriverFactory();
        await using var sessions = new SessionRegistry(drivers, NullLogger<SessionRegistry>.Instance, delivery);
        var started = await sessions.StartAsync(new SessionStartRequest(123, AgentKind.Codex,
            JobExecutionContext.General("/tmp/general")));
        delivery.RegisterSession(started.Session!.Id, 123, -123, false);
        await sessions.SubmitAsync(123, null, "tarefa");
        var driver = Assert.Single(drivers.Created);
        driver.Emit(new MessageCompletedEvent("item", "resultado persistido"));
        driver.Emit(new TurnCompletedEvent(AgentTurnOutcome.Completed));
        await Eventually(() => delivery.Get("S000001", 123)?.State == TelegramDeliveryState.Failed);

        var options = Options.Create(new TelegramOptions { BotToken = "test", AllowedUserIds = "123" });
        var runner = new ImmediateRunner();
        using var service = new TelegramPollingService(api, options, new TelegramUserAuthorizer(options),
            runner, runner, new JobRegistry(), NullLogger<TelegramPollingService>.Instance,
            sessions: sessions, delivery: delivery);
        await service.StartAsync(CancellationToken.None);
        try
        {
            api.Enqueue("/status");
            var status = await api.NextMessageAsync();
            Assert.Contains("último turno concluído", status);
            Assert.Contains("S000001/T000001: Failed", status);

            api.FailResult = false;
            api.Enqueue("/resend S000001");
            var output = await api.NextMessageAsync();
            var confirmation = await api.NextMessageAsync();
            Assert.Contains("resultado persistido", output);
            Assert.Contains("Delivered", confirmation);
            Assert.Equal(["start", "turn:tarefa"], driver.Calls);
            Assert.Equal(0, runner.Calls);
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task TransientFailureRetriesOnlyTelegramDelivery()
    {
        var api = new RecordingApi { Failures = 1, FailureStatus = HttpStatusCode.ServiceUnavailable };
        var delivery = new TelegramDeliveryService(api, NullLogger<TelegramDeliveryService>.Instance);

        await delivery.DeliverJobAsync("J1", 123, -123, "resultado concluído", CancellationToken.None);

        Assert.Equal(TelegramDeliveryState.Delivered, delivery.Get("J1", 123)!.State);
        Assert.Equal(2, api.Attempts);
        Assert.Equal(["resultado concluído"], api.Messages.Select(message => message.Text));
    }

    [Fact]
    public async Task FailedMiddleChunkAndRemainingOutputAreRecoveredWithoutStartingAnAgent()
    {
        var api = new RecordingApi { FailOnAttempt = 2, FailureStatus = HttpStatusCode.BadRequest };
        var delivery = new TelegramDeliveryService(api, NullLogger<TelegramDeliveryService>.Instance);
        var output = new string('x', 9000);

        await delivery.DeliverJobAsync("J2", 123, -123, output, CancellationToken.None);
        var failed = delivery.Get("J2", 123)!;
        Assert.Equal(TelegramDeliveryState.Failed, failed.State);
        Assert.Equal(1, failed.DeliveredChunks);
        Assert.Equal(3, failed.TotalChunks);
        Assert.Null(delivery.Get("J2", 999));

        api.FailOnAttempt = 0;
        var recovered = await delivery.RetryAsync("J2", 123, -123, CancellationToken.None);

        Assert.Equal(TelegramDeliveryState.Delivered, recovered!.State);
        Assert.Equal(3, api.Messages.Count);
        Assert.Equal(output, string.Concat(api.Messages.Select(message => message.Text)));
    }

    [Fact]
    public async Task SessionProgressIsBatchedAndConcurrentSessionsRemainIdentified()
    {
        var api = new RecordingApi();
        var delivery = new TelegramDeliveryService(api, NullLogger<TelegramDeliveryService>.Instance);
        delivery.RegisterSession("S1", 123, -123, false);
        delivery.RegisterSession("S2", 123, -123, false);
        var first = Snapshot("S1");
        var second = Snapshot("S2");

        await delivery.PublishAsync(first, new TurnStartedEvent { SessionId = "S1", TurnId = "T1" }, default);
        await delivery.PublishAsync(second, new TurnStartedEvent { SessionId = "S2", TurnId = "T2" }, default);
        for (var index = 0; index < 200; index++)
        {
            await delivery.PublishAsync(first, new MessageDeltaEvent("item", "a")
                { SessionId = "S1", TurnId = "T1" }, default);
            await delivery.PublishAsync(second, new MessageDeltaEvent("item", "b")
                { SessionId = "S2", TurnId = "T2" }, default);
        }
        await Eventually(() => api.Messages.Count >= 2);

        Assert.All(api.Messages, message => Assert.InRange(message.Text.Length, 1, 4000));
        Assert.All(api.Messages, message => Assert.True(message.Text.StartsWith("[S1 T1] ") ||
            message.Text.StartsWith("[S2 T2] ")));
        await Eventually(() => api.Messages.Any(message => message.Text.Contains(new string('a', 20))) &&
            api.Messages.Any(message => message.Text.Contains(new string('b', 20))));
        Assert.True(api.Messages.Count < 20);
        Assert.DoesNotContain(api.Messages, message => message.Text.Contains(new string('a', 20)) &&
            message.Text.Contains(new string('b', 20)));
        var firstOutput = string.Concat(api.Messages.Where(message => message.Text.StartsWith("[S1 T1] "))
            .Select(message => message.Text));
        var secondOutput = string.Concat(api.Messages.Where(message => message.Text.StartsWith("[S2 T2] "))
            .Select(message => message.Text));
        Assert.Contains("Turno iniciado", firstOutput);
        Assert.Contains("Turno iniciado", secondOutput);
        Assert.True(firstOutput.IndexOf("Turno iniciado", StringComparison.Ordinal) <
            firstOutput.IndexOf(new string('a', 20), StringComparison.Ordinal));
        Assert.True(secondOutput.IndexOf("Turno iniciado", StringComparison.Ordinal) <
            secondOutput.IndexOf(new string('b', 20), StringComparison.Ordinal));

        await delivery.PublishAsync(first, new TurnCompletedEvent(AgentTurnOutcome.Completed)
            { SessionId = "S1", TurnId = "T1" }, default);
        await Eventually(() => api.Messages.Any(message => message.Text.Contains("Turno concluído")));
        Assert.Equal(TelegramDeliveryState.Delivered, delivery.Get("S1", 123)!.State);
    }

    [Fact]
    public async Task StreamingRedactsSecretSplitAcrossDeltasAndDeliveryBatches()
    {
        const string name = "OPENAI_API_KEY";
        const string secret = "dante-cross-boundary-secret";
        var previous = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, secret);
        try
        {
            var api = new RecordingApi();
            var delivery = new TelegramDeliveryService(api, NullLogger<TelegramDeliveryService>.Instance);
            delivery.RegisterSession("S1", 123, -123, false);
            var session = Snapshot("S1");
            await delivery.PublishAsync(session, new TurnStartedEvent { SessionId = "S1", TurnId = "T1" }, default);
            await delivery.PublishAsync(session, new MessageDeltaEvent("item", new string('x', 100) + "dante-cross-")
                { SessionId = "S1", TurnId = "T1" }, default);
            await Eventually(() => api.Messages.Count > 0);
            await delivery.PublishAsync(session, new MessageDeltaEvent("item", "boundary-secret" + new string('y', 100))
                { SessionId = "S1", TurnId = "T1" }, default);
            await delivery.PublishAsync(session, new TurnCompletedEvent(AgentTurnOutcome.Completed)
                { SessionId = "S1", TurnId = "T1" }, default);
            await Eventually(() => delivery.Get("S1", 123)?.State == TelegramDeliveryState.Delivered);
            var output = string.Concat(api.Messages.Select(message => message.Text));
            Assert.DoesNotContain(secret, output);
            Assert.Contains("[segredo omitido]", output);
        }
        finally { Environment.SetEnvironmentVariable(name, previous); }
    }

    private static AgentSessionSnapshot Snapshot(string id) => new(id, AgentKind.Codex, 123,
        JobExecutionContext.General("/tmp/general"), AgentPermissionProfile.Manual,
        AgentSessionState.Running, "T1", 0, [], true, DateTimeOffset.UtcNow, null, null);

    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
        Assert.True(condition());
    }

    private sealed class RecordingApi : ITelegramBotApi
    {
        private readonly ConcurrentQueue<(long ChatId, string Text)> messages = new();
        private int attempts;
        public int Failures { get; set; }
        public int FailOnAttempt { get; set; }
        public HttpStatusCode FailureStatus { get; set; }
        public int Attempts => attempts;
        public IReadOnlyList<(long ChatId, string Text)> Messages => messages.ToArray();

        public Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(long offset, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SendMessageAsync(long chatId, string text, CancellationToken cancellationToken)
        {
            var attempt = Interlocked.Increment(ref attempts);
            if (attempt <= Failures || attempt == FailOnAttempt)
                throw new HttpRequestException("falha simulada", null, FailureStatus);
            messages.Enqueue((chatId, text));
            return Task.CompletedTask;
        }
    }

    private sealed class InteractiveApi : ITelegramBotApi
    {
        private readonly Channel<TelegramUpdate> updates = Channel.CreateUnbounded<TelegramUpdate>();
        private readonly Channel<string> messages = Channel.CreateUnbounded<string>();
        private long nextId;
        public bool FailResult { get; set; } = true;

        public void Enqueue(string text) => updates.Writer.TryWrite(new TelegramUpdate(
            Interlocked.Increment(ref nextId), new TelegramMessage(new TelegramChat(-123), text,
                new TelegramUser(123))));
        public async Task<string> NextMessageAsync() =>
            await messages.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        public async Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(long offset,
            CancellationToken cancellationToken) => [await updates.Reader.ReadAsync(cancellationToken)];
        public Task SendMessageAsync(long chatId, string text, CancellationToken cancellationToken)
        {
            if (FailResult && text.Contains("resultado persistido"))
                throw new HttpRequestException("falha definitiva", null, HttpStatusCode.BadRequest);
            messages.Writer.TryWrite(text);
            return Task.CompletedTask;
        }
    }

    private sealed class ImmediateRunner : ICodexRunner, IClaudeRunner
    {
        public int Calls { get; private set; }
        public Task<AgentProcessResult> RunAsync(string prompt, string workingDirectory,
            CancellationToken cancellationToken = default, bool generalMode = false,
            IReadOnlyDictionary<string, string>? environment = null)
        {
            Calls++;
            return Task.FromResult(new AgentProcessResult(AgentProcessStatus.Succeeded,
                "resultado persistido", "", 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        }
    }
}
