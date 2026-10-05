using Dante.Application.Agentes;
using Dante.Application.Anexos;
using Dante.Infrastructure.Agentes;
using Dante.Worker.Attachments;
using Dante.Worker.Jobs;
using Dante.Worker.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dante.Tests;

public sealed class SessionRegistryTests
{
    private const long Owner = 42;
    private const long Intruder = 7;
    private static readonly JobExecutionContext Repository = JobExecutionContext.Repository("@dante", "/repos/dante");
    private readonly FakeSessionDriverFactory drivers = new();
    private readonly RecordingSessionSink sink = new();

    [Fact]
    public async Task StartedSessionKeepsItsResolvedContextAndBecomesTheActiveSession()
    {
        await using var registry = CreateRegistry();
        var environment = new Dictionary<string, string> { ["DANTE_ENV"] = "dev" };

        var result = await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Codex, Repository,
            environment, AgentPermissionProfile.Plan));

        Assert.True(result.Accepted);
        var session = result.Session!;
        Assert.Equal(("S000001", AgentKind.Codex, Owner), (session.Id, session.Agent, session.OwnerUserId));
        Assert.Equal((Repository, AgentPermissionProfile.Plan), (session.Context, session.Profile));
        Assert.Equal(AgentSessionState.Idle, session.State);
        Assert.True(session.IsActive);
        var options = drivers.Created.Single().StartOptions!;
        Assert.Equal(("/repos/dante", false, AgentPermissionProfile.Plan),
            (options.WorkingDirectory, options.IsGeneral, options.Profile));
        Assert.Same(environment, options.EnvironmentVariables);
        Assert.Equal("S000001", registry.GetActive(Owner)!.Id);
    }

    [Fact]
    public async Task ModeTheAgentDoesNotSupportIsRefusedBeforeTheProcessStarts()
    {
        drivers.Configure = driver => driver.Capabilities = AgentDriverCapabilities.Codex with
        {
            Modes = [AgentPermissionProfile.Manual, AgentPermissionProfile.Auto]
        };
        await using var registry = CreateRegistry();

        var result = await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Codex, Repository,
            Profile: AgentPermissionProfile.Plan));

        Assert.False(result.Accepted);
        Assert.Null(result.Session);
        Assert.Equal("O modo plan não é suportado pelo Codex. Modos disponíveis: manual, auto.", result.Error);
        var driver = drivers.Created.Single();
        Assert.Empty(driver.Calls);
        Assert.True(driver.Disposed);
        Assert.Empty(registry.List(Owner));
        Assert.Null(registry.GetActive(Owner));

        var supported = await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Codex, Repository,
            Profile: AgentPermissionProfile.Auto));
        Assert.Equal(("S000001", AgentPermissionProfile.Auto), (supported.Session!.Id, supported.Session.Profile));
    }

    [Fact]
    public async Task GeneralSessionStartsInGeneralMode()
    {
        await using var registry = CreateRegistry();

        await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Claude,
            JobExecutionContext.General("/home/user/.dante/workspaces/general")));

        Assert.True(drivers.Created.Single().StartOptions!.IsGeneral);
    }

    [Fact]
    public async Task IdleSessionRunsEachMessageAsNewTurnOfTheSameSession()
    {
        await using var registry = CreateRegistry();
        await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Claude, Repository));
        var driver = drivers.Created.Single();

        var first = await registry.SubmitAsync(Owner, null, "primeira");
        Assert.Equal((SubmitOutcome.TurnStarted, "S000001", "T000001"), (first.Outcome, first.SessionId, first.TurnId));
        driver.Emit(new MessageCompletedEvent("m1", "ok"));
        driver.Emit(new TurnCompletedEvent(AgentTurnOutcome.Completed));
        await Eventually(() => registry.GetActive(Owner)!.State == AgentSessionState.Idle);

        var second = await registry.SubmitAsync(Owner, null, "segunda");
        Assert.Equal((SubmitOutcome.TurnStarted, "T000002"), (second.Outcome, second.TurnId));
        Assert.Equal(["start", "turn:primeira", "turn:segunda"], driver.Calls);
        Assert.Single(drivers.Created);

        var message = sink.Published.Select(published => published.Event).OfType<MessageCompletedEvent>().Single();
        Assert.Equal(("S000001", "T000001"), (message.SessionId, message.TurnId));
        var snapshot = registry.GetActive(Owner)!;
        Assert.Equal((AgentKind.Claude, Repository), (snapshot.Agent, snapshot.Context));
    }

    [Fact]
    public async Task MessagesDuringATurnAreQueuedAndRunInOrderWhenTheTurnEnds()
    {
        await using var registry = CreateRegistry();
        await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Codex, Repository));
        var driver = drivers.Created.Single();

        Assert.Equal(SubmitOutcome.TurnStarted, (await registry.SubmitAsync(Owner, null, "a")).Outcome);
        Assert.Equal(SubmitOutcome.Queued, (await registry.SubmitAsync(Owner, null, "b")).Outcome);
        Assert.Equal(SubmitOutcome.Queued, (await registry.SubmitAsync(Owner, "s000001", "c")).Outcome);
        Assert.Equal(2, registry.GetActive(Owner)!.QueuedCount);

        driver.Emit(new TurnCompletedEvent(AgentTurnOutcome.Completed));
        await Eventually(() => driver.Calls.Contains("turn:b"));
        driver.Emit(new TurnCompletedEvent(AgentTurnOutcome.Completed));
        await Eventually(() => driver.Calls.Contains("turn:c"));

        Assert.Equal(["start", "turn:a", "turn:b", "turn:c"], driver.Calls);
    }

    // #95: a message keeps its images through the queue, in order, and they reach the driver with it.
    [Fact]
    public async Task QueuedMessageKeepsItsImagesInOrder()
    {
        await using var registry = CreateRegistry();
        await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Claude, Repository));
        var driver = drivers.Created.Single();

        await registry.SubmitAsync(Owner, null, "a");
        Assert.Equal(SubmitOutcome.Queued, (await registry.SubmitAsync(Owner, null,
            new AgentInput("compare", [Image("A000002", Owner), Image("A000001", Owner)]))).Outcome);
        driver.Emit(new TurnCompletedEvent(AgentTurnOutcome.Completed));

        await Eventually(() => driver.Calls.Contains("turn:compare [A000002,A000001]"));
    }

    // #95: images the session cannot take are refused before reaching the driver, never sent as file names.
    [Fact]
    public async Task UnsupportedOrForeignAttachmentsAreRefusedBeforeTheDriver()
    {
        drivers.Configure = driver => driver.Capabilities = driver.Capabilities with { ImageInput = false };
        await using var registry = CreateRegistry();
        await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Codex, Repository));
        var driver = drivers.Created.Single();

        var unsupported = await registry.SubmitAsync(Owner, null, new AgentInput("veja", [Image("A000001", Owner)]));
        Assert.Equal(SubmitOutcome.Rejected, unsupported.Outcome);
        Assert.Equal("O Codex não recebe imagens nesta sessão; a mensagem não foi enviada.", unsupported.Error);
        var foreign = await registry.SubmitAsync(Owner, null, new AgentInput("veja", [Image("A000001", Intruder)]));
        Assert.Equal("Anexo de outro usuário.", foreign.Error);
        var audio = await registry.SubmitAsync(Owner, null,
            new AgentInput("ouça", [Image("A000001", Owner) with { Kind = AttachmentKind.Audio }]));
        Assert.StartsWith("Só imagens chegam ao agente", audio.Error);

        Assert.Equal(["start"], driver.Calls);
        Assert.Equal(AgentSessionState.Idle, registry.GetActive(Owner)!.State);
        Assert.Equal(SubmitOutcome.TurnStarted, (await registry.SubmitAsync(Owner, null, "só texto")).Outcome);
    }

    // #95: a session's attachment directory lives as long as the session; a leftover with its id is cleared first.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AttachmentsOfASessionAreDeletedWhenItEnds(bool fails)
    {
        var root = Directory.CreateTempSubdirectory("dante-session-attachments-").FullName;
        try
        {
            var store = new AttachmentStore(root);
            var leftover = Path.Combine(root, Owner.ToString(), "S000001", "A000009.png");
            Directory.CreateDirectory(Path.GetDirectoryName(leftover)!);
            File.WriteAllBytes(leftover, TestImages.Png(1, 1));
            await using var registry = new SessionRegistry(drivers, NullLogger<SessionRegistry>.Instance, sink,
                attachments: store);
            await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Claude, Repository));
            Assert.False(File.Exists(leftover));

            var image = Path.Combine(root, Owner.ToString(), "S000001", "A000001.png");
            Directory.CreateDirectory(Path.GetDirectoryName(image)!);
            File.WriteAllBytes(image, TestImages.Png(1, 1));
            if (fails) drivers.Created.Single().Crash(new AgentProtocolException("boom"));
            else await registry.CloseAsync(Owner, null);

            await Eventually(() => !Directory.Exists(Path.GetDirectoryName(image)));
        }
        finally { Directory.Delete(root, true); }
    }

    private static Attachment Image(string id, long owner) =>
        new(id, owner, AttachmentKind.Image, "image/png", $"/tmp/{id}.png", 10, 1, 1, null);

    [Fact]
    public async Task SteerGoesToTheDriverOfTheSession()
    {
        await using var registry = CreateRegistry();
        await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Codex, Repository));
        await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Claude, Repository));
        var (codex, claude) = (drivers.Created[0], drivers.Created[1]);
        await registry.SubmitAsync(Owner, "S000001", "tarefa");
        await registry.SubmitAsync(Owner, "S000002", "tarefa");

        var steered = await registry.SubmitAsync(Owner, "S000001", "mude o foco", MessageDelivery.Steer);
        var interrupted = await registry.SubmitAsync(Owner, "S000002", "mude o foco", MessageDelivery.Steer);

        Assert.Equal(SubmitOutcome.Steered, steered.Outcome);
        Assert.Equal("steer:mude o foco", codex.Calls[^1]);
        Assert.Equal(SubmitOutcome.SteerByInterrupt, interrupted.Outcome);
        Assert.Equal("interrupt", claude.Calls[^1]);
        claude.Emit(new TurnCompletedEvent(AgentTurnOutcome.Interrupted));
        await Eventually(() => claude.Calls.Contains("turn:mude o foco"));
    }

    [Fact]
    public async Task RejectedSteerKeepsTheSessionAlive()
    {
        await using var registry = CreateRegistry();
        drivers.Configure = driver => driver.SteerFailure = new InvalidOperationException("turno encerrado");
        await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Codex, Repository));
        await registry.SubmitAsync(Owner, null, "tarefa");

        var result = await registry.SubmitAsync(Owner, null, "desvio", MessageDelivery.Steer);

        Assert.Equal(SubmitOutcome.Rejected, result.Outcome);
        Assert.Contains("T000001", result.Error);
        Assert.Equal(AgentSessionState.Running, registry.GetActive(Owner)!.State);
    }

    [Fact]
    public async Task InterruptStopsTheTurnAndDiscardsTheQueue()
    {
        await using var registry = CreateRegistry();
        await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Codex, Repository));
        var driver = drivers.Created.Single();
        await registry.SubmitAsync(Owner, null, "a");
        await registry.SubmitAsync(Owner, null, "b");

        var result = await registry.InterruptAsync(Owner, null);

        Assert.True(result.Accepted);
        Assert.Equal(1, result.DiscardedMessages);
        Assert.Equal("interrupt", driver.Calls[^1]);
        driver.Emit(new TurnCompletedEvent(AgentTurnOutcome.Interrupted));
        await Eventually(() => registry.GetActive(Owner)!.State == AgentSessionState.Idle);
        Assert.DoesNotContain("turn:b", driver.Calls);
        Assert.False((await registry.InterruptAsync(Owner, null)).Accepted);
    }

    [Fact]
    public async Task FailedInterruptTerminatesTheSessionInsteadOfLeavingAnUninterruptibleTurn()
    {
        await using var registry = CreateRegistry();
        drivers.Configure = driver => driver.InterruptFailure = new AgentProcessInputClosedException("stdin fechado");
        await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Codex, Repository));
        await registry.SubmitAsync(Owner, null, "tarefa");

        var result = await registry.InterruptAsync(Owner, null);

        Assert.False(result.Accepted);
        Assert.Equal(AgentSessionState.Failed, registry.GetActive(Owner)!.State);
        Assert.True(drivers.Created.Single().Disposed);
    }

    [Fact]
    public async Task AnotherUserCannotSeeOrControlTheSession()
    {
        await using var registry = CreateRegistry();
        await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Codex, Repository));
        var driver = drivers.Created.Single();
        await registry.SubmitAsync(Owner, null, "tarefa");
        driver.Emit(new ApprovalRequestedEvent("upstream-7", AgentToolKind.Command, "git push"));
        await Eventually(() => registry.GetActive(Owner)!.PendingRequestIds.Count == 1);
        var requestId = registry.GetActive(Owner)!.PendingRequestIds.Single();
        var callsBefore = driver.Calls.Count;

        Assert.Null(registry.GetActive(Intruder));
        Assert.Empty(registry.List(Intruder));
        var submit = await registry.SubmitAsync(Intruder, "S000001", "outra coisa");
        Assert.Equal(SubmitOutcome.Rejected, submit.Outcome);
        Assert.Contains("não encontrada", submit.Error);
        Assert.Equal("Nenhuma sessão ativa. Inicie ou selecione uma sessão.",
            (await registry.SubmitAsync(Intruder, null, "outra coisa")).Error);
        Assert.False((await registry.InterruptAsync(Intruder, "S000001")).Accepted);
        Assert.False((await registry.CloseAsync(Intruder, "S000001")).Accepted);
        Assert.False(registry.Select(Intruder, "S000001").Accepted);
        var response = await registry.RespondAsync(Intruder, requestId,
            new AgentApprovalResponse(AgentApprovalDecision.ApproveOnce));
        Assert.False(response.Accepted);
        Assert.Contains("dono da sessão", response.Error);
        Assert.Equal(callsBefore, driver.Calls.Count);

        Assert.True((await registry.RespondAsync(Owner, requestId,
            new AgentApprovalResponse(AgentApprovalDecision.ApproveOnce))).Accepted);
        Assert.Equal("respond:upstream-7", driver.Calls[^1]);
        Assert.False((await registry.RespondAsync(Owner, requestId,
            new AgentApprovalResponse(AgentApprovalDecision.ApproveOnce))).Accepted);
    }

    [Fact]
    public async Task FailedResponseTerminatesTheSessionInsteadOfStrandingTheAgentRequest()
    {
        await using var registry = CreateRegistry();
        drivers.Configure = driver => driver.ResponseFailure = new AgentProcessInputClosedException("stdin fechado");
        await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Codex, Repository));
        await registry.SubmitAsync(Owner, null, "tarefa");
        var driver = drivers.Created.Single();
        driver.Emit(new ApprovalRequestedEvent("upstream-7", AgentToolKind.Command, "git push"));
        await Eventually(() => registry.GetActive(Owner)!.PendingRequestIds.Count == 1);
        var requestId = registry.GetActive(Owner)!.PendingRequestIds.Single();

        var result = await registry.RespondAsync(Owner, requestId,
            new AgentApprovalResponse(AgentApprovalDecision.ApproveOnce));

        Assert.False(result.Accepted);
        Assert.Equal(AgentSessionState.Failed, registry.GetActive(Owner)!.State);
        Assert.True(driver.Disposed);
    }

    [Fact]
    public async Task ResponsesRequireMatchingSessionTurnAndQuestionSet()
    {
        await using var registry = CreateRegistry();
        await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Codex, Repository));
        await registry.SubmitAsync(Owner, null, "tarefa");
        var driver = Assert.Single(drivers.Created);
        driver.Emit(new UserInputRequestedEvent("upstream-input", [
            new AgentQuestion("q1", "Primeira?", []),
            new AgentQuestion("q2", "Segunda?", [])]));
        await Eventually(() => registry.GetActive(Owner)!.PendingRequestIds.Count == 1);
        var requestId = registry.GetActive(Owner)!.PendingRequestIds.Single();
        var pending = registry.GetPendingRequest(Owner, requestId)!;

        Assert.Equal(("S000001", "T000001", false),
            (pending.SessionId, pending.TurnId, pending.IsApproval));
        Assert.Null(registry.GetPendingRequest(Intruder, requestId));
        var answers = new AgentInputResponse(new Dictionary<string, string>
        {
            ["q1"] = "sim", ["q2"] = "não"
        });
        Assert.False((await registry.RespondAsync(Owner, "S000002", pending.TurnId,
            requestId, answers)).Accepted);
        Assert.False((await registry.RespondAsync(Owner, pending.SessionId, "T000002",
            requestId, answers)).Accepted);
        Assert.False((await registry.RespondAsync(Owner, pending.SessionId, pending.TurnId,
            requestId, new AgentInputResponse(new Dictionary<string, string> { ["q1"] = "sim" }))).Accepted);
        Assert.False((await registry.RespondAsync(Intruder, pending.SessionId, pending.TurnId,
            requestId, answers)).Accepted);
        Assert.Empty(driver.Responses);

        Assert.True((await registry.RespondAsync(Owner, pending.SessionId, pending.TurnId,
            requestId, answers)).Accepted);
        var delivered = Assert.IsType<AgentInputResponse>(Assert.Single(driver.Responses).Response);
        Assert.Equal("não", delivered.Answers["q2"]);
        Assert.False((await registry.RespondAsync(Owner, pending.SessionId, pending.TurnId,
            requestId, answers)).Accepted);
    }

    [Fact]
    public async Task ExpiredApprovalIsDeniedUpstreamAndCannotBeAnsweredLate()
    {
        await using var registry = new SessionRegistry(drivers, NullLogger<SessionRegistry>.Instance,
            sink, requestTimeout: TimeSpan.FromMilliseconds(100));
        await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Codex, Repository));
        await registry.SubmitAsync(Owner, null, "tarefa");
        var driver = Assert.Single(drivers.Created);
        driver.Emit(new ApprovalRequestedEvent("upstream-approval", AgentToolKind.Command, "git push")
        { CanApproveForSession = true });
        // The request may expire before a poll sees it pending: its id comes from the published event.
        await Eventually(() => driver.Responses.Count == 1);
        var requestId = sink.Published.Select(item => item.Event).OfType<ApprovalRequestedEvent>().Single().RequestId;

        Assert.Empty(registry.GetActive(Owner)!.PendingRequestIds);
        Assert.Equal(AgentSessionState.Running, registry.GetActive(Owner)!.State);
        var denied = Assert.IsType<AgentApprovalResponse>(driver.Responses.Single().Response);
        Assert.Equal(AgentApprovalDecision.Deny, denied.Decision);
        Assert.False((await registry.RespondAsync(Owner, requestId,
            new AgentApprovalResponse(AgentApprovalDecision.ApproveOnce))).Accepted);
        Assert.Contains(sink.Published, item => item.Event is RequestExpiredEvent expired &&
            expired.RequestId == requestId && expired.TurnId == "T000001");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpirationRacingStopOrCloseDoesNotFailTheSession(bool close)
    {
        await using var registry = new SessionRegistry(drivers, NullLogger<SessionRegistry>.Instance,
            sink, requestTimeout: TimeSpan.FromMilliseconds(100));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        drivers.Configure = driver =>
        {
            driver.ResponseGate = gate;
            driver.RejectResponsesAfterInterrupt = true;
        };
        await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Codex, Repository));
        await registry.SubmitAsync(Owner, null, "tarefa");
        var driver = Assert.Single(drivers.Created);
        driver.Emit(new ApprovalRequestedEvent("upstream-approval", AgentToolKind.Command, "git push"));
        // The expiration already consumed the request locally and is delivering the denial upstream.
        await Eventually(() => driver.Calls.Contains("respond:upstream-approval"));

        var ending = close ? registry.CloseAsync(Owner, null) : registry.InterruptAsync(Owner, null);
        gate.SetResult();
        var result = await ending;
        await Eventually(() => sink.Published.Any(item => item.Event is RequestExpiredEvent or ErrorEvent));

        Assert.True(result.Accepted);
        Assert.Equal(close ? AgentSessionState.Closed : AgentSessionState.Running, registry.List(Owner).Single().State);
        // The denial reached the agent before the interrupt cleared its request.
        var calls = driver.Calls;
        Assert.True(calls.ToList().IndexOf("respond:upstream-approval") < calls.ToList().IndexOf("interrupt"));
        Assert.Single(driver.Responses);
        Assert.DoesNotContain(sink.Published, item => item.Event is ErrorEvent);
        Assert.Contains(sink.Published, item => item.Event is RequestExpiredEvent);
    }

    [Fact]
    public async Task SessionApprovalRequiresDriverSuggestion()
    {
        await using var registry = CreateRegistry();
        await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Claude, Repository));
        await registry.SubmitAsync(Owner, null, "tarefa");
        var driver = Assert.Single(drivers.Created);
        driver.Emit(new ApprovalRequestedEvent("claude-request", AgentToolKind.FileChange, "alterar arquivo"));
        await Eventually(() => registry.GetActive(Owner)!.PendingRequestIds.Count == 1);
        var requestId = registry.GetActive(Owner)!.PendingRequestIds.Single();

        Assert.False(registry.GetPendingRequest(Owner, requestId)!.CanApproveForSession);
        Assert.False((await registry.RespondAsync(Owner, requestId,
            new AgentApprovalResponse(AgentApprovalDecision.ApproveForSession))).Accepted);
        Assert.Empty(driver.Responses);
        Assert.True((await registry.RespondAsync(Owner, requestId,
            new AgentApprovalResponse(AgentApprovalDecision.ApproveOnce))).Accepted);
    }

    [Fact]
    public async Task ExpiredInputSendsEmptyAnswerAndRejectsLateText()
    {
        await using var registry = new SessionRegistry(drivers, NullLogger<SessionRegistry>.Instance,
            sink, requestTimeout: TimeSpan.FromMilliseconds(100));
        await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Claude, Repository));
        await registry.SubmitAsync(Owner, null, "tarefa");
        var driver = Assert.Single(drivers.Created);
        driver.Emit(new UserInputRequestedEvent("upstream-input", [new AgentQuestion("q1", "Nome?", [])]));
        await Eventually(() => driver.Responses.Count == 1);
        var requestId = sink.Published.Select(item => item.Event).OfType<UserInputRequestedEvent>().Single().RequestId;

        Assert.Equal("upstream-input", driver.Responses.Single().RequestId);
        Assert.Empty(Assert.IsType<AgentInputResponse>(driver.Responses.Single().Response).Answers);
        Assert.False((await registry.RespondAsync(Owner, requestId,
            new AgentInputResponse(new Dictionary<string, string> { ["q1"] = "Maria" }))).Accepted);
    }

    [Fact]
    public async Task ClosedSessionDoesNotReceiveMessages()
    {
        await using var registry = CreateRegistry();
        await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Claude, Repository));
        var driver = drivers.Created.Single();
        await registry.SubmitAsync(Owner, null, "tarefa");

        var closed = await registry.CloseAsync(Owner, null);

        Assert.True(closed.Accepted);
        Assert.Equal(AgentSessionState.Closed, closed.Session!.State);
        Assert.False(closed.Session.IsActive);
        Assert.Equal(["start", "turn:tarefa", "interrupt", "close"], driver.Calls);
        Assert.True(driver.Disposed);
        Assert.Null(registry.GetActive(Owner));
        var byId = await registry.SubmitAsync(Owner, "S000001", "depois");
        Assert.Equal(SubmitOutcome.Rejected, byId.Outcome);
        Assert.Contains("encerrada (Closed)", byId.Error);
        Assert.Equal(SubmitOutcome.Rejected, (await registry.SubmitAsync(Owner, null, "depois")).Outcome);
        Assert.False(registry.Select(Owner, "S000001").Accepted);
        Assert.False((await registry.CloseAsync(Owner, "S000001")).Accepted);
        Assert.Equal(AgentSessionState.Closed, registry.List(Owner).Single().State);
        Assert.DoesNotContain("turn:depois", driver.Calls);
    }

    [Fact]
    public async Task ProcessFailureEndsTheSessionExplicitly()
    {
        await using var registry = CreateRegistry();
        await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Codex, Repository));
        var driver = drivers.Created.Single();
        await registry.SubmitAsync(Owner, null, "tarefa");

        driver.Crash(new AgentProtocolException("O Codex encerrou inesperadamente."));
        await Eventually(() => registry.GetActive(Owner)!.State == AgentSessionState.Failed);

        await Eventually(() => driver.Disposed);
        var error = sink.Published.Select(published => published.Event).OfType<ErrorEvent>().Single();
        Assert.Equal(("S000001", "O Codex encerrou inesperadamente."), (error.SessionId, error.Message));
        // The failed session stays selected, so the next message is refused instead of going elsewhere.
        var result = await registry.SubmitAsync(Owner, null, "continua");
        Assert.Equal(SubmitOutcome.Rejected, result.Outcome);
        Assert.Contains("encerrada (Failed): O Codex encerrou inesperadamente.", result.Error);
    }

    [Fact]
    public async Task ProcessExitWithoutCloseFailsTheSession()
    {
        await using var registry = CreateRegistry();
        await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Claude, Repository));

        await drivers.Created.Single().CloseAsync();
        await Eventually(() => registry.GetActive(Owner)!.State == AgentSessionState.Failed);

        Assert.Equal("O processo do agente encerrou inesperadamente.", registry.GetActive(Owner)!.Error);
    }

    [Fact]
    public async Task StartFailureIsReportedAndNotSelected()
    {
        await using var registry = CreateRegistry();
        drivers.Configure = driver => driver.StartFailure = new AgentProtocolException("O Claude recusou o initialize.");

        var result = await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Claude, Repository));

        Assert.False(result.Accepted);
        Assert.Equal("O Claude recusou o initialize.", result.Error);
        Assert.Equal(AgentSessionState.Failed, result.Session!.State);
        Assert.Null(registry.GetActive(Owner));
        Assert.True(drivers.Created.Single().Disposed);
    }

    [Fact]
    public async Task TurnThatCannotReachTheAgentFailsTheSession()
    {
        await using var registry = CreateRegistry();
        drivers.Configure = driver => driver.TurnFailure = new AgentProcessInputClosedException("stdin fechado");
        await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Claude, Repository));

        var result = await registry.SubmitAsync(Owner, null, "tarefa");

        Assert.Equal(SubmitOutcome.Rejected, result.Outcome);
        Assert.Equal(AgentSessionState.Failed, registry.GetActive(Owner)!.State);
        Assert.True(drivers.Created.Single().Disposed);
    }

    [Fact]
    public async Task ActiveSessionChangesOnlyByExplicitSelection()
    {
        await using var registry = CreateRegistry();
        await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Codex, Repository));
        await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Claude,
            JobExecutionContext.General("/general")));
        Assert.Equal("S000002", registry.GetActive(Owner)!.Id);

        Assert.True(registry.Select(Owner, "S000001").Accepted);
        await registry.SubmitAsync(Owner, null, "tarefa");
        Assert.Equal("turn:tarefa", drivers.Created[0].Calls[^1]);
        Assert.DoesNotContain("turn:tarefa", drivers.Created[1].Calls);

        Assert.True(registry.Select(Owner, null).Accepted);
        Assert.Null(registry.GetActive(Owner));
        Assert.Equal(["S000002", "S000001"], registry.List(Owner).Select(session => session.Id));
        Assert.Equal((AgentKind.Codex, Repository), (registry.List(Owner)[1].Agent, registry.List(Owner)[1].Context));
    }

    [Fact]
    public async Task UnknownSessionMentionsThatSessionsDoNotSurviveRestart()
    {
        await using var registry = CreateRegistry();

        var result = await registry.SubmitAsync(Owner, "S000009", "olá");

        Assert.Equal(SubmitOutcome.Rejected, result.Outcome);
        Assert.Contains("não sobrevivem ao reinício do Worker", result.Error);
    }

    [Fact]
    public async Task ConcurrentMessagesOpenExactlyOneTurnAndRunEveryMessageOnce()
    {
        await using var registry = CreateRegistry();
        await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Codex, Repository));
        var driver = drivers.Created.Single();
        var texts = Enumerable.Range(1, 40).Select(index => $"m{index}").ToArray();

        var results = await Task.WhenAll(texts.Select(text => Task.Run(() => registry.SubmitAsync(Owner, null, text))));

        Assert.Single(results, result => result.Outcome == SubmitOutcome.TurnStarted);
        Assert.Equal(texts.Length - 1, results.Count(result => result.Outcome == SubmitOutcome.Queued));
        for (var turn = 1; turn <= texts.Length; turn++)
        {
            await Eventually(() => driver.Calls.Count(call => call.StartsWith("turn:")) == turn);
            driver.Emit(new TurnCompletedEvent(AgentTurnOutcome.Completed));
        }

        await Eventually(() => registry.GetActive(Owner)!.State == AgentSessionState.Idle);
        Assert.Equal(texts.Order(), driver.Calls.Where(call => call.StartsWith("turn:"))
            .Select(call => call["turn:".Length..]).Order());
    }

    [Fact]
    public async Task ConcurrentStartsKeepOneActiveSessionPerUser()
    {
        await using var registry = CreateRegistry();
        var users = Enumerable.Range(1, 20).Select(user => (long)user).ToArray();

        var results = await Task.WhenAll(users.Select(user => Task.Run(() =>
            registry.StartAsync(new SessionStartRequest(user, AgentKind.Claude, Repository)))));

        Assert.All(results, result => Assert.True(result.Accepted));
        Assert.Equal(users.Length, results.Select(result => result.Session!.Id).Distinct().Count());
        foreach (var user in users)
        {
            var active = registry.GetActive(user)!;
            Assert.Equal(user, active.OwnerUserId);
            Assert.Equal(active.Id, registry.List(user).Single().Id);
        }
    }

    [Fact]
    public async Task WorkerShutdownInvalidatesLiveSessions()
    {
        var registry = CreateRegistry();
        await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Codex, Repository));
        await registry.SubmitAsync(Owner, null, "tarefa");

        await registry.DisposeAsync();

        var session = registry.List(Owner).Single();
        Assert.Equal(AgentSessionState.Failed, session.State);
        Assert.Contains("não sobrevivem ao reinício do Worker", session.Error);
        Assert.True(drivers.Created.Single().Disposed);
        Assert.False((await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Codex, Repository))).Accepted);
        Assert.Single(drivers.Created);
    }

    [Fact]
    public async Task SynchronousHostDisposalStopsSessionDrivers()
    {
        var services = new ServiceCollection()
            .AddSingleton<IAgentSessionDriverFactory>(drivers)
            .AddLogging()
            .AddSingleton<SessionRegistry>()
            .BuildServiceProvider();
        var registry = services.GetRequiredService<SessionRegistry>();
        await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Codex, Repository));

        services.Dispose();

        Assert.True(drivers.Created.Single().Disposed);
        Assert.Equal(AgentSessionState.Failed, registry.List(Owner).Single().State);
    }

    [Fact]
    public async Task OnlyTheMostRecentEndedSessionsAreKept()
    {
        await using var registry = CreateRegistry();
        for (var index = 0; index < 22; index++)
        {
            await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Claude, Repository));
            await registry.CloseAsync(Owner, null);
        }

        await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Claude, Repository));

        var listed = registry.List(Owner);
        Assert.Equal(21, listed.Count);
        Assert.Equal("S000023", listed[0].Id);
        Assert.DoesNotContain(listed, session => session.Id is "S000001" or "S000002");
    }

    // #128: refused before the state machine sees it, idle or not, as a turn, a queued message or a steer.
    [Theory]
    [InlineData("/clear", MessageDelivery.Queue)]
    [InlineData("  /compact", MessageDelivery.Steer)]
    public async Task ClaudeCommandTextIsRefusedWhileIdleWithoutOpeningATurn(string text, MessageDelivery delivery)
    {
        await using var registry = CreateRegistry();
        await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Claude, Repository));
        var driver = drivers.Created.Single();

        var refused = await registry.SubmitAsync(Owner, null, text, delivery);

        Assert.Equal(SubmitOutcome.Rejected, refused.Outcome);
        Assert.Equal(AgentInput.CommandRefusal(AgentKind.Claude), refused.Error);
        Assert.Equal(["start"], driver.Calls);
        var session = registry.GetActive(Owner)!;
        Assert.Equal((AgentSessionState.Idle, null, 0), (session.State, session.ActiveTurnId, session.QueuedCount));
        var next = await registry.SubmitAsync(Owner, null, "explique a/b");
        Assert.Equal((SubmitOutcome.TurnStarted, "T000001"), (next.Outcome, next.TurnId));
    }

    [Fact]
    public async Task ClaudeCommandTextDuringATurnLeavesTheTurnQueueAndRequestsUntouched()
    {
        await using var registry = CreateRegistry();
        await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Claude, Repository));
        var driver = drivers.Created.Single();
        await registry.SubmitAsync(Owner, null, "tarefa");
        await registry.SubmitAsync(Owner, null, "depois");

        // A steer would interrupt the Claude turn (no native steer): the refusal comes first.
        var steer = await registry.SubmitAsync(Owner, null, "/clear", MessageDelivery.Steer);
        var queued = await registry.SubmitAsync(Owner, null, "/compact");
        driver.Emit(new ApprovalRequestedEvent("upstream", AgentToolKind.Command, "dotnet test"));
        await Eventually(() => registry.GetActive(Owner)!.PendingRequestIds.Count == 1);
        var waiting = await registry.SubmitAsync(Owner, null, "\t/clear", MessageDelivery.Steer);

        Assert.All([steer, queued, waiting], result => Assert.Equal(AgentInput.CommandRefusal(AgentKind.Claude), result.Error));
        Assert.Equal(["start", "turn:tarefa"], driver.Calls);
        var session = registry.GetActive(Owner)!;
        Assert.Equal(("T000001", 1, 1), (session.ActiveTurnId, session.QueuedCount, session.PendingRequestIds.Count));
        driver.Emit(new TurnCompletedEvent(AgentTurnOutcome.Completed));
        await Eventually(() => driver.Calls.Contains("turn:depois"));
    }

    [Fact]
    public async Task CodexKeepsTextStartingWithASlashAsAnOrdinaryMessage()
    {
        await using var registry = CreateRegistry();
        await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Codex, Repository));

        var turn = await registry.SubmitAsync(Owner, null, "/clear");

        Assert.Equal(SubmitOutcome.TurnStarted, turn.Outcome);
        Assert.Contains("turn:/clear", drivers.Created.Single().Calls);
    }

    [Fact]
    public async Task InputTheDriverRefusesEndsOnlyThatTurnAndKeepsTheSessionUsable()
    {
        // Defense in depth: a driver refusal never reaches the process, so it is not a broken session.
        drivers.Configure = driver => driver.TurnFailure = new AgentInputRejectedException("recusado pelo driver");
        await using var registry = CreateRegistry();
        await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Codex, Repository));
        var driver = drivers.Created.Single();

        var refused = await registry.SubmitAsync(Owner, null, "texto");

        Assert.Equal((SubmitOutcome.Rejected, "recusado pelo driver"), (refused.Outcome, refused.Error));
        Assert.Equal(AgentSessionState.Idle, registry.GetActive(Owner)!.State);
        driver.TurnFailure = null;
        Assert.Equal(SubmitOutcome.TurnStarted, (await registry.SubmitAsync(Owner, null, "outro")).Outcome);
    }

    // #120: a confirmed clear keeps the session, its process and its settings, replaces the upstream conversation and
    // drops the images of the previous one.
    [Fact]
    public async Task ClearGivesAnIdleSessionANewUpstreamConversationAndKeepsItsSettings()
    {
        var root = Directory.CreateTempSubdirectory("dante-clear-attachments-").FullName;
        try
        {
            await using var registry = new SessionRegistry(drivers, NullLogger<SessionRegistry>.Instance, sink,
                attachments: new AttachmentStore(root));
            var selection = new AgentModelSelection("opus", "high");
            await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Claude, Repository,
                Profile: AgentPermissionProfile.Plan, ModelSelection: selection));
            var driver = drivers.Created.Single();
            await registry.SubmitAsync(Owner, null, "primeira");
            driver.Emit(new TurnCompletedEvent(AgentTurnOutcome.Completed));
            await Eventually(() => registry.GetActive(Owner)!.State == AgentSessionState.Idle);
            var image = Path.Combine(root, Owner.ToString(), "S000001", "A000001.png");
            Directory.CreateDirectory(Path.GetDirectoryName(image)!);
            File.WriteAllBytes(image, TestImages.Png(1, 1));

            var result = await registry.ClearContextAsync(Owner, null);

            Assert.True(result.Accepted);
            var session = result.Session!;
            Assert.Equal(("S000001", "upstream-cleared-1", AgentSessionState.Idle),
                (session.Id, session.UpstreamSessionId, session.State));
            Assert.Equal((Repository, AgentPermissionProfile.Plan, selection, (AgentTurnOutcome?)null),
                (session.Context, session.Profile, session.ModelSelection, session.LastTurnOutcome));
            Assert.False(File.Exists(image));
            Assert.Equal(["start", "turn:primeira", "clear"], driver.Calls);
            Assert.Equal(SubmitOutcome.TurnStarted, (await registry.SubmitAsync(Owner, null, "nova")).Outcome);
            Assert.Single(drivers.Created);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ClearIsRefusedUnlessTheSessionIsIdle()
    {
        await using var registry = CreateRegistry();
        await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Codex, Repository));
        var driver = drivers.Created.Single();
        await registry.SubmitAsync(Owner, null, "tarefa");
        Assert.Contains("sessão ociosa", (await registry.ClearContextAsync(Owner, null)).Error);
        await registry.SubmitAsync(Owner, null, "depois");
        driver.Emit(new ApprovalRequestedEvent("upstream", AgentToolKind.Command, "dotnet test"));
        await Eventually(() => registry.GetActive(Owner)!.PendingRequestIds.Count == 1);
        Assert.False((await registry.ClearContextAsync(Owner, null)).Accepted);

        await registry.InterruptAsync(Owner, null);
        driver.Emit(new TurnCompletedEvent(AgentTurnOutcome.Interrupted));
        await Eventually(() => registry.GetActive(Owner)!.State == AgentSessionState.Idle);
        // A mode switch waiting for the next Codex turn is not dropped by a clear.
        await registry.ChangeModeAsync(Owner, null, AgentPermissionProfile.Plan);
        Assert.Contains("troca de modo aguardando", (await registry.ClearContextAsync(Owner, null)).Error);
        Assert.DoesNotContain("clear", driver.Calls);
    }

    [Fact]
    public async Task ClearTheAgentRefusesKeepsTheSessionAndAnUncertainOneEndsIt()
    {
        drivers.Configure = driver => driver.ClearFailure = new AgentContextUnchangedException("conversa anterior mantida");
        await using var registry = CreateRegistry();
        await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Claude, Repository));
        var refused = await registry.ClearContextAsync(Owner, null);
        Assert.Equal((false, "conversa anterior mantida", AgentSessionState.Idle),
            (refused.Accepted, refused.Error, refused.Session!.State));
        Assert.Equal(SubmitOutcome.TurnStarted, (await registry.SubmitAsync(Owner, null, "segue")).Outcome);

        drivers.Configure = driver => driver.ClearFailure = new TimeoutException();
        await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Codex, Repository));
        var uncertain = await registry.ClearContextAsync(Owner, null);
        Assert.Equal(AgentSessionState.Failed, uncertain.Session!.State);
        Assert.Contains("contexto incerto", uncertain.Error);
        Assert.True(drivers.Created[1].Disposed);
        Assert.Equal(SubmitOutcome.Rejected, (await registry.SubmitAsync(Owner, null, "x")).Outcome);
    }

    [Fact]
    public async Task ClearIsOnlyForTheOwnerAndIsSerializedWithMessages()
    {
        await using var registry = CreateRegistry();
        await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Codex, Repository));
        var driver = drivers.Created.Single();
        Assert.StartsWith("Sessão S000001 não encontrada", (await registry.ClearContextAsync(Intruder, "S000001")).Error);

        driver.ClearGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clear = registry.ClearContextAsync(Owner, null);
        await Eventually(() => driver.Calls.Contains("clear"));
        var message = registry.SubmitAsync(Owner, null, "nova conversa");
        await Task.Delay(100);
        Assert.False(message.IsCompleted);
        driver.ClearGate.SetResult();

        Assert.True((await clear).Accepted);
        Assert.Equal(SubmitOutcome.TurnStarted, (await message).Outcome);
        Assert.Equal(["start", "clear", "turn:nova conversa"], driver.Calls);
    }

    // #121: a compaction runs in the background; meanwhile the session refuses messages and other context operations,
    // and when it ends the same session (same upstream id) goes on with its settings.
    [Fact]
    public async Task CompactRunsInTheBackgroundAndRefusesEverythingElseMeanwhile()
    {
        await using var registry = CreateRegistry();
        await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Claude, Repository,
            Profile: AgentPermissionProfile.Plan));
        var driver = await CompletedTurnAsync(registry);
        driver.CompactGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var upstream = registry.GetActive(Owner)!.UpstreamSessionId;

        var compaction = await registry.CompactContextAsync(Owner, null);

        Assert.True(compaction.Started.Accepted);
        Assert.True(registry.GetActive(Owner)!.Compacting);
        Assert.Contains("está sendo compactada", (await registry.SubmitAsync(Owner, null, "outra")).Error);
        Assert.Contains("está sendo compactada", (await registry.ClearContextAsync(Owner, null)).Error);
        Assert.Contains("está sendo compactada", (await registry.ChangeModeAsync(Owner, null, AgentPermissionProfile.Manual)).Error);
        Assert.Contains("está sendo compactada", (await registry.CompactContextAsync(Owner, null)).Started.Error);
        driver.CompactGate.SetResult();

        var result = await compaction.Completion!;
        Assert.Equal((true, 5201, 592), (result.Compacted, result.PreTokens, result.PostTokens));
        var session = registry.GetActive(Owner)!;
        Assert.Equal((false, upstream, AgentPermissionProfile.Plan, AgentSessionState.Idle),
            (session.Compacting, session.UpstreamSessionId, session.Profile, session.State));
        Assert.Equal(["start", "turn:primeira", "compact"], driver.Calls);
        Assert.Equal(SubmitOutcome.TurnStarted, (await registry.SubmitAsync(Owner, null, "segue")).Outcome);
    }

    [Fact]
    public async Task MessageWaitingForLockIsRefusedWhenCompactionStartsFirst()
    {
        await using var registry = CreateRegistry();
        await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Claude, Repository,
            Profile: AgentPermissionProfile.Plan));
        var driver = await CompletedTurnAsync(registry);
        driver.ModeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        driver.CompactGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mode = registry.ChangeModeAsync(Owner, null, AgentPermissionProfile.Manual);
        await Eventually(() => driver.Calls.Contains("mode:manual"));
        var compact = registry.CompactContextAsync(Owner, null);
        var message = registry.SubmitAsync(Owner, null, "concorrente");
        Assert.False(compact.IsCompleted);
        Assert.False(message.IsCompleted);

        driver.ModeGate.SetResult();
        Assert.True((await mode).Accepted);
        var compaction = await compact;
        try
        {
            Assert.True(compaction.Started.Accepted);
            Assert.Equal(SubmitOutcome.Rejected, (await message).Outcome);
            Assert.True(registry.GetActive(Owner)!.Compacting);
            Assert.DoesNotContain("turn:concorrente", driver.Calls);
            Assert.Equal(AgentSessionState.Idle, registry.GetActive(Owner)!.State);
        }
        finally { driver.CompactGate.SetResult(); }
        Assert.True((await compaction.Completion!).Compacted);
        Assert.Equal(SubmitOutcome.TurnStarted, (await registry.SubmitAsync(Owner, null, "segue")).Outcome);
    }

    [Fact]
    public async Task NothingToCompactUntilTheConversationHasATurn()
    {
        await using var registry = CreateRegistry();
        await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Codex, Repository));
        Assert.StartsWith("Nada a compactar", (await registry.CompactContextAsync(Owner, null)).Started.Error);

        await CompletedTurnAsync(registry);
        await registry.ClearContextAsync(Owner, null);

        Assert.StartsWith("Nada a compactar", (await registry.CompactContextAsync(Owner, null)).Started.Error);
        Assert.DoesNotContain("compact", drivers.Created.Single().Calls);
        Assert.StartsWith("Sessão S000001 não encontrada",
            (await registry.CompactContextAsync(Intruder, "S000001")).Started.Error);
    }

    [Fact]
    public async Task StopCancelsTheCompactionAndKeepsTheSession()
    {
        await using var registry = CreateRegistry();
        await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Codex, Repository));
        var driver = await CompletedTurnAsync(registry);
        driver.CompactGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var compaction = await registry.CompactContextAsync(Owner, null);

        var stopped = await registry.InterruptAsync(Owner, null);

        Assert.True(stopped.Accepted);
        var result = await compaction.Completion!;
        Assert.Equal((false, "A compactação foi cancelada; a conversa anterior foi mantida."), (result.Compacted, result.Error));
        Assert.Equal(AgentSessionState.Idle, result.Session!.State);
        Assert.Contains("compact-interrupt", driver.Calls);
        Assert.Equal(SubmitOutcome.TurnStarted, (await registry.SubmitAsync(Owner, null, "segue")).Outcome);
    }

    [Fact]
    public async Task UncertainCompactionEndsTheSessionAndCloseEndsAPendingOne()
    {
        await using var registry = CreateRegistry();
        await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Claude, Repository));
        var driver = await CompletedTurnAsync(registry);
        driver.CompactFailure = new TimeoutException();
        var uncertain = await (await registry.CompactContextAsync(Owner, null)).Completion!;
        Assert.Equal(AgentSessionState.Failed, uncertain.Session!.State);
        Assert.Contains("contexto incerto", uncertain.Error);

        await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Codex, Repository));
        var second = await CompletedTurnAsync(registry, drivers.Created[1]);
        second.CompactGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = await registry.CompactContextAsync(Owner, null);
        Assert.True((await registry.CloseAsync(Owner, null)).Accepted);
        Assert.False((await pending.Completion!).Compacted);
    }

    private async Task<FakeSessionDriver> CompletedTurnAsync(SessionRegistry registry, FakeSessionDriver? driver = null)
    {
        driver ??= drivers.Created.Single();
        await registry.SubmitAsync(Owner, null, "primeira");
        driver.Emit(new TurnCompletedEvent(AgentTurnOutcome.Completed));
        await Eventually(() => registry.GetActive(Owner)!.State == AgentSessionState.Idle);
        return driver;
    }

    private SessionRegistry CreateRegistry() =>
        new(drivers, NullLogger<SessionRegistry>.Instance, sink);

    private static async Task Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "A condição esperada não ocorreu a tempo.");
            await Task.Delay(10);
        }
    }
}
