using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;
using Dante.Application.Agentes;
using Dante.Application.Anexos;
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
            await Eventually(() => driver.Calls.Contains("turn:primeira tarefa"));
            api.Enqueue("segunda tarefa");
            Assert.Contains("Recebido", await api.NextMessageAsync());
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
    public async Task TelegramRoutesPermissionProfileApprovalDenialAndInputToTheMatchingTurn()
    {
        var api = new InteractiveApi { FailResult = false };
        var delivery = new TelegramDeliveryService(api, NullLogger<TelegramDeliveryService>.Instance);
        var drivers = new FakeSessionDriverFactory();
        await using var sessions = new SessionRegistry(drivers, NullLogger<SessionRegistry>.Instance, delivery);
        var options = Options.Create(new TelegramOptions { BotToken = "test", AllowedUserIds = "123" });
        var runner = new ImmediateRunner();
        using var service = new TelegramPollingService(api, options, new TelegramUserAuthorizer(options),
            runner, runner, new JobRegistry(), NullLogger<TelegramPollingService>.Instance,
            sessions: sessions, delivery: delivery);
        await service.StartAsync(CancellationToken.None);
        try
        {
            api.Enqueue("/permissions");
            Assert.Contains("manual (recomendado)", await api.NextMessageAsync());
            api.Enqueue("/permissions auto");
            Assert.Contains("Perfil para novas sessões: auto", await api.NextMessageAsync());
            api.Enqueue("/session start codex");
            Assert.Contains("modo auto (automático)", await api.NextMessageAsync());
            var driver = Assert.Single(drivers.Created);
            Assert.Equal(AgentPermissionProfile.Auto, driver.StartOptions!.Profile);
            api.Enqueue("tarefa");
            await Eventually(() => driver.Calls.Contains("turn:tarefa"));
            driver.Emit(new ApprovalRequestedEvent("upstream-approval", AgentToolKind.Command, "git push")
            { CanApproveForSession = true });
            await Eventually(() => sessions.GetActive(123)!.PendingRequestIds.Count == 1);
            var requestId = sessions.GetActive(123)!.PendingRequestIds.Single();
            var ids = $"S000001 T000001 {requestId}";
            var requestMessage = await api.NextMessageContainingAsync("Aprovação pendente");
            Assert.Contains("/approve-session " + ids, requestMessage);

            api.Enqueue("/approve S000001 T000002 " + requestId);
            Assert.Contains("não encontrada", await api.NextMessageContainingAsync("Solicitação não encontrada"));
            Assert.Empty(driver.Responses);
            api.Enqueue("/status");
            Assert.Contains("aguardando " + requestId, await api.NextMessageContainingAsync("Sessões:"));
            api.Enqueue("/approve-session " + ids);
            Assert.Contains("Resposta entregue", await api.NextMessageContainingAsync("Resposta entregue"));
            Assert.Equal(AgentApprovalDecision.ApproveForSession,
                Assert.IsType<AgentApprovalResponse>(driver.Responses.Single().Response).Decision);
            api.Enqueue("/approve " + ids);
            Assert.Contains("não encontrada", await api.NextMessageContainingAsync("Solicitação não encontrada"));

            driver.Emit(new UserInputRequestedEvent("upstream-input", [
                new AgentQuestion("q1", "Primeira?", ["sim", "não"]),
                new AgentQuestion("q2", "Segunda?", [])]));
            await Eventually(() => sessions.GetActive(123)!.PendingRequestIds.Count == 1);
            var inputId = sessions.GetActive(123)!.PendingRequestIds.Single();
            var inputIds = $"S000001 T000001 {inputId}";
            api.Enqueue("/input " + inputIds + " sim");
            Assert.Contains("2 resposta(s)", await api.NextMessageContainingAsync("2 resposta(s)"));
            api.Enqueue("/input " + inputIds + " sim | não");
            Assert.Contains("Resposta entregue", await api.NextMessageContainingAsync("Resposta entregue"));
            var input = Assert.IsType<AgentInputResponse>(driver.Responses.Last().Response);
            Assert.Equal("não", input.Answers["q2"]);

            driver.Emit(new ApprovalRequestedEvent("upstream-deny", AgentToolKind.FileChange, "alterar"));
            await Eventually(() => sessions.GetActive(123)!.PendingRequestIds.Count == 1);
            var denyId = sessions.GetActive(123)!.PendingRequestIds.Single();
            api.Enqueue($"/deny S000001 T000001 {denyId} não permitido");
            Assert.Contains("Resposta entregue", await api.NextMessageContainingAsync("Resposta entregue"));
            var denial = Assert.IsType<AgentApprovalResponse>(driver.Responses.Last().Response);
            Assert.Equal((AgentApprovalDecision.Deny, "não permitido"), (denial.Decision, denial.Reason));

            api.Enqueue("/session start claude plan");
            Assert.Contains("modo plan", await api.NextMessageContainingAsync("modo plan"));
            Assert.Equal(AgentPermissionProfile.Plan, drivers.Created.Last().StartOptions!.Profile);
            api.Enqueue("/permissions full");
            Assert.Contains("Acesso full não é oferecido", await api.NextMessageContainingAsync("Acesso full"));
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
        var first = Snapshot("S1", active: false);
        var second = Snapshot("S2", active: false);

        await delivery.PublishAsync(first, new TurnStartedEvent { SessionId = "S1", TurnId = "T1" }, default);
        await delivery.PublishAsync(second, new TurnStartedEvent { SessionId = "S2", TurnId = "T2" }, default);
        for (var index = 0; index < 200; index++)
        {
            await delivery.PublishAsync(first, new MessageDeltaEvent("item", "a\n")
                { SessionId = "S1", TurnId = "T1" }, default);
            await delivery.PublishAsync(second, new MessageDeltaEvent("item", "b\n")
                { SessionId = "S2", TurnId = "T2" }, default);
        }
        await Eventually(() => api.Messages.Count >= 2);

        Assert.All(api.Messages, message => Assert.InRange(message.Text.Length, 1, 4000));
        Assert.All(api.Messages, message => Assert.True(message.Text.StartsWith("[S1] ") ||
            message.Text.StartsWith("[S2] ")));
        var lines = (string text) => string.Concat(Enumerable.Repeat(text + "\n", 20));
        await Eventually(() => api.Messages.Any(message => message.Text.Contains(lines("a"))) &&
            api.Messages.Any(message => message.Text.Contains(lines("b"))));
        Assert.True(api.Messages.Count < 20);
        Assert.DoesNotContain(api.Messages, message => message.Text.Contains('a') && message.Text.Contains('b'));
        await delivery.PublishAsync(first, new TurnCompletedEvent(AgentTurnOutcome.Completed)
            { SessionId = "S1", TurnId = "T1" }, default);
        await Eventually(() => delivery.Get("S1", 123)?.State == TelegramDeliveryState.Delivered);
        var firstOutput = string.Concat(api.Messages.Where(message => message.Text.StartsWith("[S1] "))
            .Select(message => message.Text["[S1] ".Length..]));
        Assert.Equal(string.Concat(Enumerable.Repeat("a\n", 200)), firstOutput);
    }

    [Fact]
    public async Task ActiveSessionReadsLikeAConversationAndShowsTypingOnlyWhileTheAgentWorks()
    {
        var api = new RecordingApi();
        var delivery = new TelegramDeliveryService(api, NullLogger<TelegramDeliveryService>.Instance)
        {
            TypingInterval = TimeSpan.FromMilliseconds(50)
        };
        delivery.RegisterSession("S1", 123, -123, false);
        delivery.SetActiveSession(123, "S1");
        var session = Snapshot("S1");
        Task Publish(AgentEvent agentEvent, string? turn = "T1", AgentSessionSnapshot? snapshot = null) =>
            delivery.PublishAsync(snapshot ?? session, agentEvent with { SessionId = "S1", TurnId = turn }, default);

        await Publish(new TurnStartedEvent());
        await Eventually(() => api.ChatActions > 0);
        await Publish(new MessageDeltaEvent("m1", "Vou rodar os testes"));
        await Publish(new ToolStartedEvent("c1", AgentToolKind.Command, "dotnet test /tmp/general/tests/A.csproj"));
        await Publish(new ToolCompletedEvent("c1", AgentToolKind.Command, false));
        await Publish(new FileChangeEvent(["/tmp/general/src/A.cs", "/outside/B.cs"]));
        await Publish(new MessageCompletedEvent("blank", "\n"));
        await Eventually(() => api.Messages.Count > 0);
        await Publish(new MessageCompletedEvent("leading", "\n\nDepois de uma linha em branco."));
        await Publish(new ToolStartedEvent("c2", AgentToolKind.Command, "/bin/bash -lc 'git status --short'"));
        await Publish(new ApprovalRequestedEvent("upstream", AgentToolKind.Command, "git push")
            { RequestId = "R1" });
        // Waiting for the user is not "typing".
        await Task.Delay(200);
        var whileWaiting = api.ChatActions;
        await Task.Delay(300);
        Assert.Equal(whileWaiting, api.ChatActions);
        await Publish(new MessageCompletedEvent("m2", "Testes falharam."));
        await Eventually(() => api.ChatActions > whileWaiting);
        await Publish(new TurnCompletedEvent(AgentTurnOutcome.Completed));
        await Eventually(() => delivery.Get("S1", 123)?.State == TelegramDeliveryState.Delivered);
        var afterTurn = api.ChatActions;
        await Task.Delay(300);
        Assert.Equal(afterTurn, api.ChatActions);

        await Publish(new TurnStartedEvent(), "T2");
        await Publish(new MessageCompletedEvent("only-blank", " \n"), "T2");
        await Publish(new TurnCompletedEvent(AgentTurnOutcome.Completed), "T2");
        await Publish(new TurnStartedEvent(), "T3");
        await Publish(new TurnCompletedEvent(AgentTurnOutcome.Failed, "boom"), "T3");
        await Publish(new ErrorEvent("O processo do agente encerrou inesperadamente."), null,
            Snapshot("S1", state: AgentSessionState.Failed));
        await Eventually(() => api.Messages.Any(message => message.Text.Contains("/session start")) &&
            api.Messages.Any(message => message.Text.Contains("boom")) &&
            api.Messages.Any(message => message.Text.Contains("sem resposta")));

        var output = string.Join("\n---\n", api.Messages.Select(message => message.Text));
        Assert.Contains("Vou rodar os testes\n→ dotnet test tests/A.csproj\n✗ dotnet test tests/A.csproj falhou.\n" +
            "Arquivos alterados: src/A.cs, /outside/B.cs\n", output);
        Assert.Contains("→ git status --short\n", output);
        Assert.Contains("Depois de uma linha em branco.", output);
        Assert.DoesNotContain(api.Messages, message => string.IsNullOrWhiteSpace(message.Text));
        Assert.DoesNotContain(api.Messages, message => message.Text.StartsWith('\n'));
        Assert.Contains("Aprovação pendente S1 T1 R1", output);
        Assert.Contains("Testes falharam.", output);
        Assert.Contains("(sem resposta do agente)", output);
        Assert.Contains("A resposta falhou: boom", output);
        Assert.Contains("A sessão S1 foi encerrada: O processo do agente encerrou inesperadamente.", output);
        Assert.DoesNotContain("Turno", output);
        Assert.DoesNotContain("[S1", output);
        Assert.DoesNotContain("Job", output);
    }

    [Fact]
    public async Task SessionThatStopsBeingActiveMidTurnIsIdentifiedAndStopsTyping()
    {
        var api = new RecordingApi();
        var delivery = new TelegramDeliveryService(api, NullLogger<TelegramDeliveryService>.Instance)
        {
            TypingInterval = TimeSpan.FromMilliseconds(50),
            PartInterval = TimeSpan.FromMilliseconds(50)
        };
        delivery.RegisterSession("S1", 123, -123, false);
        delivery.RegisterSession("S2", 123, -123, false);
        delivery.SetActiveSession(123, "S1");
        Task Publish(string sessionId, AgentEvent agentEvent) => delivery.PublishAsync(Snapshot(sessionId),
            agentEvent with { SessionId = sessionId, TurnId = "T1" }, default);

        await Publish("S1", new TurnStartedEvent());
        await Publish("S1", new MessageDeltaEvent("m1", "primeira parte\n"));
        await Eventually(() => api.Messages.Any(message => message.Text == "primeira parte\n"));
        Assert.True(api.ChatActions > 0);

        // /session start or /session select moves the conversation to S2 while S1's turn is still running.
        delivery.SetActiveSession(123, "S2");
        await Task.Delay(200);
        var afterSwitch = api.ChatActions;
        await Task.Delay(300);
        Assert.Equal(afterSwitch, api.ChatActions);

        await Publish("S1", new MessageDeltaEvent("m1", "segunda parte\n"));
        await Eventually(() => api.Messages.Any(message => message.Text == "[S1] segunda parte\n"));
        await Task.Delay(300);
        Assert.Equal(afterSwitch, api.ChatActions);

        await Publish("S2", new TurnStartedEvent());
        await Publish("S2", new MessageCompletedEvent("m2", "resposta de S2"));
        await Eventually(() => api.Messages.Any(message => message.Text == "resposta de S2\n"));
        await Eventually(() => api.ChatActions > afterSwitch);
        await Publish("S1", new TurnCompletedEvent(AgentTurnOutcome.Failed, "boom"));
        await Eventually(() => api.Messages.Any(message => message.Text == "[S1] A resposta falhou: boom\n"));
        Assert.DoesNotContain(api.Messages, message => message.Text.StartsWith("[S2]"));
    }

    [Fact]
    public async Task ConsecutivePartsOfATurnAreSpacedEvenWhenOneFlushProducesSeveral()
    {
        var api = new RecordingApi();
        var delivery = new TelegramDeliveryService(api, NullLogger<TelegramDeliveryService>.Instance)
        {
            PartInterval = TimeSpan.FromMilliseconds(400)
        };
        delivery.RegisterSession("S1", 123, -123, false);
        delivery.SetActiveSession(123, "S1");
        var session = Snapshot("S1");
        await delivery.PublishAsync(session, new MessageCompletedEvent("m1", new string('x', 9000))
            { SessionId = "S1", TurnId = "T1" }, default);
        await delivery.PublishAsync(session, new TurnCompletedEvent(AgentTurnOutcome.Completed)
            { SessionId = "S1", TurnId = "T1" }, default);

        await Eventually(() => delivery.Get("S1", 123)?.State == TelegramDeliveryState.Delivered);
        var sent = api.Sent;
        Assert.Equal(3, sent.Count);
        Assert.All(sent.Zip(sent.Skip(1)), pair =>
            Assert.True(pair.Second - pair.First >= TimeSpan.FromMilliseconds(380), $"{pair.Second - pair.First}"));
    }

    [Fact]
    public async Task FinalEventArrivingAfterBatchDrainsIsScheduledWithoutAnotherEvent()
    {
        var api = new RecordingApi();
        var delivery = new TelegramDeliveryService(api, NullLogger<TelegramDeliveryService>.Instance);
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        delivery.BeforeScheduledDeliveryCleanupAsync = async () =>
        {
            drained.TrySetResult();
            await finishCleanup.Task;
        };
        delivery.RegisterSession("S1", 123, -123, false);
        var session = Snapshot("S1");

        await delivery.PublishAsync(session, new TurnStartedEvent { SessionId = "S1", TurnId = "T1" }, default);
        await delivery.PublishAsync(session, new MessageCompletedEvent("item", "parcial")
            { SessionId = "S1", TurnId = "T1" }, default);
        await drained.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TelegramDeliveryState.Delivered, delivery.Get("S1", 123)!.State);
        await delivery.PublishAsync(session, new TurnCompletedEvent(AgentTurnOutcome.Interrupted)
            { SessionId = "S1", TurnId = "T1" }, default);
        Assert.Equal(TelegramDeliveryState.Pending, delivery.Get("S1", 123)!.State);

        finishCleanup.SetResult();
        await Eventually(() => delivery.Get("S1", 123)?.State == TelegramDeliveryState.Delivered &&
            api.Messages.Any(message => message.Text.Contains("Resposta interrompida")));
        Assert.Equal(2, api.Messages.Count);
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
            // A short progress line cannot complete the secret: it is not held back until the turn ends.
            await delivery.PublishAsync(session, new ToolStartedEvent("c1", AgentToolKind.Command, "dotnet test")
                { SessionId = "S1", TurnId = "T1" }, default);
            await Eventually(() => api.Messages.Any(message => message.Text.Contains("→ dotnet test")));
            await delivery.PublishAsync(session, new MessageDeltaEvent("item",
                new string('x', 100) + "\n" + new string('z', 40) + "dante-cross-")
                { SessionId = "S1", TurnId = "T1" }, default);
            await Eventually(() => api.Messages.Count > 1);
            Assert.DoesNotContain(api.Messages, message => message.Text.Contains("dante-cross-"));
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

    [Fact]
    public async Task RequestsWithBoundSecretsShowCommandsButNotAgentDetails()
    {
        var api = new RecordingApi();
        var delivery = new TelegramDeliveryService(api, NullLogger<TelegramDeliveryService>.Instance);
        delivery.RegisterSession("S1", 123, -123, hideOutput: true);
        var session = Snapshot("S1");
        await delivery.PublishAsync(session, new ApprovalRequestedEvent("upstream", AgentToolKind.Command,
            "comando com segredo") { SessionId = "S1", TurnId = "T1", RequestId = "R1" }, default);
        await delivery.PublishAsync(session, new UserInputRequestedEvent("upstream-2",
            [new AgentQuestion("q1", "pergunta com segredo", [])])
        { SessionId = "S1", TurnId = "T1", RequestId = "R2" }, default);
        await delivery.PublishAsync(session, new RequestExpiredEvent("R1")
        { SessionId = "S1", TurnId = "T1" }, default);

        await Eventually(() => api.Messages.Any(message => message.Text.Contains("A solicitação R1 expirou")));
        var output = string.Concat(api.Messages.Select(message => message.Text));
        Assert.Contains("/deny S1 T1 R1", output);
        Assert.Contains("/input S1 T1 R2", output);
        Assert.Contains("A solicitação R1 expirou", output);
        Assert.DoesNotContain("comando com segredo", output);
        Assert.DoesNotContain("pergunta com segredo", output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FencedSecretSplitAcrossBatchesIsRedactedBeforeHtmlAndPlainFallback(bool fallback)
    {
        const string secret = "dante-secret-<&>-suffix";
        const string name = "OPENAI_API_KEY";
        var previous = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, secret);
        try
        {
            var api = new SecretFormattedApi(fallback);
            var delivery = new TelegramDeliveryService(api, NullLogger<TelegramDeliveryService>.Instance)
                { PartInterval = TimeSpan.Zero };
            delivery.RegisterSession("S1", 123, -123, false);
            var session = Snapshot("S1");
            await delivery.PublishAsync(session, new MessageDeltaEvent("item", "```python\nprint(1)\ndante-secret-")
                { SessionId = "S1", TurnId = "T1" }, default);
            await Eventually(() => api.Output.Count > 0);
            Assert.DoesNotContain(api.Output, text => text.Contains("dante-secret-"));
            // A tool flush must also retain the partial secret instead of serializing it into HTML.
            await delivery.PublishAsync(session, new ToolStartedEvent("cmd", AgentToolKind.Command, "echo one\necho two")
                { SessionId = "S1", TurnId = "T1" }, default);
            // Drain the scheduled batch while the secret is still incomplete: the command must remain pending.
            await Task.Delay(1000);
            Assert.Single(api.Output);
            Assert.DoesNotContain(api.Output, text => text.Contains("dante-secret-"));
            await delivery.PublishAsync(session, new MessageDeltaEvent("item", "<&>-suffix\n```\nFim")
                { SessionId = "S1", TurnId = "T1" }, default);
            await delivery.PublishAsync(session, new TurnCompletedEvent(AgentTurnOutcome.Completed)
                { SessionId = "S1", TurnId = "T1" }, default);
            await Eventually(() => delivery.Get("S1", 123)?.State == TelegramDeliveryState.Delivered);
            var output = string.Concat(api.Output);
            Assert.DoesNotContain(secret, output);
            Assert.DoesNotContain("dante-secret-", output);
            Assert.Contains("[segredo omitido]", output);
            Assert.True(output.IndexOf("[segredo omitido]", StringComparison.Ordinal) <
                output.IndexOf("→ Executando comando", StringComparison.Ordinal));
            Assert.True(output.IndexOf("echo two", StringComparison.Ordinal) < output.IndexOf("Fim", StringComparison.Ordinal));
        }
        finally { Environment.SetEnvironmentVariable(name, previous); }
    }

    private sealed class SecretFormattedApi(bool fallback) : ITelegramBotApi
    {
        public ConcurrentQueue<string> Output { get; } = new();
        public Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(long offset, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task SendMessageAsync(long chatId, string text, CancellationToken cancellationToken)
        {
            Output.Enqueue(text);
            return Task.CompletedTask;
        }
        public Task<long?> SendFormattedMessageAsync(long chatId, TelegramFormattedMessage message,
            TelegramInlineKeyboard? keyboard, CancellationToken cancellationToken)
        {
            Assert.DoesNotContain("dante-secret-", message.Html);
            if (fallback) throw new TelegramMarkupException();
            TelegramMessageFormatterTests.AssertValid(message);
            Output.Enqueue(message.PlainText);
            return Task.FromResult<long?>(null);
        }
    }

    private static AgentSessionSnapshot Snapshot(string id, bool active = true,
        AgentSessionState state = AgentSessionState.Running) => new(id, AgentKind.Codex, 123,
        JobExecutionContext.General("/tmp/general"), AgentPermissionProfile.Manual,
        state, "T1", 0, [], active, DateTimeOffset.UtcNow, null, null);

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
        public IReadOnlyList<DateTimeOffset> Sent => sent.ToArray();
        private readonly ConcurrentQueue<DateTimeOffset> sent = new();
        public int ChatActions => chatActions;
        private int chatActions;

        public Task SendChatActionAsync(long chatId, string action, CancellationToken cancellationToken)
        {
            Assert.Equal("typing", action);
            Interlocked.Increment(ref chatActions);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(long offset, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SendMessageAsync(long chatId, string text, CancellationToken cancellationToken)
        {
            var attempt = Interlocked.Increment(ref attempts);
            if (attempt <= Failures || attempt == FailOnAttempt)
                throw new HttpRequestException("falha simulada", null, FailureStatus);
            messages.Enqueue((chatId, text));
            sent.Enqueue(DateTimeOffset.UtcNow);
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
        public async Task<string> NextMessageContainingAsync(string expected)
        {
            for (var index = 0; index < 20; index++)
            {
                var message = await NextMessageAsync();
                if (message.Contains(expected, StringComparison.Ordinal)) return message;
            }
            throw new Xunit.Sdk.XunitException($"Mensagem esperada não recebida: {expected}");
        }
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
            IReadOnlyDictionary<string, string>? environment = null, string? model = null, string? effort = null,
            IReadOnlyList<Dante.Application.Anexos.Attachment>? attachments = null)
        {
            Calls++;
            return Task.FromResult(new AgentProcessResult(AgentProcessStatus.Succeeded,
                "resultado persistido", "", 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        }
    }
}
