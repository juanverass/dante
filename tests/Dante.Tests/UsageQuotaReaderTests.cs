using System.Diagnostics;
using System.Text.Json.Nodes;
using Dante.ProcessProbe;
using Dante.Worker.Agents;
using Dante.Worker.Usage;

namespace Dante.Tests;

// Quota query (#116, AD-31) against FakeCodex (Dante.ProcessProbe): the quotas come from account/rateLimits/read in a
// short-lived app-server, without a thread or a turn, and every missing metric stays unavailable.
public sealed class UsageQuotaReaderTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    private readonly string root = Path.Combine(Path.GetTempPath(), "dante-usage-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CodexSessionAndWeekComeFromRateLimitsReadInAShortLivedGeneralProcess()
    {
        var launcher = new ProbeLauncher();
        var reader = new UsageQuotaReader(launcher, Workspace(), new FixedTime(Now));

        var report = await reader.ReadAsync(AgentKind.Codex);

        Assert.Equal(AgentKind.Codex, report.Agent);
        Assert.Equal(Now, report.QueriedAt);
        Assert.Equal(new QuotaWindow(37, DateTimeOffset.FromUnixTimeSeconds(4102444800), TimeSpan.FromHours(5)),
            report.Session.Window);
        Assert.Equal(new QuotaWindow(62, DateTimeOffset.FromUnixTimeSeconds(4103049600), TimeSpan.FromDays(7)),
            report.Weekly.Window);
        Assert.Empty(report.Additional);
        var request = Assert.Single(launcher.Requests);
        Assert.Equal(["app-server", "--listen", "stdio://"], request.Arguments);
        Assert.True(request.IsGeneral);
        Assert.Equal(Workspace().Path, request.WorkingDirectory);
        await WaitUntilExitedAsync(launcher.Started.Single().ProcessId);
    }

    [Fact]
    public async Task OtherBucketsAreShownApartAndNeverReplaceTheGeneralQuota()
    {
        var report = await new UsageQuotaReader(new ProbeLauncher("multi-bucket"), Workspace())
            .ReadAsync(AgentKind.Codex);

        Assert.Equal(37, report.Session.Window!.UsedPercent);
        Assert.Equal(62, report.Weekly.Window!.UsedPercent);
        var other = Assert.Single(report.Additional);
        Assert.Equal(new QuotaWindow(42, DateTimeOffset.FromUnixTimeSeconds(4102444800), TimeSpan.FromHours(1),
            "Codex Other"), other);
    }

    [Fact]
    public async Task SingleBucketViewIsReadWhenTheMultiBucketViewIsAbsent()
    {
        var report = await new UsageQuotaReader(new ProbeLauncher("single-view"), Workspace())
            .ReadAsync(AgentKind.Codex);

        Assert.Equal(37, report.Session.Window!.UsedPercent);
        Assert.Equal(62, report.Weekly.Window!.UsedPercent);
    }

    [Fact]
    public async Task WindowsAreIdentifiedByDurationAndAmbiguityIsUnavailable()
    {
        // Two short windows (15 min and 1 h, as in the official example): neither is the proven 5h session window, and
        // the secondary one is not taken for the week.
        var report = await new UsageQuotaReader(new ProbeLauncher("short-windows"), Workspace())
            .ReadAsync(AgentKind.Codex);

        Assert.Null(report.Session.Window);
        Assert.Equal("o Codex não informou uma janela de sessão de 5h", report.Session.UnavailableReason);
        Assert.Null(report.Weekly.Window);
        Assert.Equal("o Codex não informou uma janela semanal", report.Weekly.UnavailableReason);
        Assert.Equal([25m, 42m], report.Additional.Select(window => window.UsedPercent));
        Assert.All(report.Additional, window => Assert.Equal("Codex", window.Label));
    }

    [Fact]
    public async Task PercentageOutsideTheContractIsAnUnsupportedAnswer()
    {
        var reader = new UsageQuotaReader(new ProbeLauncher("percent-out-of-range"), Workspace());

        var exception = await Assert.ThrowsAsync<UsageQueryException>(() => reader.ReadAsync(AgentKind.Codex));

        Assert.Equal(UsageQueryFailure.Unsupported, exception.Failure);
        Assert.Contains("versão da CLI", exception.Message);
    }

    [Theory]
    [InlineData("no-auth", UsageQueryFailure.NotAuthenticated, "codex login")]
    [InlineData("api-key", UsageQueryFailure.NoSubscription, "API key")]
    [InlineData("old-cli", UsageQueryFailure.Unsupported, "atualize a CLI")]
    [InlineData("upstream-error", UsageQueryFailure.Failed, "tente novamente")]
    public async Task FailuresAreClassifiedWithoutAccountData(string scenario, UsageQueryFailure failure, string hint)
    {
        var reader = new UsageQuotaReader(new ProbeLauncher(scenario), Workspace());

        var exception = await Assert.ThrowsAsync<UsageQueryException>(() => reader.ReadAsync(AgentKind.Codex));

        Assert.Equal(failure, exception.Failure);
        Assert.Contains(hint, exception.Message);
        Assert.DoesNotContain("fake@example.invalid", exception.Message);
        Assert.DoesNotContain("503", exception.Message);
    }

    [Fact]
    public async Task SlowAnswerTimesOutAndCancellationIsNotATimeout()
    {
        var launcher = new ProbeLauncher("hang-limits");
        var reader = new UsageQuotaReader(launcher, Workspace(), timeout: TimeSpan.FromSeconds(2));
        var exception = await Assert.ThrowsAsync<UsageQueryException>(() => reader.ReadAsync(AgentKind.Codex));
        Assert.Equal(UsageQueryFailure.Timeout, exception.Failure);
        await WaitUntilExitedAsync(launcher.Started.Single().ProcessId);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var patient = new UsageQuotaReader(new ProbeLauncher("hang-limits"), Workspace(), timeout: TimeSpan.FromMinutes(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            patient.ReadAsync(AgentKind.Codex, cancellation.Token));
    }

    [Fact]
    public async Task MissingCliIsAFailureAndClaudeIsNotQueriedYet()
    {
        var missing = new UsageQuotaReader(new MissingLauncher(), Workspace());
        var exception = await Assert.ThrowsAsync<UsageQueryException>(() => missing.ReadAsync(AgentKind.Codex));
        Assert.Equal(UsageQueryFailure.Failed, exception.Failure);

        var launcher = new ProbeLauncher();
        exception = await Assert.ThrowsAsync<UsageQueryException>(() =>
            new UsageQuotaReader(launcher, Workspace()).ReadAsync(AgentKind.Claude));
        Assert.Equal(UsageQueryFailure.Unsupported, exception.Failure);
        Assert.Empty(launcher.Requests);
    }

    [Fact]
    public void MissingFieldsAreUnavailableInsteadOfZero()
    {
        // No reset, no duration and a null secondary: nothing is invented.
        var report = UsageQuotaReader.ParseCodex(JsonNode.Parse("""
            {"rateLimits":{"limitId":"codex","primary":{"usedPercent":12,"windowDurationMins":300,"resetsAt":null},
             "secondary":null},"rateLimitsByLimitId":null}
            """)!.AsObject(), Now);
        Assert.Equal(new QuotaWindow(12, null, TimeSpan.FromHours(5)), report.Session.Window);
        Assert.Equal("o Codex não informou uma janela semanal", report.Weekly.UnavailableReason);

        report = UsageQuotaReader.ParseCodex(JsonNode.Parse("""
            {"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":5},"secondary":null}}}
            """)!.AsObject(), Now);
        Assert.Equal("o Codex não informou uma janela de sessão de 5h", report.Session.UnavailableReason);
        Assert.Null(report.Weekly.Window);
        Assert.Equal(new QuotaWindow(5, null, null, "Codex"), Assert.Single(report.Additional));

        // Several buckets and none is the general quota: no bucket is promoted to it.
        report = UsageQuotaReader.ParseCodex(JsonNode.Parse("""
            {"rateLimitsByLimitId":{"a":{"primary":{"usedPercent":1,"windowDurationMins":300}},
             "b":{"limitName":"B","primary":{"usedPercent":2,"windowDurationMins":10080}}}}
            """)!.AsObject(), Now);
        Assert.Equal("o Codex não informou a cota geral da conta", report.Session.UnavailableReason);
        Assert.Equal("o Codex não informou a cota geral da conta", report.Weekly.UnavailableReason);
        Assert.Equal(["a", "B"], report.Additional.Select(window => window.Label));
    }

    [Theory]
    [InlineData("""{"rateLimitsByLimitId":{"codex_other":{"limitName":"Other","primary":{"usedPercent":42,"windowDurationMins":300},"secondary":{"usedPercent":73,"windowDurationMins":10080}}}}""")]
    [InlineData("""{"rateLimits":{"limitId":"codex_other","limitName":"Other","primary":{"usedPercent":42,"windowDurationMins":300},"secondary":{"usedPercent":73,"windowDurationMins":10080}},"rateLimitsByLimitId":null}""")]
    public void ASpecificBucketAloneNeverReplacesTheGeneralQuota(string json)
    {
        var report = UsageQuotaReader.ParseCodex(JsonNode.Parse(json)!.AsObject(), Now);

        Assert.Equal("o Codex não informou a cota geral da conta", report.Session.UnavailableReason);
        Assert.Equal("o Codex não informou a cota geral da conta", report.Weekly.UnavailableReason);
        Assert.Equal([("Other", 42m), ("Other", 73m)],
            report.Additional.Select(window => (window.Label!, window.UsedPercent)));
    }

    [Fact]
    public void SingleShortWindowOfUnprovenDurationIsNotTheSession()
    {
        var report = UsageQuotaReader.ParseCodex(JsonNode.Parse("""
            {"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":42,"windowDurationMins":60}}}}
            """)!.AsObject(), Now);

        Assert.Equal("o Codex não informou uma janela de sessão de 5h", report.Session.UnavailableReason);
        Assert.Equal(new QuotaWindow(42, null, TimeSpan.FromHours(1), "Codex"), Assert.Single(report.Additional));

        // A single view without limitId is not assumed to be the general quota either.
        report = UsageQuotaReader.ParseCodex(JsonNode.Parse("""
            {"rateLimits":{"primary":{"usedPercent":7,"windowDurationMins":300}}}
            """)!.AsObject(), Now);
        Assert.Equal("o Codex não informou a cota geral da conta", report.Session.UnavailableReason);
        Assert.Equal("limite sem identificação", Assert.Single(report.Additional).Label);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void PercentageOutsideZeroToHundredIsRefusedNotClamped(int percent) =>
        Assert.Throws<FormatException>(() => UsageQuotaReader.ParseCodex(JsonNode.Parse(
            """{"rateLimitsByLimitId":{"codex_other":{"primary":{"usedPercent":""" + percent +
            ""","windowDurationMins":60}}}}""")!.AsObject(), Now));

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

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class MissingLauncher : IInteractiveAgentProcessLauncher
    {
        public Task<InteractiveAgentProcess> StartAsync(AgentProcessRequest request,
            Func<string, string>? redactOutput = null, CancellationToken cancellationToken = default) =>
            throw new AgentProcessStartException("not installed");
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
