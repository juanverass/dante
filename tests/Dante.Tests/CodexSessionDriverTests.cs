using System.Diagnostics;
using Dante.Application.Agentes;
using Dante.Application.Anexos;
using Dante.Infrastructure.Agentes;
using Dante.ProcessProbe;
using Dante.Worker.Jobs;
using Dante.Worker.Sessions;

namespace Dante.Tests;

// Drives CodexSessionDriver against FakeCodex (Dante.ProcessProbe): a real child process speaking the app-server
// JSON-RPC protocol, without the Codex CLI or any external service.
public sealed class CodexSessionDriverTests
{
    [Fact]
    public async Task ModeOverridesAreConfirmedInTheSameThreadAndProcessAtTheNextTurn()
    {
        var launcher = new ProbeLauncher();
        await using var driver = new CodexSessionDriver(launcher);
        var started = await driver.StartAsync(new AgentSessionStartOptions(AppContext.BaseDirectory,
            Profile: AgentPermissionProfile.Auto, ModelSelection: new AgentModelSelection("fake-model", "high")));
        await using var events = driver.ReadEventsAsync().GetAsyncEnumerator();
        foreach (var mode in new[] { AgentPermissionProfile.Manual, AgentPermissionProfile.Plan, AgentPermissionProfile.Auto })
        {
            await driver.ChangeModeAsync(mode);
            await driver.StartTurnAsync("config");
            var turn = await ReadTurnAsync(events);
            Assert.Equal(mode, turn.OfType<ModeAppliedEvent>().Single().Profile);
            var expected = mode == AgentPermissionProfile.Plan ? "on-request/read-only/plan/fake-model/true/user"
                : $"on-request/workspace-write/default/fake-model/true/{(mode == AgentPermissionProfile.Auto ? "auto_review" : "user")}";
            Assert.Equal(expected, turn.OfType<MessageCompletedEvent>().Single().Text);
            Assert.False(HasExited(started.ProcessId));
        }
        Assert.Single(launcher.Requests);
        Assert.Equal("thread-1", started.UpstreamSessionId);
    }

    [Fact]
    public async Task AnOlderServerWithoutSettingsConfirmationDoesNotApplyTheSnapshotMode()
    {
        await using var driver = new CodexSessionDriver(new ProbeLauncher("missing-mode-confirmation"));
        await driver.StartAsync(new AgentSessionStartOptions(AppContext.BaseDirectory));
        await driver.ChangeModeAsync(AgentPermissionProfile.Plan);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => driver.StartTurnAsync("config", timeout.Token));
        await using var events = driver.ReadEventsAsync().GetAsyncEnumerator();
        Assert.DoesNotContain(await ReadTurnAsync(events), item => item is ModeAppliedEvent);
    }

    [Theory]
    [InlineData("wrong-mode-reviewer")]
    [InlineData("wrong-mode-sandbox")]
    [InlineData("reject-mode")]
    public async Task UpstreamPolicyRefusalIsNeverReportedAsApplied(string scenario)
    {
        await using var driver = new CodexSessionDriver(new ProbeLauncher(scenario));
        await driver.StartAsync(new AgentSessionStartOptions(AppContext.BaseDirectory));
        await driver.ChangeModeAsync(AgentPermissionProfile.Auto);
        if (scenario == "reject-mode")
        {
            await Assert.ThrowsAsync<AgentModeRejectedException>(() => driver.StartTurnAsync("config"));
            await using var events = driver.ReadEventsAsync().GetAsyncEnumerator();
            await driver.StartTurnAsync("config");
            Assert.DoesNotContain(await ReadTurnAsync(events), item => item is ModeAppliedEvent);
        }
        else await Assert.ThrowsAsync<AgentProtocolException>(() => driver.StartTurnAsync("config"));
    }

    [Theory]
    [InlineData(AgentPermissionProfile.Plan, "effort:high/high")]
    [InlineData(AgentPermissionProfile.Manual, "effort:high/default")]
    public async Task EffortIsKeptAcrossTurns(AgentPermissionProfile profile, string expected)
    {
        var launcher = new ProbeLauncher();
        await using var driver = new CodexSessionDriver(launcher);
        await driver.StartAsync(new AgentSessionStartOptions(AppContext.BaseDirectory, IsGeneral: true,
            Profile: profile, ModelSelection: new AgentModelSelection("fake-model", "high")));
        await using var events = driver.ReadEventsAsync().GetAsyncEnumerator();
        await driver.StartTurnAsync("effort");
        var first = await ReadTurnAsync(events);
        await driver.StartTurnAsync("effort");
        var second = await ReadTurnAsync(events);
        Assert.Equal(expected, first.OfType<MessageCompletedEvent>().Single().Text);
        Assert.Equal(expected, second.OfType<MessageCompletedEvent>().Single().Text);
    }

    // #120: the clear is a new ephemeral thread in the same process with the model and policies of the session; the old
    // thread is unsubscribed and whatever it still sends is ignored.
    [Fact]
    public async Task ClearMovesToANewThreadWithTheSameSettingsAndIgnoresTheOldOne()
    {
        var launcher = new ProbeLauncher();
        await using var driver = new CodexSessionDriver(launcher);
        var started = await driver.StartAsync(new AgentSessionStartOptions(AppContext.BaseDirectory,
            Profile: AgentPermissionProfile.Plan, ModelSelection: new AgentModelSelection("fake-mini")));
        await using var events = driver.ReadEventsAsync().GetAsyncEnumerator();
        await driver.StartTurnAsync("pong");
        Assert.Equal("pong 1", (await ReadTurnAsync(events)).OfType<MessageCompletedEvent>().Single().Text);

        var cleared = await driver.ClearContextAsync();

        Assert.Equal(("thread-1", "thread-2"), (started.UpstreamSessionId, cleared.UpstreamSessionId));
        await driver.StartTurnAsync("thread");
        var turn = await ReadTurnAsync(events);
        Assert.Equal("thread:thread-2;turn:1;model:fake-mini;sandbox:read-only;unsubscribed:thread-1",
            turn.OfType<MessageCompletedEvent>().Single().Text);
        Assert.Single(launcher.Requests);
    }

    [Theory]
    [InlineData("reject-clear")]
    [InlineData("hang-clear")]
    public async Task ClearTheServerDoesNotConfirmKeepsThePreviousThread(string scenario)
    {
        await using var driver = new CodexSessionDriver(new ProbeLauncher(scenario), TimeSpan.FromSeconds(1));
        await driver.StartAsync(new AgentSessionStartOptions(AppContext.BaseDirectory));
        await using var events = driver.ReadEventsAsync().GetAsyncEnumerator();
        await driver.StartTurnAsync("pong");
        await ReadTurnAsync(events);

        await Assert.ThrowsAsync<AgentContextUnchangedException>(() => driver.ClearContextAsync());

        await driver.StartTurnAsync("thread");
        Assert.StartsWith("thread:thread-1;turn:2;",
            (await ReadTurnAsync(events)).OfType<MessageCompletedEvent>().Single().Text);
    }

    // #121: thread/compact/start runs as a turn of its own; the driver correlates it and nothing of it becomes a session
    // turn. The app-server reports no context size, so none is returned.
    [Fact]
    public async Task CompactIsItsOwnTurnAndKeepsTheThread()
    {
        await using var driver = new CodexSessionDriver(new ProbeLauncher());
        await driver.StartAsync(new AgentSessionStartOptions(AppContext.BaseDirectory));
        await using var events = driver.ReadEventsAsync().GetAsyncEnumerator();
        await driver.StartTurnAsync("pong");
        await ReadTurnAsync(events);

        var compacted = await driver.CompactContextAsync();

        Assert.Equal(new AgentContextCompacted(), compacted);
        await driver.StartTurnAsync("thread");
        var turn = await ReadTurnAsync(events);
        Assert.Single(turn.OfType<TurnStartedEvent>());
        Assert.StartsWith("thread:thread-1;turn:2;", turn.OfType<MessageCompletedEvent>().Single().Text);
    }

    [Theory]
    [InlineData("compact-no-ack")]
    [InlineData("compact-late-ack")]
    public async Task CompletedCompactionWithoutTimelyAckUsesTheConfirmedResult(string scenario)
    {
        await using var driver = new CodexSessionDriver(new ProbeLauncher(scenario),
            contextTimeout: TimeSpan.FromMilliseconds(100));
        await driver.StartAsync(new AgentSessionStartOptions(AppContext.BaseDirectory));
        await using var events = driver.ReadEventsAsync().GetAsyncEnumerator();
        await driver.StartTurnAsync("pong");
        await ReadTurnAsync(events);

        Assert.Equal(new AgentContextCompacted(), await driver.CompactContextAsync());
        await driver.StartTurnAsync("thread");
        var turn = await ReadTurnAsync(events);
        Assert.Single(turn.OfType<TurnStartedEvent>());
        Assert.StartsWith("thread:thread-1;turn:2;", turn.OfType<MessageCompletedEvent>().Single().Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartedCompactionWithoutAckIsInterruptedBeforeReusingTheThread(bool cancel)
    {
        await using var driver = new CodexSessionDriver(new ProbeLauncher("compact-no-ack", "compact-hang"),
            contextTimeout: cancel ? TimeSpan.FromSeconds(10) : TimeSpan.FromMilliseconds(100));
        await driver.StartAsync(new AgentSessionStartOptions(AppContext.BaseDirectory));
        await using var events = driver.ReadEventsAsync().GetAsyncEnumerator();
        await driver.StartTurnAsync("pong");
        await ReadTurnAsync(events);
        using var cancellation = new CancellationTokenSource(cancel ? TimeSpan.FromMilliseconds(300) : Timeout);

        var error = await Assert.ThrowsAsync<AgentContextUnchangedException>(() =>
            driver.CompactContextAsync(cancellation.Token));
        Assert.Contains(cancel ? "foi cancelada" : "passou do limite", error.Message);
        await driver.StartTurnAsync("thread");
        var turn = await ReadTurnAsync(events);
        Assert.Single(turn.OfType<TurnStartedEvent>());
        Assert.StartsWith("thread:thread-1;turn:2;", turn.OfType<MessageCompletedEvent>().Single().Text);
    }

    [Fact]
    public async Task CompactionWithoutAckOrNotificationsRemainsUncertain()
    {
        await using var driver = new CodexSessionDriver(new ProbeLauncher("compact-no-start"),
            contextTimeout: TimeSpan.FromMilliseconds(100));
        await driver.StartAsync(new AgentSessionStartOptions(AppContext.BaseDirectory));
        // No upstream confirmation: a timeout must reach the registry as uncertain, never as unchanged.
        await Assert.ThrowsAsync<TimeoutException>(() => driver.CompactContextAsync());
    }

    [Theory]
    [InlineData("compact-reject", "não iniciou a compactação")]
    [InlineData("compact-fail", "compaction failed upstream")]
    public async Task CompactTheServerDoesNotCompleteKeepsTheHistory(string scenario, string reason)
    {
        await using var driver = new CodexSessionDriver(new ProbeLauncher(scenario));
        await driver.StartAsync(new AgentSessionStartOptions(AppContext.BaseDirectory));
        await using var events = driver.ReadEventsAsync().GetAsyncEnumerator();
        await driver.StartTurnAsync("pong");
        await ReadTurnAsync(events);

        var exception = await Assert.ThrowsAsync<AgentContextUnchangedException>(() => driver.CompactContextAsync());

        Assert.Contains(reason, exception.Message);
        await driver.StartTurnAsync("thread");
        Assert.StartsWith("thread:thread-1;turn:2;",
            (await ReadTurnAsync(events)).OfType<MessageCompletedEvent>().Single().Text);
    }

    [Theory]
    [InlineData(true, "foi cancelada")]
    [InlineData(false, "passou do limite")]
    public async Task CancelledOrLateCompactIsInterruptedUpstream(bool cancel, string reason)
    {
        await using var driver = new CodexSessionDriver(new ProbeLauncher("compact-hang"),
            compactTimeout: cancel ? null : TimeSpan.FromSeconds(1));
        await driver.StartAsync(new AgentSessionStartOptions(AppContext.BaseDirectory));
        await using var events = driver.ReadEventsAsync().GetAsyncEnumerator();
        await driver.StartTurnAsync("pong");
        await ReadTurnAsync(events);
        using var cancellation = new CancellationTokenSource(cancel ? TimeSpan.FromMilliseconds(300) : Timeout);

        var exception = await Assert.ThrowsAsync<AgentContextUnchangedException>(() =>
            driver.CompactContextAsync(cancellation.Token));

        Assert.Contains(reason, exception.Message);
        await driver.StartTurnAsync("thread");
        Assert.StartsWith("thread:thread-1;turn:2;",
            (await ReadTurnAsync(events)).OfType<MessageCompletedEvent>().Single().Text);
    }

    private const long Owner = 42;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task StartsTheAppServerAndAnEphemeralThreadWithoutThePromptOnTheCommandLine()
    {
        var launcher = new ProbeLauncher();
        await using var driver = new CodexSessionDriver(launcher);

        var started = await driver.StartAsync(new AgentSessionStartOptions(AppContext.BaseDirectory));

        var request = Assert.Single(launcher.Requests);
        Assert.Equal(AgentKind.Codex, request.Agent);
        Assert.Equal(AppContext.BaseDirectory, request.WorkingDirectory);
        Assert.Equal(["app-server", "--listen", "stdio://"], request.Arguments);
        Assert.Equal("thread-1", started.UpstreamSessionId);
        Assert.Equal(AgentDriverCapabilities.Codex, driver.Capabilities);
        Assert.False(HasExited(started.ProcessId));
    }

    [Theory]
    [InlineData(AgentPermissionProfile.Manual, false, "on-request/workspace-write/default//true/user")]
    [InlineData(AgentPermissionProfile.Auto, false, "on-request/workspace-write/default//true/auto_review")]
    [InlineData(AgentPermissionProfile.Plan, true, "on-request/read-only/plan/fake-model/true/user")]
    public async Task MapsPermissionProfilesToApprovalPolicyAndSandbox(
        AgentPermissionProfile profile, bool general, string expected)
    {
        var launcher = new ProbeLauncher();
        await using var driver = new CodexSessionDriver(launcher);
        await driver.StartAsync(new AgentSessionStartOptions(AppContext.BaseDirectory, general, Profile: profile));
        await using var events = driver.ReadEventsAsync().GetAsyncEnumerator();

        await driver.StartTurnAsync("config");
        var turn = await ReadTurnAsync(events);

        Assert.Equal(general, Assert.Single(launcher.Requests).IsGeneral);
        // The fake echoes approvalPolicy/sandbox/collaboration mode/model/ephemeral as it received them.
        Assert.Equal(expected, turn.OfType<MessageCompletedEvent>().Single().Text);
    }

    [Fact]
    public async Task ChosenModelGoesToThreadStartAndTheReportedModelIsReturned()
    {
        var launcher = new ProbeLauncher();
        await using var driver = new CodexSessionDriver(launcher);
        var started = await driver.StartAsync(new AgentSessionStartOptions(AppContext.BaseDirectory,
            Profile: AgentPermissionProfile.Plan, ModelSelection: new AgentModelSelection("fake-mini")));
        await using var events = driver.ReadEventsAsync().GetAsyncEnumerator();

        await driver.StartTurnAsync("model");
        var first = await ReadTurnAsync(events);
        await driver.StartTurnAsync("config");
        var second = await ReadTurnAsync(events);

        // The model never goes on the command line; the plan collaboration mode reuses the thread's model.
        Assert.Equal(["app-server", "--listen", "stdio://"], Assert.Single(launcher.Requests).Arguments);
        Assert.Equal("fake-mini", started.Model);
        Assert.Equal("model:fake-mini", first.OfType<MessageCompletedEvent>().Single().Text);
        Assert.Equal("on-request/read-only/plan/fake-mini/true/user", second.OfType<MessageCompletedEvent>().Single().Text);

        // Without a selection thread/start carries no model and the CLI reports its own default.
        await using var cliDefault = new CodexSessionDriver(new ProbeLauncher());
        var defaultStarted = await cliDefault.StartAsync(new AgentSessionStartOptions(AppContext.BaseDirectory));
        await using var defaultEvents = cliDefault.ReadEventsAsync().GetAsyncEnumerator();
        await cliDefault.StartTurnAsync("model");
        Assert.Equal("model:default", (await ReadTurnAsync(defaultEvents)).OfType<MessageCompletedEvent>().Single().Text);
        Assert.Equal("fake-model", defaultStarted.Model);
    }

    [Fact]
    public async Task TwoTurnsUseTheSameThreadAndStreamDeltas()
    {
        await using var driver = new CodexSessionDriver(new ProbeLauncher());
        var started = await driver.StartAsync(new AgentSessionStartOptions(AppContext.BaseDirectory));
        await using var events = driver.ReadEventsAsync().GetAsyncEnumerator();

        await driver.StartTurnAsync("pong");
        var first = await ReadTurnAsync(events);
        await driver.StartTurnAsync("pong");
        var second = await ReadTurnAsync(events);

        Assert.IsType<TurnStartedEvent>(first[0]);
        Assert.Equal(["po", "ng"], first.OfType<MessageDeltaEvent>().Select(delta => delta.Text));
        Assert.All(first.OfType<MessageDeltaEvent>(), delta => Assert.Equal("msg-1", delta.ItemId));
        Assert.Equal(new MessageCompletedEvent("msg-1", "pong 1"), first.OfType<MessageCompletedEvent>().Single());
        Assert.Equal(AgentTurnOutcome.Completed, Assert.IsType<TurnCompletedEvent>(first[^1]).Outcome);
        // The fake rejects any other thread id and counts turns: the second turn reused thread-1 in the same process.
        Assert.Equal("pong 2", second.OfType<MessageCompletedEvent>().Single().Text);
        Assert.False(HasExited(started.ProcessId));
    }

    [Theory]
    [InlineData(AgentApprovalDecision.ApproveOnce, "accept", true)]
    [InlineData(AgentApprovalDecision.ApproveForSession, "acceptForSession", true)]
    [InlineData(AgentApprovalDecision.Deny, "decline", false)]
    public async Task CommandApprovalIsCorrelatedThroughTheSession(
        AgentApprovalDecision decision, string upstreamDecision, bool succeeded)
    {
        await using var driver = new CodexSessionDriver(new ProbeLauncher());
        var session = await StartSessionAsync(driver);
        await using var events = driver.ReadEventsAsync().GetAsyncEnumerator();

        var turn = session.Submit("command");
        await driver.StartTurnAsync("command");
        Assert.IsType<TurnStartedEvent>(session.Apply(await NextAsync(events)));
        var tool = Assert.IsType<ToolStartedEvent>(session.Apply(await NextAsync(events)));
        var approval = Assert.IsType<ApprovalRequestedEvent>(session.Apply(await NextAsync(events)));

        Assert.Equal(("cmd-1", AgentToolKind.Command, "dotnet test"), (tool.ItemId, tool.Kind, tool.Description));
        Assert.Equal((AgentToolKind.Command, "dotnet test", "rodar os testes"),
            (approval.Kind, approval.Action, approval.Reason));
        Assert.True(approval.CanApproveForSession);
        // JSON-RPC ids from the server are numbers; their text form is the upstream id.
        Assert.Equal("101", approval.UpstreamRequestId);
        Assert.Equal(turn.TurnId, approval.TurnId);
        Assert.Equal(AgentSessionState.WaitingForUser, session.State);

        var response = new AgentApprovalResponse(decision, "motivo");
        var resolution = session.Resolve(approval.RequestId, Owner, response);
        Assert.True(resolution.Accepted);
        await driver.RespondAsync(resolution.UpstreamRequestId!, response);
        var rest = await ReadTurnAsync(events);

        var completed = rest.OfType<ToolCompletedEvent>().Single();
        Assert.Equal(("cmd-1", succeeded), (completed.ItemId, completed.Succeeded));
        Assert.Equal($"decision:{upstreamDecision}", rest.OfType<MessageCompletedEvent>().Single().Text);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => driver.RespondAsync(resolution.UpstreamRequestId!, response));
    }

    [Fact]
    public async Task ApprovedFileChangeReportsPathsAndDiff()
    {
        await using var driver = new CodexSessionDriver(new ProbeLauncher());
        await driver.StartAsync(new AgentSessionStartOptions(AppContext.BaseDirectory));
        await using var events = driver.ReadEventsAsync().GetAsyncEnumerator();

        await driver.StartTurnAsync("edit");
        var started = await NextOfTypeAsync<ToolStartedEvent>(events);
        var approval = await NextOfTypeAsync<ApprovalRequestedEvent>(events);
        await driver.RespondAsync(approval.UpstreamRequestId,
            new AgentApprovalResponse(AgentApprovalDecision.ApproveOnce));
        var rest = await ReadTurnAsync(events);

        Assert.Equal((AgentToolKind.FileChange, "a.txt"), (started.Kind, started.Description));
        Assert.Equal((AgentToolKind.FileChange, "alterar arquivos", "criar a.txt"),
            (approval.Kind, approval.Action, approval.Reason));
        Assert.True(rest.OfType<ToolCompletedEvent>().Single().Succeeded);
        var change = rest.OfType<FileChangeEvent>().Single();
        Assert.Equal(["a.txt"], change.Paths);
        Assert.Equal("+hi", change.Diff);
    }

    [Fact]
    public async Task UserInputIsAnsweredByTheCodexQuestionId()
    {
        await using var driver = new CodexSessionDriver(new ProbeLauncher());
        await driver.StartAsync(new AgentSessionStartOptions(AppContext.BaseDirectory));
        await using var events = driver.ReadEventsAsync().GetAsyncEnumerator();

        await driver.StartTurnAsync("ask");
        var input = await NextOfTypeAsync<UserInputRequestedEvent>(events);
        var question = Assert.Single(input.Questions);
        Assert.Equal(("codename", "Qual o codinome?"), (question.Id, question.Text));
        Assert.Equal(["alpha", "beta"], question.Options);

        await Assert.ThrowsAsync<ArgumentException>(() => driver.RespondAsync(input.UpstreamRequestId,
            new AgentApprovalResponse(AgentApprovalDecision.ApproveOnce)));
        await Assert.ThrowsAsync<ArgumentException>(() => driver.RespondAsync(input.UpstreamRequestId,
            new AgentInputResponse(new Dictionary<string, string> { ["outra"] = "x" })));
        await driver.RespondAsync(input.UpstreamRequestId,
            new AgentInputResponse(new Dictionary<string, string> { ["codename"] = "beta" }));
        var rest = await ReadTurnAsync(events);

        Assert.Equal("""answers:{"codename":{"answers":["beta"]}}""",
            rest.OfType<MessageCompletedEvent>().Single().Text);
        Assert.Equal(AgentTurnOutcome.Completed, rest.OfType<TurnCompletedEvent>().Single().Outcome);
    }

    // #95: the text first, then one localImage per image by absolute path, in turn/start and turn/steer alike.
    [Fact]
    public async Task ImagesGoAsLocalImageItemsInTurnsAndSteers()
    {
        var first = Image("A000001", "/home/u/.dante/attachments/42/S000001/A000001.png");
        var second = Image("A000002", "/home/u/.dante/attachments/42/S000001/A000002.webp");
        await using var driver = new CodexSessionDriver(new ProbeLauncher());
        await driver.StartAsync(new AgentSessionStartOptions(AppContext.BaseDirectory));
        await using var events = driver.ReadEventsAsync().GetAsyncEnumerator();

        await driver.StartTurnAsync(new AgentInput("describe-input", [first, second]));
        Assert.Equal($"input:text:describe-input|localImage:{first.Path}|localImage:{second.Path}",
            (await ReadTurnAsync(events)).OfType<MessageCompletedEvent>().Single().Text);

        await driver.StartTurnAsync("slow");
        Assert.Equal("working", (await NextOfTypeAsync<MessageCompletedEvent>(events)).Text);
        await driver.SteerAsync(new AgentInput("olhe este print", [second]));
        Assert.Equal($"steered:olhe este print|localImage:{second.Path}",
            (await ReadTurnAsync(events)).OfType<MessageCompletedEvent>().Single().Text);

        static Attachment Image(string id, string path) =>
            new(id, Owner, AttachmentKind.Image, "image/png", path, 10, 1, 1, null);
    }

    // #97: a generated image arrives as an artifact event with the path the CLI reported; prose never becomes one.
    [Fact]
    public async Task GeneratedImageIsReportedByItsSavedPath()
    {
        await using var driver = new CodexSessionDriver(new ProbeLauncher());
        await driver.StartAsync(new AgentSessionStartOptions(AppContext.BaseDirectory));
        await using var events = driver.ReadEventsAsync().GetAsyncEnumerator();

        await driver.StartTurnAsync("generate-image /home/u/.codex/generated_images/t1/ig_1.png");
        var turn = await ReadTurnAsync(events);

        var artifact = Assert.Single(turn.OfType<ArtifactProducedEvent>());
        Assert.Equal("/home/u/.codex/generated_images/t1/ig_1.png", artifact.Path);
        Assert.Contains("O arquivo está em", turn.OfType<MessageCompletedEvent>().Single().Text);
    }

    [Fact]
    public async Task SteerReachesTheActiveTurn()
    {
        await using var driver = new CodexSessionDriver(new ProbeLauncher());
        await driver.StartAsync(new AgentSessionStartOptions(AppContext.BaseDirectory));
        await using var events = driver.ReadEventsAsync().GetAsyncEnumerator();

        await Assert.ThrowsAsync<InvalidOperationException>(() => driver.SteerAsync("sem turno"));
        await driver.StartTurnAsync("slow");
        // The turn has not ended yet: its output already arrived.
        Assert.Equal("working", (await NextOfTypeAsync<MessageCompletedEvent>(events)).Text);
        await driver.SteerAsync("mude de plano");
        var rest = await ReadTurnAsync(events);

        Assert.Equal("steered:mude de plano", rest.OfType<MessageCompletedEvent>().Single().Text);
        Assert.Equal(AgentTurnOutcome.Completed, rest.OfType<TurnCompletedEvent>().Single().Outcome);
    }

    [Fact]
    public async Task InterruptCancelsPendingApprovalAndKeepsTheProcess()
    {
        await using var driver = new CodexSessionDriver(new ProbeLauncher());
        var started = await driver.StartAsync(new AgentSessionStartOptions(AppContext.BaseDirectory));
        await using var events = driver.ReadEventsAsync().GetAsyncEnumerator();

        await driver.StartTurnAsync("command");
        var approval = await NextOfTypeAsync<ApprovalRequestedEvent>(events);
        await driver.InterruptTurnAsync();
        var rest = await ReadTurnAsync(events);
        await driver.StartTurnAsync("pong");
        var next = await ReadTurnAsync(events);

        // Codex was told to cancel the approval, so it does not wait for an answer that will never come.
        Assert.Equal("decision:cancel", rest.OfType<MessageCompletedEvent>().Single().Text);
        Assert.Equal(AgentTurnOutcome.Interrupted, rest.OfType<TurnCompletedEvent>().Single().Outcome);
        await Assert.ThrowsAsync<InvalidOperationException>(() => driver.RespondAsync(approval.UpstreamRequestId,
            new AgentApprovalResponse(AgentApprovalDecision.ApproveOnce)));
        Assert.Equal("pong 2", next.OfType<MessageCompletedEvent>().Single().Text);
        Assert.False(HasExited(started.ProcessId));
        // With no active turn, interrupt is a no-op instead of an error.
        await driver.InterruptTurnAsync();
    }

    [Fact]
    public async Task FailuresWarningsAndUnsupportedRequestsKeepTheSession()
    {
        await using var driver = new CodexSessionDriver(new ProbeLauncher());
        await driver.StartAsync(new AgentSessionStartOptions(AppContext.BaseDirectory));
        await using var events = driver.ReadEventsAsync().GetAsyncEnumerator();

        await driver.StartTurnAsync("fail");
        var failed = (await ReadTurnAsync(events)).OfType<TurnCompletedEvent>().Single();
        await driver.StartTurnAsync("warn");
        var warned = await ReadTurnAsync(events);
        await driver.StartTurnAsync("elicit");
        var elicited = await ReadTurnAsync(events);

        Assert.Equal(new TurnCompletedEvent(AgentTurnOutcome.Failed, "boom"), failed);
        Assert.Equal(["cuidado", "tentando de novo"], warned.OfType<WarningEvent>().Select(warning => warning.Message));
        Assert.StartsWith("rejected:mcpServer/elicitation/request não é suportado",
            elicited.OfType<MessageCompletedEvent>().Single().Text);
        Assert.Empty(elicited.OfType<ApprovalRequestedEvent>());
    }

    [Theory]
    [InlineData("garbage", "fora do protocolo")]
    [InlineData("crash", "código 5")]
    public async Task ProtocolErrorsEndTheEventStreamAndTheProcess(string scenario, string message)
    {
        await using var driver = new CodexSessionDriver(new ProbeLauncher());
        var started = await driver.StartAsync(new AgentSessionStartOptions(AppContext.BaseDirectory));
        await using var events = driver.ReadEventsAsync().GetAsyncEnumerator();

        // The fake answers turn/start before it misbehaves.
        await driver.StartTurnAsync(scenario);
        var error = await Assert.ThrowsAsync<AgentProtocolException>(async () =>
        {
            while (await events.MoveNextAsync().AsTask().WaitAsync(Timeout))
            {
            }
        });

        Assert.Contains(message, error.Message);
        await WaitUntilExitedAsync(started.ProcessId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => driver.StartTurnAsync("pong"));
    }

    [Fact]
    public async Task CloseEndsTheProcessAndTheEventStream()
    {
        await using var driver = new CodexSessionDriver(new ProbeLauncher());
        var started = await driver.StartAsync(new AgentSessionStartOptions(AppContext.BaseDirectory));

        await driver.CloseAsync().WaitAsync(Timeout);

        Assert.Empty(await ReadToEndAsync(driver));
        Assert.True(HasExited(started.ProcessId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => driver.StartTurnAsync("pong"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => driver.InterruptTurnAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => driver.StartAsync(new AgentSessionStartOptions(AppContext.BaseDirectory)));
    }

    [Theory]
    [InlineData("wrong-reviewer")]
    [InlineData("missing-reviewer")]
    public async Task AutoRequiresConfirmationOfTheAutomaticReviewer(string scenario)
    {
        var launcher = new ProbeLauncher(scenario);
        await using var driver = new CodexSessionDriver(launcher);

        var error = await Assert.ThrowsAsync<AgentProtocolException>(() => driver.StartAsync(
            new AgentSessionStartOptions(AppContext.BaseDirectory, Profile: AgentPermissionProfile.Auto)));

        Assert.Contains("não confirmou", error.Message);
        await WaitUntilExitedAsync(launcher.Started.Single().ProcessId);
    }

    [Fact]
    public async Task RejectedThreadFailsTheStartWithoutLeavingAProcess()
    {
        var launcher = new ProbeLauncher("reject-thread");
        await using var driver = new CodexSessionDriver(launcher);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => driver.StartAsync(new AgentSessionStartOptions(AppContext.BaseDirectory)));

        Assert.Contains("thread rejected", error.Message);
        await WaitUntilExitedAsync(launcher.Started.Single().ProcessId);
    }

    // #148: the last tokenUsage of the turn goes with its completion and is not carried to the next turn.
    [Fact]
    public async Task TokenUsageOfTheTurnIsReportedWithItsCompletion()
    {
        await using var driver = new CodexSessionDriver(new ProbeLauncher());
        await driver.StartAsync(new AgentSessionStartOptions(AppContext.BaseDirectory));
        await using var events = driver.ReadEventsAsync().GetAsyncEnumerator();
        await driver.StartTurnAsync("usage");
        Assert.Equal(new AgentTokenUsage(18000, 40, 17000),
            (await ReadTurnAsync(events)).OfType<TurnCompletedEvent>().Single().Usage);
        await driver.StartTurnAsync("pong");
        Assert.Null((await ReadTurnAsync(events)).OfType<TurnCompletedEvent>().Single().Usage);
    }

    private static readonly AgentToolServer PlanilhasServer = new("dante_planilhas", "/usr/bin/dante",
        ["--mcp-planilhas", "--origem", "telegram:42"], new Dictionary<string, string> { ["HOME"] = "/home/x" }, ["ler_intervalo"]);

    // #224: the D.A.N.T.E. tool servers reach the app-server as -c overrides; nothing secret goes on the command line.
    [Fact]
    public async Task ToolServersBecomeMcpConfigOverrides()
    {
        var launcher = new ProbeLauncher();
        await using var driver = new CodexSessionDriver(launcher);
        await driver.StartAsync(new AgentSessionStartOptions(AppContext.BaseDirectory, ToolServers: [PlanilhasServer]));
        Assert.Equal(["app-server", "--listen", "stdio://",
            "-c", "mcp_servers.dante_planilhas.command=\"/usr/bin/dante\"",
            "-c", "mcp_servers.dante_planilhas.args=[\"--mcp-planilhas\",\"--origem\",\"telegram:42\"]",
            "-c", "mcp_servers.dante_planilhas.env={HOME = \"/home/x\"}",
            "-c", "mcp_servers.dante_planilhas.startup_timeout_sec=30"], Assert.Single(launcher.Requests).Arguments);
    }

    // #224: an MCP tool call that is not read-only arrives as an elicitation (codex-cli 0.159.3) and becomes an
    // approval with the tool and its arguments; the answer goes back as accept or decline.
    [Theory]
    [InlineData(AgentApprovalDecision.ApproveOnce, "accept")]
    [InlineData(AgentApprovalDecision.Deny, "decline")]
    public async Task McpToolApprovalBecomesAnApprovalRequest(AgentApprovalDecision decision, string action)
    {
        await using var driver = new CodexSessionDriver(new ProbeLauncher());
        await driver.StartAsync(new AgentSessionStartOptions(AppContext.BaseDirectory));
        await using var events = driver.ReadEventsAsync().GetAsyncEnumerator();
        await driver.StartTurnAsync("mcp");
        var approval = await NextOfTypeAsync<ApprovalRequestedEvent>(events);
        Assert.Equal((AgentToolKind.Tool, "dante_planilhas.atualizar_celulas {\"planilha\":\"financas\"}", false),
            (approval.Kind, approval.Action, approval.CanApproveForSession));
        await driver.RespondAsync(approval.UpstreamRequestId, new AgentApprovalResponse(decision));
        Assert.Equal($"mcp:{action}", (await ReadTurnAsync(events)).OfType<MessageCompletedEvent>().Single().Text);
    }

    [Fact]
    public async Task PlanModeDeclinesMcpToolsThatWriteWithoutAsking()
    {
        await using var driver = new CodexSessionDriver(new ProbeLauncher());
        await driver.StartAsync(new AgentSessionStartOptions(AppContext.BaseDirectory, Profile: AgentPermissionProfile.Plan));
        await using var events = driver.ReadEventsAsync().GetAsyncEnumerator();
        await driver.StartTurnAsync("mcp");
        var turn = await ReadTurnAsync(events);
        Assert.Empty(turn.OfType<ApprovalRequestedEvent>());
        Assert.Equal("mcp:decline", turn.OfType<MessageCompletedEvent>().Single().Text);
    }

    private static async Task<AgentSession> StartSessionAsync(CodexSessionDriver driver)
    {
        var session = new AgentSession("S000001", AgentKind.Codex, Owner,
            JobExecutionContext.General(AppContext.BaseDirectory), driver.Capabilities, new SessionIdGenerator());
        session.MarkStarted(await driver.StartAsync(new AgentSessionStartOptions(AppContext.BaseDirectory)));
        return session;
    }

    private static async Task<AgentEvent> NextAsync(IAsyncEnumerator<AgentEvent> events)
    {
        Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(Timeout), "The event stream ended.");
        return events.Current;
    }

    private static async Task<T> NextOfTypeAsync<T>(IAsyncEnumerator<AgentEvent> events) where T : AgentEvent
    {
        while (true)
        {
            if (await NextAsync(events) is T match)
            {
                return match;
            }
        }
    }

    // Events up to and including the TurnCompletedEvent.
    private static async Task<List<AgentEvent>> ReadTurnAsync(IAsyncEnumerator<AgentEvent> events)
    {
        var turn = new List<AgentEvent>();
        do
        {
            turn.Add(await NextAsync(events));
        } while (turn[^1] is not TurnCompletedEvent);

        return turn;
    }

    private static async Task<List<AgentEvent>> ReadToEndAsync(CodexSessionDriver driver)
    {
        using var cts = new CancellationTokenSource(Timeout);
        var all = new List<AgentEvent>();
        await foreach (var agentEvent in driver.ReadEventsAsync(cts.Token))
        {
            all.Add(agentEvent);
        }

        return all;
    }

    private static bool HasExited(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static async Task WaitUntilExitedAsync(int processId)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!HasExited(processId))
        {
            Assert.True(DateTime.UtcNow < deadline, $"Process {processId} is still running.");
            await Task.Delay(100);
        }
    }

    // Runs FakeCodex through the real launcher: the driver's arguments reach the probe after the scenario flags.
    private sealed class ProbeLauncher(params string[] probeArguments) : IInteractiveAgentProcessLauncher
    {
        private static readonly string ProbeAssembly = typeof(ProbeMarker).Assembly.Location;
        private static readonly string RuntimeConfig =
            Path.Combine(AppContext.BaseDirectory, "Dante.Tests.runtimeconfig.json");
        private readonly InteractiveAgentProcessLauncher inner = new(new DotnetResolver());

        public List<AgentProcessRequest> Requests { get; } = [];

        public List<InteractiveAgentProcess> Started { get; } = [];

        public async Task<InteractiveAgentProcess> StartAsync(
            AgentProcessRequest request,
            Func<string, string>? redactOutput = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var process = await inner.StartAsync(request with
            {
                Arguments = ["exec", "--runtimeconfig", RuntimeConfig, ProbeAssembly, "fake-codex",
                    .. probeArguments, .. request.Arguments]
            }, redactOutput, cancellationToken);
            Started.Add(process);
            return process;
        }
    }

    private sealed class DotnetResolver : IAgentExecutableResolver
    {
        public string? Resolve(AgentKind agent)
        {
            var name = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
            return (Environment.GetEnvironmentVariable("PATH") ?? "")
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(directory => Path.Combine(directory, name))
                .FirstOrDefault(File.Exists)
                   ?? throw new FileNotFoundException("dotnet host must be on PATH to run process tests.");
        }
    }
}
