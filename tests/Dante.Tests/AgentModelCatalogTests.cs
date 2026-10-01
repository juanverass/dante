using System.Diagnostics;
using Dante.ProcessProbe;
using Dante.Worker.Agents;

namespace Dante.Tests;

// Model catalog (#77) against FakeClaude and FakeCodex (Dante.ProcessProbe): the models come from each CLI's own
// protocol, without a list kept in the D.A.N.T.E. and without starting a turn.
public sealed class AgentModelCatalogTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "dante-models-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ClaudeModelsComeFromTheInitializeResponse()
    {
        var launcher = new ProbeLauncher();
        var catalog = new AgentModelCatalog(launcher, Workspace());

        var models = await catalog.GetModelsAsync(AgentKind.Claude);

        // "default" is the CLI default itself: it marks the model it resolves to instead of being offered.
        Assert.Equal(["opus", "sonnet", "claude-legacy-test"], models.Select(model => model.Id));
        Assert.Equal(new AgentModelInfo("opus", "Opus Test", true, ["low", "medium", "high"], "claude-opus-test"),
            models[0] with { EffortLevels = models[0].EffortLevels.ToArray() is var levels ? levels : [] },
            new ModelComparer());
        Assert.False(models[1].IsDefault);
        Assert.Equal("claude-sonnet-test", models[1].ResolvedId);
        Assert.Null(models[2].ResolvedId);
        Assert.Empty(models[2].EffortLevels);

        // Same restrictions as a General Mode session, and the process does not outlive the query.
        var request = Assert.Single(launcher.Requests);
        Assert.True(request.IsGeneral);
        Assert.Equal(Workspace().Path, request.WorkingDirectory);
        Assert.Contains("--restricted", request.Arguments);
        Assert.Equal("plan", request.Arguments[request.Arguments.ToList().IndexOf("--permission-mode") + 1]);
        await WaitUntilExitedAsync(launcher.Started.Single().ProcessId);
    }

    [Fact]
    public async Task CodexModelsComeFromEveryPageOfModelList()
    {
        var launcher = new ProbeLauncher();
        var catalog = new AgentModelCatalog(launcher, Workspace());

        var models = await catalog.GetModelsAsync(AgentKind.Codex);

        // Hidden models are not offered.
        Assert.Equal(["fake-model", "fake-mini"], models.Select(model => model.Id));
        Assert.True(models[0].IsDefault);
        Assert.False(models[1].IsDefault);
        Assert.Equal(["low", "high"], models[1].EffortLevels);
        Assert.Equal("FAKE-MINI", models[1].DisplayName);
        var request = Assert.Single(launcher.Requests);
        Assert.Equal(["app-server", "--listen", "stdio://"], request.Arguments);
        Assert.True(request.IsGeneral);
        await WaitUntilExitedAsync(launcher.Started.Single().ProcessId);
    }

    [Fact]
    public async Task AnswersAreCachedPerAgentForAFewMinutes()
    {
        var launcher = new ProbeLauncher();
        var time = new ManualTime();
        var catalog = new AgentModelCatalog(launcher, Workspace(), time);

        var first = await catalog.GetModelsAsync(AgentKind.Codex);
        Assert.Same(first, await catalog.GetModelsAsync(AgentKind.Codex));
        await catalog.GetModelsAsync(AgentKind.Claude);
        Assert.Equal([AgentKind.Codex, AgentKind.Claude], launcher.Requests.Select(request => request.Agent));

        // A CLI upgrade is noticed without restarting the Worker.
        time.Now += TimeSpan.FromMinutes(11);
        Assert.NotSame(first, await catalog.GetModelsAsync(AgentKind.Codex));
        Assert.Equal(3, launcher.Requests.Count);
    }

    [Fact]
    public async Task CliThatCannotAnswerIsAnErrorAndIsNotCached()
    {
        var refused = new AgentModelCatalog(new ProbeLauncher("reject-init"), Workspace());
        var exception = await Assert.ThrowsAsync<AgentModelCatalogException>(() =>
            refused.GetModelsAsync(AgentKind.Claude));
        Assert.Equal("o Claude recusou o initialize", exception.Message);

        var missing = new MissingLauncher();
        var unavailable = new AgentModelCatalog(missing, Workspace());
        exception = await Assert.ThrowsAsync<AgentModelCatalogException>(() =>
            unavailable.GetModelsAsync(AgentKind.Codex));
        Assert.Equal("o Codex não pôde ser iniciado", exception.Message);
        await Assert.ThrowsAsync<AgentModelCatalogException>(() => unavailable.GetModelsAsync(AgentKind.Codex));
        Assert.Equal(2, missing.Attempts);
    }

    [Fact]
    public void FindMatchesIdsAndResolvedIdsAsTheCliSpellsThem()
    {
        IReadOnlyList<AgentModelInfo> models =
        [
            new("opus", "Opus", true, [], "claude-opus-5-5"),
            new("gpt-5.5", "GPT-5.5", false, [])
        ];

        Assert.Equal("opus", AgentModelCatalog.Find(models, "OPUS"));
        Assert.Equal("claude-opus-5-5", AgentModelCatalog.Find(models, "Claude-Opus-5-5"));
        Assert.Equal("gpt-5.5", AgentModelCatalog.Find(models, "gpt-5.5"));
        Assert.Null(AgentModelCatalog.Find(models, "default"));
        Assert.Null(AgentModelCatalog.Find(models, "sonnet"));
    }

    [Theory]
    [InlineData("opus", true)]
    [InlineData("claude-opus-5-5", true)]
    [InlineData("opus[1m]", true)]
    [InlineData("gpt-5.5", true)]
    [InlineData("anthropic/claude@2026:v1", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("-m", false)]
    [InlineData("opus sonnet", false)]
    [InlineData("opus;rm", false)]
    [InlineData("opus$(x)", false)]
    [InlineData("ópus", false)]
    public void ModelNamesAreCheckedForSyntaxOnly(string? name, bool valid)
    {
        Assert.Equal(valid, AgentModelSelection.IsValidName(name));
        Assert.False(AgentModelSelection.IsValidName(new string('a', 101)));
    }

    [Fact]
    public void LabelSaysWhenTheCliChoosesTheModel()
    {
        Assert.Equal("padrão da CLI", AgentModelSelection.CliDefault.ModelLabel);
        Assert.Equal("opus", new AgentModelSelection("opus").ModelLabel);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private GeneralWorkspace Workspace() => new(Path.Combine(root, "general"));

    private static async Task WaitUntilExitedAsync(int processId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (true)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited) return;
            }
            catch (ArgumentException)
            {
                return;
            }

            Assert.True(DateTime.UtcNow < deadline, $"Process {processId} is still running.");
            await Task.Delay(100);
        }
    }

    private sealed class ModelComparer : IEqualityComparer<AgentModelInfo>
    {
        public bool Equals(AgentModelInfo? x, AgentModelInfo? y) =>
            x is not null && y is not null && x with { EffortLevels = [] } == y with { EffortLevels = [] } &&
            x.EffortLevels.SequenceEqual(y.EffortLevels);

        public int GetHashCode(AgentModelInfo obj) => obj.Id.GetHashCode();
    }

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class MissingLauncher : IInteractiveAgentProcessLauncher
    {
        public int Attempts { get; private set; }

        public Task<InteractiveAgentProcess> StartAsync(AgentProcessRequest request,
            Func<string, string>? redactOutput = null, CancellationToken cancellationToken = default)
        {
            Attempts++;
            throw new AgentProcessStartException("not installed");
        }
    }

    // Runs the fake CLI that speaks the requested agent's protocol through the real interactive launcher.
    private sealed class ProbeLauncher(params string[] probeArguments) : IInteractiveAgentProcessLauncher
    {
        private static readonly string ProbeAssembly = typeof(ProbeMarker).Assembly.Location;
        private static readonly string RuntimeConfig =
            Path.Combine(AppContext.BaseDirectory, "Dante.Tests.runtimeconfig.json");
        private readonly InteractiveAgentProcessLauncher inner = new(new DotnetResolver());

        public List<AgentProcessRequest> Requests { get; } = [];

        public List<InteractiveAgentProcess> Started { get; } = [];

        public async Task<InteractiveAgentProcess> StartAsync(AgentProcessRequest request,
            Func<string, string>? redactOutput = null, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var fake = request.Agent == AgentKind.Codex ? "fake-codex" : "fake-claude";
            var process = await inner.StartAsync(request with
            {
                Arguments = ["exec", "--runtimeconfig", RuntimeConfig, ProbeAssembly, fake, .. probeArguments,
                    .. request.Arguments]
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
