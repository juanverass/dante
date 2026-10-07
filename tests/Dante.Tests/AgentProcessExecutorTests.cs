using Dante.Application.Agentes;
using Dante.Infrastructure.Agentes;
using Dante.ProcessProbe;

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
    public async Task GeneralExecutionDoesNotInheritProjectEnvironment()
    {
        const string name = "DANTE_REPO_SECRET_TEST";
        var previous = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, "project-value");
        try
        {
            var executor = CreateExecutor();
            var ordinary = await executor.ExecuteAsync(Request("env", name));
            var general = await executor.ExecuteAsync(Request("env", name) with { IsGeneral = true });
            Assert.Equal("project-value", ordinary.StandardOutput.Trim());
            Assert.Equal("<unset>", general.StandardOutput.Trim());
        }
        finally { Environment.SetEnvironmentVariable(name, previous); }
    }

    [Theory]
    [InlineData("Google__ClientSecret")]
    [InlineData("DANTE_GOOGLE_KEY")]
    [InlineData("ConnectionStrings__Dante")]
    [InlineData("DANTE_BRAIN_CONNECTION")]
    [InlineData("PGPASSWORD")]
    public async Task DatabaseConnectionIsNeverInheritedByAgents(string name)
    {
        var previous = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, "dante-test-database-secret");
        try
        {
            foreach (var request in new[]
            {
                Request("env", name), Request("env", name) with { IsGeneral = true },
                Request("env", name) with
                { EnvironmentVariables = new Dictionary<string, string> { [name] = "override-secret" } }
            })
            {
                var result = await CreateExecutor().ExecuteAsync(request);
                Assert.Equal(AgentProcessStatus.Succeeded, result.Status);
                Assert.Equal("<unset>", result.StandardOutput.Trim());
            }
        }
        finally { Environment.SetEnvironmentVariable(name, previous); }
    }

    [Fact]
    public async Task RepositoryEnvironmentIsAppliedPerChildProcess()
    {
        const string name = "DANTE_TEST_REPO_VALUE";
        var previous = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, "host-value");
        try
        {
            var executor = CreateExecutor();
            var first = await executor.ExecuteAsync(Request("env", name) with
            {
                EnvironmentVariables = new Dictionary<string, string> { [name] = "first" }
            });
            var second = await executor.ExecuteAsync(Request("env", name) with
            {
                EnvironmentVariables = new Dictionary<string, string> { [name] = "second" }
            });
            var empty = await executor.ExecuteAsync(Request("env", name) with
            {
                EnvironmentVariables = new Dictionary<string, string>()
            });
            Assert.Equal("first", first.StandardOutput.Trim());
            Assert.Equal("second", second.StandardOutput.Trim());
            Assert.Equal("<unset>", empty.StandardOutput.Trim());
            Assert.Equal("host-value", Environment.GetEnvironmentVariable(name));
        }
        finally { Environment.SetEnvironmentVariable(name, previous); }
    }

    [Fact]
    public async Task AuthenticationVariableSurvivesGeneralAndRepositoryEnvironmentFiltering()
    {
        const string name = "OPENAI_API_KEY";
        var previous = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, "dante-test-auth-value");
        try
        {
            var executor = CreateExecutor();
            var general = await executor.ExecuteAsync(Request("env", name) with { IsGeneral = true });
            var repository = await executor.ExecuteAsync(Request("env", name) with
            {
                EnvironmentVariables = new Dictionary<string, string> { ["PROJECT_SETTING"] = "enabled" }
            });
            Assert.Equal("dante-test-auth-value", general.StandardOutput.Trim());
            Assert.Equal("dante-test-auth-value", repository.StandardOutput.Trim());
        }
        finally { Environment.SetEnvironmentVariable(name, previous); }
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
