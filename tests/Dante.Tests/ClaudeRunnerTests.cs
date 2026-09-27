using Dante.Worker.Agents;

namespace Dante.Tests;

public sealed class ClaudeRunnerTests
{
    [Fact]
    public async Task UsesPrintModeAndPassesPromptAsOneArgument()
    {
        const string prompt = "--permission-mode bypassPermissions; $(echo unsafe)";
        var result = Result(AgentProcessStatus.Succeeded, "CLAUDE OK");
        var executor = new RecordingExecutor(result);
        var runner = new ClaudeRunner(executor);
        using var cancellation = new CancellationTokenSource();

        var actual = await runner.RunAsync(prompt, AppContext.BaseDirectory, cancellation.Token);

        Assert.Same(result, actual);
        Assert.NotNull(executor.Request);
        Assert.Equal(AgentKind.Claude, executor.Request.Agent);
        Assert.Equal(AppContext.BaseDirectory, executor.Request.WorkingDirectory);
        Assert.Equal(
            ["--print", "--permission-mode", "auto", "--permission-prompts", "none", "--", prompt],
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
            new ClaudeRunner(executor).RunAsync(prompt, AppContext.BaseDirectory));

        Assert.Null(executor.Request);
    }

    [Fact]
    public async Task RejectsEmptyWorkingDirectoryBeforeStartingProcess()
    {
        var executor = new RecordingExecutor(Result(AgentProcessStatus.Succeeded));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            new ClaudeRunner(executor).RunAsync("Hello", " "));

        Assert.Null(executor.Request);
    }

    [Fact]
    public async Task PreservesAuthenticationFailureWrittenToStandardOutput()
    {
        const string diagnostic = "Authentication required. Run claude auth login.";
        var failure = Result(AgentProcessStatus.Failed, output: diagnostic);
        var actual = await new ClaudeRunner(new RecordingExecutor(failure))
            .RunAsync("Say hello", AppContext.BaseDirectory);

        Assert.Same(failure, actual);
        Assert.Equal(AgentProcessStatus.Failed, actual.Status);
        Assert.Equal(diagnostic, actual.StandardOutput);
    }

    [Fact]
    public async Task PreservesUnavailableCliError()
    {
        const string diagnostic = "Claude executable is unavailable. Install it and add its native binary to PATH.";
        var failure = Result(AgentProcessStatus.Failed, error: diagnostic);
        var actual = await new ClaudeRunner(new RecordingExecutor(failure))
            .RunAsync("Say hello", AppContext.BaseDirectory);

        Assert.Same(failure, actual);
        Assert.Equal(AgentProcessStatus.Failed, actual.Status);
        Assert.Equal(diagnostic, actual.ErrorMessage);
    }

    [Fact]
    public async Task ReturnsCancellationResultFromProcessExecutor()
    {
        var cancelled = Result(AgentProcessStatus.Cancelled);
        var actual = await new ClaudeRunner(new RecordingExecutor(cancelled))
            .RunAsync("Wait", AppContext.BaseDirectory);

        Assert.Same(cancelled, actual);
        Assert.Equal(AgentProcessStatus.Cancelled, actual.Status);
    }

    private static AgentProcessResult Result(
        AgentProcessStatus status,
        string output = "",
        string? error = null) =>
        new(status, output, error ?? "", status == AgentProcessStatus.Succeeded ? 0 : 1,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, error);

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
