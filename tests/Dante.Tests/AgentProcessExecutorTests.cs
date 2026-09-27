using Dante.ProcessProbe;
using Dante.Worker.Agents;

namespace Dante.Tests;

public sealed class AgentProcessExecutorTests
{
    private static readonly string ProbeAssembly = typeof(ProbeMarker).Assembly.Location;
    private static readonly string RuntimeConfig = Path.Combine(
        AppContext.BaseDirectory, "Dante.Tests.runtimeconfig.json");

    [Fact]
    public async Task CapturesOutputAndPreservesArgumentsAsData()
    {
        var executor = CreateExecutor();
        const string input = "text with spaces; $(echo unsafe) & more";

        var result = await executor.ExecuteAsync(Request("echo", input));

        Assert.Equal(AgentProcessStatus.Succeeded, result.Status);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(input + Environment.NewLine, result.StandardOutput);
        Assert.Equal("probe stderr" + Environment.NewLine, result.StandardError);
        Assert.NotNull(result.StartedAtUtc);
        Assert.True(result.FinishedAtUtc >= result.StartedAtUtc);
    }

    [Fact]
    public async Task ReportsNonZeroExitAsFailure()
    {
        var result = await CreateExecutor().ExecuteAsync(Request("fail"));

        Assert.Equal(AgentProcessStatus.Failed, result.Status);
        Assert.Equal(7, result.ExitCode);
        Assert.Contains("probe failed", result.StandardError);
        Assert.NotNull(result.StartedAtUtc);
    }

    [Fact]
    public async Task UsesRequestedWorkingDirectory()
    {
        var result = await CreateExecutor().ExecuteAsync(Request("cwd"));

        Assert.Equal(AgentProcessStatus.Succeeded, result.Status);
        Assert.Equal(Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar),
            result.StandardOutput.Trim());
    }

    [Fact]
    public async Task CancellationStopsRunningProcess()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var result = await CreateExecutor().ExecuteAsync(Request("wait"), cts.Token);

        Assert.Equal(AgentProcessStatus.Cancelled, result.Status);
        Assert.Contains("ready", result.StandardOutput);
        Assert.NotNull(result.StartedAtUtc);
    }

    [Fact]
    public async Task MissingExecutableReturnsHandledFailure()
    {
        var executor = new AgentProcessExecutor(new FixedResolver(null));

        Assert.False(executor.IsAvailable(AgentKind.Codex));
        var result = await executor.ExecuteAsync(Request("echo", "unused"));

        Assert.Equal(AgentProcessStatus.Failed, result.Status);
        Assert.Null(result.ExitCode);
        Assert.Null(result.StartedAtUtc);
        Assert.Contains("unavailable", result.ErrorMessage);
    }

    [Fact]
    public async Task StartFailureReturnsHandledFailure()
    {
        var nonexistent = Path.Combine(AppContext.BaseDirectory, "missing-agent-executable.exe");
        var executor = new AgentProcessExecutor(new FixedResolver(nonexistent));

        var result = await executor.ExecuteAsync(Request("echo", "unused"));

        Assert.Equal(AgentProcessStatus.Failed, result.Status);
        Assert.Null(result.ExitCode);
        Assert.Null(result.StartedAtUtc);
        Assert.Contains("could not be started", result.ErrorMessage);
    }

    private static AgentProcessExecutor CreateExecutor()
    {
        var dotnet = FindDotnetHost();
        return new AgentProcessExecutor(new FixedResolver(dotnet));
    }

    private static AgentProcessRequest Request(params string[] arguments) =>
        new(AgentKind.Codex, AppContext.BaseDirectory,
            ["exec", "--runtimeconfig", RuntimeConfig, ProbeAssembly, .. arguments]);

    private static string FindDotnetHost()
    {
        var name = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("dotnet host must be on PATH to run process tests.");
    }

    private sealed record FixedResolver(string? Executable) : IAgentExecutableResolver
    {
        public string? Resolve(AgentKind agent) => Executable;
    }
}
