using Dante.Worker.Agents;
using Dante.Worker.Telegram;
using Dante.Worker.Usage;
using Xunit.Abstractions;

namespace Dante.Tests;

// Opt-in evidence (#116): the real CLI's quotas through the production reader, in the General workspace. No turn runs.
// DANTE_LIVE_CLI=1 dotnet test Dante.sln --filter FullyQualifiedName~LiveUsageEvidenceTests
public sealed class LiveUsageEvidenceTests(ITestOutputHelper output)
{
    [LiveCliFact]
    public async Task CodexReportsTheSessionWindowTheWeekAndTheReset()
    {
        var root = Directory.CreateTempSubdirectory("dante-live-usage-").FullName;
        try
        {
            var reader = new UsageQuotaReader(new InteractiveAgentProcessLauncher(new AgentExecutableResolver()),
                new GeneralWorkspace(Path.Combine(root, "general")));

            var report = await reader.ReadAsync(AgentKind.Codex);

            output.WriteLine(UsageReportFormatter.Format(report, DateTimeOffset.UtcNow));
            Assert.Equal(TimeSpan.FromHours(5), report.Session.Window?.Duration);
            Assert.NotNull(report.Session.Window!.ResetsAt);
            Assert.Equal(TimeSpan.FromDays(7), report.Weekly.Window?.Duration);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class LiveCliFactAttribute : FactAttribute
    {
        public LiveCliFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("DANTE_LIVE_CLI") != "1")
                Skip = "Evidência com a CLI real; rode com DANTE_LIVE_CLI=1.";
        }
    }
}
