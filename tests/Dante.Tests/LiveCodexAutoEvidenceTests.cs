using System.Diagnostics;
using Dante.Worker.Agents;
using Dante.Worker.Sessions;
using Xunit.Abstractions;

namespace Dante.Tests;

// Opt-in evidence: real Codex, a disposable Git repository, and a public read from GitHub.
// DANTE_LIVE_CLI=1 dotnet test Dante.sln --filter FullyQualifiedName~LiveCodexAutoEvidenceTests
public sealed class LiveCodexAutoEvidenceTests(ITestOutputHelper output)
{
    [LiveCliFact]
    public async Task AutoCreatesAGitBranchAndReadsGitHubWithoutHumanApproval()
    {
        var root = Directory.CreateTempSubdirectory("dante-live-auto-").FullName;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        try
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = root, UseShellExecute = false };
            start.ArgumentList.Add("init");
            start.ArgumentList.Add("--quiet");
            using (var git = Process.Start(start)!)
            {
                await git.WaitForExitAsync(timeout.Token);
                Assert.Equal(0, git.ExitCode);
            }

            await using var driver = new CodexSessionDriver(
                new InteractiveAgentProcessLauncher(new AgentExecutableResolver()));
            await driver.StartAsync(new AgentSessionStartOptions(root, Profile: AgentPermissionProfile.Auto),
                timeout.Token);
            await driver.StartTurnAsync("Neste repositório temporário, execute git checkout -b evidence-auto. " +
                "Depois execute git ls-remote https://github.com/juanverass/dante.git HEAD. " +
                "Se o sandbox bloquear qualquer comando, solicite a exceção de acesso necessária e tente novamente. " +
                "Não altere arquivos nem remotes e não faça push. Informe o resultado.", timeout.Token);
            var tools = new List<ToolCompletedEvent>();
            await foreach (var agentEvent in driver.ReadEventsAsync(timeout.Token))
            {
                Assert.IsNotType<ApprovalRequestedEvent>(agentEvent);
                if (agentEvent is ToolCompletedEvent tool)
                {
                    tools.Add(tool);
                    output.WriteLine($"Command succeeded={tool.Succeeded}: {tool.Output}");
                }
                if (agentEvent is MessageCompletedEvent message) output.WriteLine(message.Text);
                if (agentEvent is TurnCompletedEvent completed)
                {
                    Assert.Equal(AgentTurnOutcome.Completed, completed.Outcome);
                    break;
                }
            }

            Assert.Equal("ref: refs/heads/evidence-auto", File.ReadAllText(Path.Combine(root, ".git", "HEAD")).Trim());
            Assert.Contains(tools, tool => tool.Succeeded && tool.Output?.Contains("\tHEAD") == true);
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
