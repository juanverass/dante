using Dante.Worker.Agents;
using Dante.Worker.Jobs;
using Dante.Worker.Sessions;

namespace Dante.Tests;

public sealed class AgentSessionTests
{
    private const long Owner = 42;

    [Fact]
    public void QueuesMessagesUntilStartedThenRunsThemInOrder()
    {
        var session = CreateSession(AgentDriverCapabilities.Codex, started: false);

        Assert.Equal(SubmitOutcome.Queued, session.Submit("primeira").Outcome);
        Assert.Equal(SubmitOutcome.Rejected, session.Submit("desvio", MessageDelivery.Steer).Outcome);
        session.MarkStarted(new AgentSessionStarted("thread-1", 4242));

        Assert.Equal("thread-1", session.UpstreamSessionId);
        Assert.Equal(4242, session.ProcessId);
        Assert.True(session.TryStartQueued(out var turnId, out var text));
        Assert.Equal(("T000001", "primeira"), (turnId, text!.Text));
        Assert.Equal(AgentSessionState.Running, session.State);
        Assert.False(session.TryStartQueued(out _, out _));
    }

    [Fact]
    public void MessageAfterStartDoesNotOvertakeTheQueue()
    {
        var session = CreateSession(AgentDriverCapabilities.Codex, started: false);
        session.Submit("primeira");
        session.MarkStarted(new AgentSessionStarted("thread-1", 4242));

        var second = session.Submit("segunda");
        Assert.Equal(SubmitOutcome.Queued, second.Outcome);
        Assert.Null(session.ActiveTurnId);

        Assert.True(session.TryStartQueued(out var turnId, out var text));
        Assert.Equal(("T000001", "primeira"), (turnId, text!.Text));
        session.Apply(new TurnCompletedEvent(AgentTurnOutcome.Completed));
        Assert.True(session.TryStartQueued(out _, out text));
        Assert.Equal("segunda", text!.Text);
    }

    [Fact]
    public void MessageAfterTurnCompletionDoesNotOvertakeTheQueue()
    {
        var session = CreateSession(AgentDriverCapabilities.Codex);
        session.Submit("tarefa");
        session.Submit("fila");
        session.Apply(new TurnCompletedEvent(AgentTurnOutcome.Completed));

        Assert.Equal(SubmitOutcome.Queued, session.Submit("nova").Outcome);
        Assert.Equal(SubmitOutcome.Queued, session.Submit("urgente", MessageDelivery.Steer).Outcome);

        var order = new List<string>();
        while (session.TryStartQueued(out _, out var text))
        {
            order.Add(text!.Text);
            session.Apply(new TurnCompletedEvent(AgentTurnOutcome.Completed));
        }

        Assert.Equal(["urgente", "fila", "nova"], order);
    }

    [Fact]
    public void IdleMessageOpensTurnAndCompletionReturnsToIdle()
    {
        var session = CreateSession(AgentDriverCapabilities.Claude);

        var result = session.Submit("olá");
        Assert.Equal(SubmitOutcome.TurnStarted, result.Outcome);
        Assert.Equal(result.TurnId, session.ActiveTurnId);

        var delta = session.Apply(new MessageDeltaEvent("item-1", "oi"));
        Assert.Equal(("S000001", result.TurnId), (delta.SessionId, delta.TurnId));
        Assert.NotEqual(default, delta.TimestampUtc);

        session.Apply(new TurnCompletedEvent(AgentTurnOutcome.Completed));
        Assert.Equal(AgentSessionState.Idle, session.State);
        Assert.Null(session.ActiveTurnId);
        Assert.Equal(SubmitOutcome.TurnStarted, session.Submit("de novo").Outcome);
        Assert.Equal("T000002", session.ActiveTurnId);
    }

    [Fact]
    public void QueueIsTheDefaultWhileATurnRuns()
    {
        var session = CreateSession(AgentDriverCapabilities.Codex);
        session.Submit("tarefa");

        var queued = session.Submit("depois");
        Assert.Equal(SubmitOutcome.Queued, queued.Outcome);
        Assert.Equal(1, session.QueuedCount);

        session.Apply(new TurnCompletedEvent(AgentTurnOutcome.Completed));
        Assert.True(session.TryStartQueued(out _, out var text));
        Assert.Equal("depois", text!.Text);
    }

    [Fact]
    public void NativeSteerTargetsTheActiveTurn()
    {
        var session = CreateSession(AgentDriverCapabilities.Codex);
        var turn = session.Submit("tarefa").TurnId;

        var steered = session.Submit("mude de rumo", MessageDelivery.Steer);

        Assert.Equal((SubmitOutcome.Steered, turn), (steered.Outcome, steered.TurnId));
        Assert.Equal(0, session.QueuedCount);
        Assert.Equal(AgentSessionState.Running, session.State);
    }

    [Fact]
    public void SteerWithoutNativeSupportInterruptsAndRunsTheMessageFirst()
    {
        var session = CreateSession(AgentDriverCapabilities.Claude);
        session.Submit("tarefa");
        session.Submit("na fila");

        var steered = session.Submit("mude de rumo", MessageDelivery.Steer);

        Assert.Equal(SubmitOutcome.SteerByInterrupt, steered.Outcome);
        Assert.Equal(SubmitOutcome.Rejected, session.Submit("outro desvio", MessageDelivery.Steer).Outcome);
        session.Apply(new TurnCompletedEvent(AgentTurnOutcome.Interrupted));
        Assert.True(session.TryStartQueued(out _, out var first));
        Assert.Equal("mude de rumo", first!.Text);
        session.Apply(new TurnCompletedEvent(AgentTurnOutcome.Completed));
        Assert.True(session.TryStartQueued(out _, out var second));
        Assert.Equal("na fila", second!.Text);
    }

    [Fact]
    public void ApprovalWaitsForTheOwnerAndResumesTheTurn()
    {
        var session = CreateSession(AgentDriverCapabilities.Codex);
        session.Submit("tarefa");

        var requested = (ApprovalRequestedEvent)session.Apply(
            new ApprovalRequestedEvent("0", AgentToolKind.Command, "git push"));

        Assert.Equal("R000001", requested.RequestId);
        Assert.Equal(AgentSessionState.WaitingForUser, session.State);
        Assert.Equal(SubmitOutcome.Rejected, session.Submit("desvio", MessageDelivery.Steer).Outcome);
        Assert.Equal(SubmitOutcome.Queued, session.Submit("depois").Outcome);

        var intruder = session.Resolve("R000001", 7, new AgentApprovalResponse(AgentApprovalDecision.ApproveOnce));
        Assert.False(intruder.Accepted);

        var wrongKind = session.Resolve("R000001", Owner,
            new AgentInputResponse(new Dictionary<string, string> { ["q"] = "a" }));
        Assert.False(wrongKind.Accepted);

        var accepted = session.Resolve("r000001", Owner, new AgentApprovalResponse(AgentApprovalDecision.Deny));
        Assert.Equal((true, "0"), (accepted.Accepted, accepted.UpstreamRequestId));
        Assert.Equal(AgentSessionState.Running, session.State);
        Assert.False(session.Resolve("R000001", Owner, new AgentApprovalResponse(AgentApprovalDecision.Deny))
            .Accepted);
    }

    [Fact]
    public void UserInputRequestAcceptsOnlyAnswers()
    {
        var session = CreateSession(AgentDriverCapabilities.Claude);
        session.Submit("tarefa");
        var requested = (UserInputRequestedEvent)session.Apply(new UserInputRequestedEvent("req-1",
            [new AgentQuestion("cor", "Qual cor?", ["vermelho", "azul"])]));

        Assert.False(session.Resolve(requested.RequestId, Owner,
            new AgentApprovalResponse(AgentApprovalDecision.ApproveOnce)).Accepted);
        var accepted = session.Resolve(requested.RequestId, Owner,
            new AgentInputResponse(new Dictionary<string, string> { ["cor"] = "azul" }));
        Assert.Equal("req-1", accepted.UpstreamRequestId);
    }

    [Fact]
    public void LateAnswersAfterTurnEndAreRejected()
    {
        var session = CreateSession(AgentDriverCapabilities.Codex);
        session.Submit("tarefa");
        var requested = (ApprovalRequestedEvent)session.Apply(
            new ApprovalRequestedEvent("0", AgentToolKind.FileChange, "editar README.md"));

        session.Apply(new TurnCompletedEvent(AgentTurnOutcome.Failed, "processo falhou"));

        Assert.Equal(AgentSessionState.Idle, session.State);
        Assert.Empty(session.PendingRequestIds);
        Assert.False(session.Resolve(requested.RequestId, Owner,
            new AgentApprovalResponse(AgentApprovalDecision.ApproveOnce)).Accepted);
    }

    [Fact]
    public void RequestFromAnotherSessionIsRejected()
    {
        var ids = new SessionIdGenerator();
        var first = CreateSession(AgentDriverCapabilities.Codex, ids: ids);
        var second = CreateSession(AgentDriverCapabilities.Codex, ids: ids);
        first.Submit("a");
        second.Submit("b");
        var requested = (ApprovalRequestedEvent)first.Apply(
            new ApprovalRequestedEvent("0", AgentToolKind.Command, "dotnet test"));

        Assert.False(second.Resolve(requested.RequestId, Owner,
            new AgentApprovalResponse(AgentApprovalDecision.ApproveOnce)).Accepted);
        Assert.True(first.Resolve(requested.RequestId, Owner,
            new AgentApprovalResponse(AgentApprovalDecision.ApproveOnce)).Accepted);
    }

    [Fact]
    public void InterruptExpiresRequestsAndDiscardsQueue()
    {
        var session = CreateSession(AgentDriverCapabilities.Codex);
        session.Submit("tarefa");
        session.Submit("fila 1");
        session.Submit("fila 2");
        var requested = (ApprovalRequestedEvent)session.Apply(
            new ApprovalRequestedEvent("0", AgentToolKind.Command, "rm -rf build"));

        Assert.True(session.TryInterrupt(out var discarded));
        Assert.Equal(2, discarded);
        Assert.False(session.TryInterrupt(out _));
        Assert.Equal(AgentSessionState.Running, session.State);
        Assert.False(session.Resolve(requested.RequestId, Owner,
            new AgentApprovalResponse(AgentApprovalDecision.ApproveOnce)).Accepted);

        // A request that races with the interrupt is never answerable.
        var late = (ApprovalRequestedEvent)session.Apply(
            new ApprovalRequestedEvent("1", AgentToolKind.Command, "git push"));
        Assert.Empty(session.PendingRequestIds);
        Assert.False(session.Resolve(late.RequestId, Owner,
            new AgentApprovalResponse(AgentApprovalDecision.ApproveOnce)).Accepted);

        session.Apply(new TurnCompletedEvent(AgentTurnOutcome.Interrupted));
        Assert.Equal(AgentSessionState.Idle, session.State);
        Assert.False(session.TryStartQueued(out _, out _));
        Assert.False(session.TryInterrupt(out _));
    }

    [Fact]
    public void CloseRejectsNewInputAndEndsTerminal()
    {
        var session = CreateSession(AgentDriverCapabilities.Claude);
        session.Submit("tarefa");
        session.Submit("fila");

        Assert.True(session.TryClose());
        Assert.False(session.TryClose());
        Assert.Equal(AgentSessionState.Closing, session.State);
        Assert.Equal(0, session.QueuedCount);
        Assert.Equal(SubmitOutcome.Rejected, session.Submit("mais").Outcome);

        session.Apply(new TurnCompletedEvent(AgentTurnOutcome.Interrupted));
        Assert.Equal(AgentSessionState.Closing, session.State);
        session.MarkClosed();

        Assert.Equal(AgentSessionState.Closed, session.State);
        Assert.Throws<InvalidOperationException>(() => session.Apply(new WarningEvent("tarde")));
        Assert.Throws<InvalidOperationException>(() => session.MarkStarted(new AgentSessionStarted("x", 4242)));
        Assert.False(session.TryInterrupt(out _));
    }

    [Fact]
    public void ProcessFailureIsTerminal()
    {
        var session = CreateSession(AgentDriverCapabilities.Codex);
        session.Submit("tarefa");
        var requested = (ApprovalRequestedEvent)session.Apply(
            new ApprovalRequestedEvent("0", AgentToolKind.Command, "dotnet build"));

        session.MarkFailed("processo encerrado inesperadamente");

        Assert.Equal(AgentSessionState.Failed, session.State);
        Assert.Equal("processo encerrado inesperadamente", session.Error);
        Assert.Null(session.ActiveTurnId);
        Assert.Equal(SubmitOutcome.Rejected, session.Submit("mais").Outcome);
        Assert.False(session.Resolve(requested.RequestId, Owner,
            new AgentApprovalResponse(AgentApprovalDecision.ApproveOnce)).Accepted);
        Assert.False(session.TryClose());
        Assert.Throws<InvalidOperationException>(() => session.MarkClosed());
    }

    [Fact]
    public void TurnEventsOutsideATurnAreProtocolErrors()
    {
        var session = CreateSession(AgentDriverCapabilities.Codex);

        var warning = session.Apply(new WarningEvent("config"));
        Assert.Null(warning.TurnId);
        Assert.Throws<InvalidOperationException>(() => session.Apply(new MessageDeltaEvent("i", "x")));
        Assert.Throws<InvalidOperationException>(() =>
            session.Apply(new TurnCompletedEvent(AgentTurnOutcome.Completed)));
        Assert.Throws<InvalidOperationException>(() => session.MarkStarted(new AgentSessionStarted("again", 4242)));
    }

    private static AgentSession CreateSession(AgentDriverCapabilities capabilities, bool started = true,
        SessionIdGenerator? ids = null)
    {
        ids ??= new SessionIdGenerator();
        var session = new AgentSession(ids.NextSessionId(), AgentKind.Codex, Owner,
            JobExecutionContext.General("/general"), capabilities, ids);
        if (started)
        {
            session.MarkStarted(new AgentSessionStarted("upstream", 4242));
        }

        return session;
    }
}
