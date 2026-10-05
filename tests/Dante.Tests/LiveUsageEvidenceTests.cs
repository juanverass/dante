using Dante.Infrastructure.Agentes;
using Dante.Infrastructure.Contextos;
using Dante.Infrastructure.Uso;
using Dante.Worker.Telegram;
using Xunit.Abstractions;

namespace Dante.Tests;

// Opt-in evidence (#116, #117): the real CLIs' quotas through the production reader, in the General workspace. No turn runs.
// DANTE_LIVE_CLI=1 dotnet test Dante.sln --filter FullyQualifiedName~LiveUsageEvidenceTests
public sealed class LiveUsageEvidenceTests(ITestOutputHelper output)
{
    [LiveCliFact]
    public Task CodexReportsTheSessionWindowTheWeekAndTheReset() => ReportsTheThreeFieldsAsync(AgentKind.Codex);

    [LiveCliFact]
    public Task ClaudeReportsTheSessionWindowTheWeekAndTheReset() => ReportsTheThreeFieldsAsync(AgentKind.Claude);

    private async Task ReportsTheThreeFieldsAsync(AgentKind agent)
    {
        var root = Directory.CreateTempSubdirectory("dante-live-usage-").FullName;
        try
        {
            var reader = new UsageQuotaReader(new InteractiveAgentProcessLauncher(new AgentExecutableResolver()),
                new GeneralWorkspace(Path.Combine(root, "general")));

            var report = await reader.ReadAsync(agent);

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
