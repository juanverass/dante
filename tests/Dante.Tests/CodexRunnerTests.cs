using Dante.Worker.Agents;

namespace Dante.Tests;

public sealed class CodexRunnerTests
{
    [Fact]
    public async Task UsesFixedCommandAndPassesPromptAsOneArgument()
    {
        const string prompt = "--dangerously-bypass-approvals-and-sandbox; $(echo unsafe)";
        var result = Result(AgentProcessStatus.Succeeded, "CODEX OK");
        var executor = new RecordingExecutor(result);
        var runner = new CodexRunner(executor);
        using var cancellation = new CancellationTokenSource();

        var actual = await runner.RunAsync(prompt, AppContext.BaseDirectory, cancellation.Token);

        Assert.Same(result, actual);
        Assert.NotNull(executor.Request);
        Assert.Equal(AgentKind.Codex, executor.Request.Agent);
        Assert.Equal(AppContext.BaseDirectory, executor.Request.WorkingDirectory);
        Assert.Equal(
            ["exec", "--sandbox", "workspace-write", "--approve-for-me", "--", prompt],
            executor.Request.Arguments);
        Assert.Equal(cancellation.Token, executor.CancellationToken);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RejectsEmptyPromptBeforeStartingProcess(string prompt)
    {
        var executor = new RecordingExecutor(Result(AgentProcessStatus.Succeeded));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            new CodexRunner(executor).RunAsync(prompt, AppContext.BaseDirectory));

        Assert.Null(executor.Request);
    }

    [Fact]
    public async Task ReturnsAuthenticationFailureWithCliDiagnostics()
    {
        var failure = Result(AgentProcessStatus.Failed, error: "Authentication required. Run codex login.");
        var runner = new CodexRunner(new RecordingExecutor(failure));

        var actual = await runner.RunAsync("Say hello", AppContext.BaseDirectory);

        Assert.Same(failure, actual);
        Assert.Equal(AgentProcessStatus.Failed, actual.Status);
        Assert.Contains("codex login", actual.StandardError);
    }

    [Fact]
    public async Task ReturnsCancellationResultFromProcessExecutor()
    {
        var cancelled = Result(AgentProcessStatus.Cancelled);
        var runner = new CodexRunner(new RecordingExecutor(cancelled));

        var actual = await runner.RunAsync("Wait", AppContext.BaseDirectory);

        Assert.Same(cancelled, actual);
        Assert.Equal(AgentProcessStatus.Cancelled, actual.Status);
    }

    private static AgentProcessResult Result(
        AgentProcessStatus status,
        string output = "",
        string error = "") =>
        new(status, output, error, status == AgentProcessStatus.Succeeded ? 0 : 1,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private sealed class RecordingExecutor(AgentProcessResult result) : IAgentProcessExecutor
    {
        public AgentProcessRequest? Request { get; private set; }
        public CancellationToken CancellationToken { get; private set; }

        public bool IsAvailable(AgentKind agent) => true;

        public Task<AgentProcessResult> ExecuteAsync(
            AgentProcessRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            CancellationToken = cancellationToken;
            return Task.FromResult(result);
        }
    }
}
