using System.Diagnostics;
using Dante.ProcessProbe;
using Dante.Worker.Agents;
using Dante.Worker.Attachments;
using Dante.Worker.Jobs;
using Dante.Worker.Sessions;

namespace Dante.Tests;

// Drives ClaudeSessionDriver against FakeClaude (Dante.ProcessProbe): a real child process speaking stream-json,
// without the Claude CLI or any call to Anthropic.
public sealed class ClaudeSessionDriverTests
{
    [Fact]
    public async Task ModeChangesAreConfirmedOnTheSameProcessAcrossAllModes()
    {
        var launcher = new ProbeLauncher();
        await using var driver = new ClaudeSessionDriver(launcher);
        var started = await driver.StartAsync(new AgentSessionStartOptions(AppContext.BaseDirectory));
        await using var events = driver.ReadEventsAsync().GetAsyncEnumerator();
        foreach (var mode in new[] { AgentPermissionProfile.Auto, AgentPermissionProfile.Manual, AgentPermissionProfile.Plan, AgentPermissionProfile.Manual })
        {
            await driver.ChangeModeAsync(mode);
            await driver.StartTurnAsync("mode");
            Assert.Equal("mode:" + (mode == AgentPermissionProfile.Manual ? "default" : AgentSessionModes.Name(mode)),
                (await ReadTurnAsync(events)).OfType<MessageCompletedEvent>().Single().Text);
            Assert.False(HasExited(started.ProcessId));
        }
        Assert.Single(launcher.Requests);
    }

    [Theory]
    [InlineData("reject-mode", false)]
    [InlineData("missing-mode", true)]
    public async Task ModeRefusalOrMissingConfirmationIsNeverSuccess(string scenario, bool unconfirmed)
    {
        await using var driver = new ClaudeSessionDriver(new ProbeLauncher(scenario));
        await driver.StartAsync(new AgentSessionStartOptions(AppContext.BaseDirectory));
        if (unconfirmed) await Assert.ThrowsAsync<AgentModeUnconfirmedException>(() => driver.ChangeModeAsync(AgentPermissionProfile.Plan));
        else await Assert.ThrowsAsync<AgentProtocolException>(() => driver.ChangeModeAsync(AgentPermissionProfile.Plan));
    }

    [Fact]
    public async Task EffortIsKeptAcrossTurnsInPlanMode()
    {
        var launcher = new ProbeLauncher();
        await using var driver = new ClaudeSessionDriver(launcher);
        await driver.StartAsync(new AgentSessionStartOptions(AppContext.BaseDirectory, IsGeneral: true,
            Profile: AgentPermissionProfile.Plan, ModelSelection: new AgentModelSelection("opus", "high")));
        await using var events = driver.ReadEventsAsync().GetAsyncEnumerator();
        await driver.StartTurnAsync("effort");
        var first = await ReadTurnAsync(events);
        await driver.StartTurnAsync("effort");
        var second = await ReadTurnAsync(events);
        Assert.Equal("effort:high", first.OfType<MessageCompletedEvent>().Single().Text);
        Assert.Equal("effort:high", second.OfType<MessageCompletedEvent>().Single().Text);
    }

    // #95: images go as base64 image blocks, each after its label, in the order sent and before the user's text.
    [Fact]
    public async Task ImagesGoAsLabeledBase64BlocksBeforeTheText()
    {
        var directory = Directory.CreateTempSubdirectory("dante-claude-images-").FullName;
        try
        {
            var png = TestImages.Png(10, 10);
            var jpeg = TestImages.Jpeg(20, 10);
            File.WriteAllBytes(Path.Combine(directory, "A000001.png"), png);
            File.WriteAllBytes(Path.Combine(directory, "A000002.jpg"), jpeg);
            await using var driver = new ClaudeSessionDriver(new ProbeLauncher());
            await driver.StartAsync(new AgentSessionStartOptions(AppContext.BaseDirectory));
            await using var events = driver.ReadEventsAsync().GetAsyncEnumerator();

            await driver.StartTurnAsync(new AgentInput("compare os prints",
            [
                new Attachment("A000001", Owner, AttachmentKind.Image, "image/png", Path.Combine(directory, "A000001.png"),
                    png.Length, 10, 10, "antes.png"),
                new Attachment("A000002", Owner, AttachmentKind.Image, "image/jpeg", Path.Combine(directory, "A000002.jpg"),
                    jpeg.Length, 20, 10, null)
            ]));
            var turn = await ReadTurnAsync(events);

            Assert.Equal($"content:text:Imagem 1 (antes.png):|image:image/png:{png.Length}|text:Imagem 2:|" +
                $"image:image/jpeg:{jpeg.Length}|text:compare os prints", turn.OfType<MessageCompletedEvent>().Single().Text);
            // Text-only turns keep the plain string content.
            await driver.StartTurnAsync("pong");
            Assert.Contains("pong 2", (await ReadTurnAsync(events)).OfType<MessageCompletedEvent>().Select(item => item.Text));
        }
        finally { Directory.Delete(directory, true); }
    }

    // #128: Claude would run it as /clear or /compact. The driver refuses it before writing, so the fake counts the
    // next turn as the first one.
    [Theory]
    [InlineData("/clear", false)]
    [InlineData("  /compact", false)]
    [InlineData("\n/clear agora", false)]
    [InlineData("/clear", true)]
    public async Task UserTextStartingWithACommandNeverReachesTheProcess(string text, bool withImage)
    {
        await using var driver = new ClaudeSessionDriver(new ProbeLauncher());
        await driver.StartAsync(new AgentSessionStartOptions(AppContext.BaseDirectory));
        await using var events = driver.ReadEventsAsync().GetAsyncEnumerator();
        IReadOnlyList<Attachment> images = withImage
            ? [new Attachment("A000001", Owner, AttachmentKind.Image, "image/png", "/nao/existe.png", 10, 10, 10, null)]
            : [];

        var exception = await Assert.ThrowsAsync<AgentInputRejectedException>(() =>
            driver.StartTurnAsync(new AgentInput(text, images)));

        Assert.Contains("barra no início", exception.Message);
        await driver.StartTurnAsync("pong");
        var turn = await ReadTurnAsync(events);
        Assert.Single(turn.OfType<TurnStartedEvent>());
        Assert.Equal("pong 1", turn.OfType<MessageCompletedEvent>().Single().Text);
    }

    // #120: /clear is the driver's own message; it is confirmed by conversation_reset and the result, gives a new
    // session_id, emits no conversation event and keeps the process and the mode changed at runtime.
    [Fact]
    public async Task ClearIsConfirmedByTheResetAndStartsAnEmptyConversationInTheSameProcess()
    {
        var launcher = new ProbeLauncher();
        await using var driver = new ClaudeSessionDriver(launcher);
        var started = await driver.StartAsync(new AgentSessionStartOptions(AppContext.BaseDirectory));
        await using var events = driver.ReadEventsAsync().GetAsyncEnumerator();
        await driver.ChangeModeAsync(AgentPermissionProfile.Plan);
        await driver.StartTurnAsync("pong");
        Assert.Equal("pong 1", (await ReadTurnAsync(events)).OfType<MessageCompletedEvent>().Single().Text);

        var cleared = await driver.ClearContextAsync();

        Assert.NotEqual(started.UpstreamSessionId, cleared.UpstreamSessionId);
        await driver.StartTurnAsync("session");
        var turn = await ReadTurnAsync(events);
        Assert.IsType<TurnStartedEvent>(turn[0]);
        Assert.Equal($"session:{cleared.UpstreamSessionId};turn:1;mode:plan",
            turn.OfType<MessageCompletedEvent>().Single().Text);
        Assert.Single(launcher.Requests);
        Assert.False(HasExited(started.ProcessId));
    }

    [Fact]
    public async Task ClearWithoutTheResetKeepsThePreviousConversation()
    {
        await using var driver = new ClaudeSessionDriver(new ProbeLauncher("clear-unconfirmed"));
        var started = await driver.StartAsync(new AgentSessionStartOptions(AppContext.BaseDirectory));
        await using var events = driver.ReadEventsAsync().GetAsyncEnumerator();
        await driver.StartTurnAsync("pong");
        await ReadTurnAsync(events);

        await Assert.ThrowsAsync<AgentContextUnchangedException>(() => driver.ClearContextAsync());

        await driver.StartTurnAsync("session");
        Assert.Equal($"session:{started.UpstreamSessionId};turn:2;mode:manual",
            (await ReadTurnAsync(events)).OfType<MessageCompletedEvent>().Single().Text);
    }

    [Fact]
    public async Task ClearWithoutAnyAnswerIsUncertainNotUnchanged()
    {
        await using var driver = new ClaudeSessionDriver(new ProbeLauncher("clear-hang"), TimeSpan.FromSeconds(1));
        await driver.StartAsync(new AgentSessionStartOptions(AppContext.BaseDirectory));

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => driver.ClearContextAsync());

        Assert.IsNotType<AgentContextUnchangedException>(exception);
        Assert.IsType<TimeoutException>(exception);
    }

    private const long Owner = 42;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task StartsOneStructuredProcessWithoutThePromptOnTheCommandLine()
    {
        var launcher = new ProbeLauncher();
        await using var driver = new ClaudeSessionDriver(launcher);

        var started = await driver.StartAsync(new AgentSessionStartOptions(AppContext.BaseDirectory));

        var request = Assert.Single(launcher.Requests);
        Assert.Equal(AgentKind.Claude, request.Agent);
        Assert.Equal(AppContext.BaseDirectory, request.WorkingDirectory);
        Assert.False(request.IsGeneral);
        Assert.True(Guid.TryParse(started.UpstreamSessionId, out _));
        Assert.Equal(
        [
            "--print", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
            "--include-partial-messages", "--session-id", started.UpstreamSessionId,
            "--permission-mode", "manual", "--permission-prompt-tool", "stdio"
        ], request.Arguments);
        Assert.Equal(AgentDriverCapabilities.Claude, driver.Capabilities);
        Assert.False(HasExited(started.ProcessId));
    }

    [Theory]
    [InlineData(AgentPermissionProfile.Manual, "manual")]
    [InlineData(AgentPermissionProfile.Auto, "auto")]
    [InlineData(AgentPermissionProfile.Plan, "plan")]
    public async Task MapsPermissionProfilesAndKeepsGeneralModeRestricted(AgentPermissionProfile profile, string mode)
    {
        var launcher = new ProbeLauncher();
        await using var driver = new ClaudeSessionDriver(launcher);

        await driver.StartAsync(new AgentSessionStartOptions(AppContext.BaseDirectory, IsGeneral: true, Profile: profile));

        var request = Assert.Single(launcher.Requests);
        Assert.True(request.IsGeneral);
        var arguments = request.Arguments.ToList();
        Assert.Equal(mode, arguments[arguments.IndexOf("--permission-mode") + 1]);
        Assert.Equal("stdio", arguments[arguments.IndexOf("--permission-prompt-tool") + 1]);
        Assert.Equal(["--restricted", "--strict-mcp-config", "--tools", "Read,Write,Edit,AskUserQuestion"],
            arguments[^4..]);
    }

    [Fact]
    public async Task ChosenModelIsPassedOnceAndKeptForEveryTurn()
    {
        var launcher = new ProbeLauncher();
        await using var driver = new ClaudeSessionDriver(launcher);
        var started = await driver.StartAsync(new AgentSessionStartOptions(AppContext.BaseDirectory, IsGeneral: true,
            ModelSelection: new AgentModelSelection("opus")));
        await using var events = driver.ReadEventsAsync().GetAsyncEnumerator();

        await driver.StartTurnAsync("model");
        var first = await ReadTurnAsync(events);
        await driver.StartTurnAsync("model");
        var second = await ReadTurnAsync(events);

        Assert.Equal(["--model", "opus"], Assert.Single(launcher.Requests).Arguments.ToArray()[^2..]);
        Assert.Equal("model:opus", first.OfType<MessageCompletedEvent>().Single().Text);
        Assert.Equal("model:opus", second.OfType<MessageCompletedEvent>().Single().Text);
        // Claude says nothing about the model at start; without a selection no --model is passed.
        Assert.Null(started.Model);
        var defaultLauncher = new ProbeLauncher();
        await using var cliDefault = new ClaudeSessionDriver(defaultLauncher);
        await cliDefault.StartAsync(new AgentSessionStartOptions(AppContext.BaseDirectory,
            ModelSelection: AgentModelSelection.CliDefault));
        Assert.DoesNotContain("--model", Assert.Single(defaultLauncher.Requests).Arguments);
    }

    [Fact]
    public async Task TwoTurnsRunInTheSameProcessAndStreamDeltas()
    {
        await using var driver = new ClaudeSessionDriver(new ProbeLauncher());
        var started = await driver.StartAsync(new AgentSessionStartOptions(AppContext.BaseDirectory));
        await using var events = driver.ReadEventsAsync().GetAsyncEnumerator();

        await driver.StartTurnAsync("pong");
        var first = await ReadTurnAsync(events);
        await driver.StartTurnAsync("pong");
        var second = await ReadTurnAsync(events);

        Assert.IsType<TurnStartedEvent>(first[0]);
        Assert.Equal(["po", "ng"], first.OfType<MessageDeltaEvent>().Select(delta => delta.Text));
        Assert.All(first.OfType<MessageDeltaEvent>(), delta => Assert.Equal("msg_1", delta.ItemId));
        Assert.Equal(new MessageCompletedEvent("msg_1", "pong 1"), first.OfType<MessageCompletedEvent>().Single());
        Assert.Equal(AgentTurnOutcome.Completed, Assert.IsType<TurnCompletedEvent>(first[^1]).Outcome);
        // The fake counts turns per process: the second turn reached the same process and session.
        Assert.Equal("pong 2", second.OfType<MessageCompletedEvent>().Single().Text);
        Assert.False(HasExited(started.ProcessId));
    }

    [Fact]
    public async Task ApprovalIsCorrelatedThroughTheSessionAndAppliedForTheSession()
    {
        await using var driver = new ClaudeSessionDriver(new ProbeLauncher());
        var session = await StartSessionAsync(driver);
        await using var events = driver.ReadEventsAsync().GetAsyncEnumerator();

        var turn = session.Submit("write");
        await driver.StartTurnAsync("write");
        Assert.IsType<TurnStartedEvent>(session.Apply(await NextAsync(events)));
        var tool = Assert.IsType<ToolStartedEvent>(session.Apply(await NextAsync(events)));
        var approval = Assert.IsType<ApprovalRequestedEvent>(session.Apply(await NextAsync(events)));

        Assert.Equal(("toolu_write", AgentToolKind.FileChange, "Write notes.txt"), (tool.ItemId, tool.Kind, tool.Description));
        Assert.Equal((AgentToolKind.FileChange, "Write notes.txt"), (approval.Kind, approval.Action));
        Assert.True(approval.CanApproveForSession);
        Assert.Equal(turn.TurnId, approval.TurnId);
        Assert.Equal(AgentSessionState.WaitingForUser, session.State);

        var response = new AgentApprovalResponse(AgentApprovalDecision.ApproveForSession);
        var resolution = session.Resolve(approval.RequestId, Owner, response);
        Assert.True(resolution.Accepted);
        await driver.RespondAsync(resolution.UpstreamRequestId!, response);
        var rest = await ReadTurnAsync(events);

        var completed = rest.OfType<ToolCompletedEvent>().Single();
        Assert.Equal(("toolu_write", true), (completed.ItemId, completed.Succeeded));
        Assert.Equal(["notes.txt"], rest.OfType<FileChangeEvent>().Single().Paths);
        Assert.Equal("allow:session", rest.OfType<MessageCompletedEvent>().Single().Text);
        // Answered once: a second answer for the same upstream request is refused by the driver as well.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => driver.RespondAsync(resolution.UpstreamRequestId!, response));
    }

    [Fact]
    public async Task PersistentSuggestionsDoNotEnableSessionApproval()
    {
        await using var driver = new ClaudeSessionDriver(new ProbeLauncher());
        await driver.StartAsync(new AgentSessionStartOptions(AppContext.BaseDirectory));
        await using var events = driver.ReadEventsAsync().GetAsyncEnumerator();

        await driver.StartTurnAsync("write-persistent");
        var approval = await NextOfTypeAsync<ApprovalRequestedEvent>(events);
        Assert.False(approval.CanApproveForSession);

        // Even a session approval that bypasses the registry check carries no persistent permission to Claude.
        await driver.RespondAsync(approval.UpstreamRequestId,
            new AgentApprovalResponse(AgentApprovalDecision.ApproveForSession));
        var rest = await ReadTurnAsync(events);

        Assert.Equal("allow:once", rest.OfType<MessageCompletedEvent>().Single().Text);
    }

    [Fact]
    public async Task SessionApprovalForwardsOnlySessionScopedSuggestions()
    {
        await using var driver = new ClaudeSessionDriver(new ProbeLauncher());
        await driver.StartAsync(new AgentSessionStartOptions(AppContext.BaseDirectory));
        await using var events = driver.ReadEventsAsync().GetAsyncEnumerator();

        await driver.StartTurnAsync("write-mixed");
        var approval = await NextOfTypeAsync<ApprovalRequestedEvent>(events);
        Assert.True(approval.CanApproveForSession);

        await driver.RespondAsync(approval.UpstreamRequestId,
            new AgentApprovalResponse(AgentApprovalDecision.ApproveForSession));
        var rest = await ReadTurnAsync(events);

        // Two of the five suggestions are session-scoped; userSettings, projectSettings and localSettings never go back.
        Assert.Equal("allow:session,session", rest.OfType<MessageCompletedEvent>().Single().Text);
    }

    [Fact]
    public async Task DeniedApprovalCarriesTheReasonToClaude()
    {
        await using var driver = new ClaudeSessionDriver(new ProbeLauncher());
        await driver.StartAsync(new AgentSessionStartOptions(AppContext.BaseDirectory));
        await using var events = driver.ReadEventsAsync().GetAsyncEnumerator();

        await driver.StartTurnAsync("write");
        var approval = await NextOfTypeAsync<ApprovalRequestedEvent>(events);
        await driver.RespondAsync(approval.UpstreamRequestId,
            new AgentApprovalResponse(AgentApprovalDecision.Deny, "não altere arquivos"));
        var rest = await ReadTurnAsync(events);

        Assert.False(rest.OfType<ToolCompletedEvent>().Single().Succeeded);
        Assert.Empty(rest.OfType<FileChangeEvent>());
        Assert.Equal("deny:não altere arquivos", rest.OfType<MessageCompletedEvent>().Single().Text);
    }

    [Fact]
    public async Task UserInputQuestionsAreAnsweredByQuestionId()
    {
        await using var driver = new ClaudeSessionDriver(new ProbeLauncher());
        await driver.StartAsync(new AgentSessionStartOptions(AppContext.BaseDirectory));
        await using var events = driver.ReadEventsAsync().GetAsyncEnumerator();

        await driver.StartTurnAsync("ask");
        var input = await NextOfTypeAsync<UserInputRequestedEvent>(events);
        var question = Assert.Single(input.Questions);
        Assert.Equal(("q1", "Which color?"), (question.Id, question.Text));
        Assert.Equal(["red", "blue"], question.Options);

        await Assert.ThrowsAsync<ArgumentException>(() => driver.RespondAsync(input.UpstreamRequestId,
            new AgentApprovalResponse(AgentApprovalDecision.ApproveOnce)));
        await Assert.ThrowsAsync<ArgumentException>(() => driver.RespondAsync(input.UpstreamRequestId,
            new AgentInputResponse(new Dictionary<string, string> { ["q9"] = "blue" })));
        await driver.RespondAsync(input.UpstreamRequestId,
            new AgentInputResponse(new Dictionary<string, string> { ["q1"] = "blue" }));
        var rest = await ReadTurnAsync(events);

        Assert.Equal("""answers:{"Which color?":"blue"}""", rest.OfType<MessageCompletedEvent>().Single().Text);
        Assert.Equal(AgentTurnOutcome.Completed, rest.OfType<TurnCompletedEvent>().Single().Outcome);
    }

    [Fact]
    public async Task InterruptEndsTheTurnButKeepsTheSession()
    {
        await using var driver = new ClaudeSessionDriver(new ProbeLauncher());
        var started = await driver.StartAsync(new AgentSessionStartOptions(AppContext.BaseDirectory));
        await using var events = driver.ReadEventsAsync().GetAsyncEnumerator();

        await driver.StartTurnAsync("slow");
        // The turn has not ended yet: its output already arrived.
        Assert.Equal("working", (await NextOfTypeAsync<MessageCompletedEvent>(events)).Text);
        await driver.InterruptTurnAsync();
        var interrupted = await NextOfTypeAsync<TurnCompletedEvent>(events);
        await driver.StartTurnAsync("pong");
        var next = await ReadTurnAsync(events);

        Assert.Equal(AgentTurnOutcome.Interrupted, interrupted.Outcome);
        Assert.Equal(AgentTurnOutcome.Completed, next.OfType<TurnCompletedEvent>().Single().Outcome);
        Assert.False(HasExited(started.ProcessId));
        await Assert.ThrowsAsync<NotSupportedException>(() => driver.SteerAsync("desvio"));
    }

    [Fact]
    public async Task FailedTurnReportsTheErrorAndTheSessionContinues()
    {
        await using var driver = new ClaudeSessionDriver(new ProbeLauncher());
        await driver.StartAsync(new AgentSessionStartOptions(AppContext.BaseDirectory));
        await using var events = driver.ReadEventsAsync().GetAsyncEnumerator();

        await driver.StartTurnAsync("fail");
        var failed = (await ReadTurnAsync(events)).OfType<TurnCompletedEvent>().Single();
        await driver.StartTurnAsync("pong");
        var next = await ReadTurnAsync(events);

        Assert.Equal(new TurnCompletedEvent(AgentTurnOutcome.Failed, "boom"), failed);
        Assert.Equal(AgentTurnOutcome.Completed, next.OfType<TurnCompletedEvent>().Single().Outcome);
    }

    [Theory]
    [InlineData("garbage", "fora do protocolo")]
    [InlineData("crash", "código 5")]
    public async Task ProtocolErrorsEndTheEventStreamAndTheProcess(string scenario, string message)
    {
        await using var driver = new ClaudeSessionDriver(new ProbeLauncher());
        var started = await driver.StartAsync(new AgentSessionStartOptions(AppContext.BaseDirectory));

        await driver.StartTurnAsync(scenario);
        var error = await Assert.ThrowsAsync<AgentProtocolException>(() => ReadToEndAsync(driver));

        Assert.Contains(message, error.Message);
        await WaitUntilExitedAsync(started.ProcessId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => driver.StartTurnAsync("pong"));
    }

    [Fact]
    public async Task CloseEndsTheProcessAndTheEventStream()
    {
        await using var driver = new ClaudeSessionDriver(new ProbeLauncher());
        var started = await driver.StartAsync(new AgentSessionStartOptions(AppContext.BaseDirectory));

        await driver.CloseAsync().WaitAsync(Timeout);

        Assert.Empty(await ReadToEndAsync(driver));
        Assert.True(HasExited(started.ProcessId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => driver.StartTurnAsync("pong"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => driver.InterruptTurnAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => driver.StartAsync(new AgentSessionStartOptions(AppContext.BaseDirectory)));
    }

    [Fact]
    public async Task RejectedInitializeFailsTheStartWithoutLeavingAProcess()
    {
        var launcher = new ProbeLauncher("reject-init");
        await using var driver = new ClaudeSessionDriver(launcher);

        var error = await Assert.ThrowsAsync<AgentProtocolException>(
            () => driver.StartAsync(new AgentSessionStartOptions(AppContext.BaseDirectory)));

        Assert.Contains("initialize rejected", error.Message);
        await WaitUntilExitedAsync(launcher.Started.Single().ProcessId);
    }

    private static async Task<AgentSession> StartSessionAsync(ClaudeSessionDriver driver)
    {
        var session = new AgentSession("S000001", AgentKind.Claude, Owner,
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

    private static async Task<List<AgentEvent>> ReadToEndAsync(ClaudeSessionDriver driver)
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

    // Runs FakeClaude through the real launcher: the driver's arguments reach the probe after the scenario flags.
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
                Arguments = ["exec", "--runtimeconfig", RuntimeConfig, ProbeAssembly, "fake-claude",
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
