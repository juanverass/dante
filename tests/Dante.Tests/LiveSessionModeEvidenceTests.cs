using Dante.Worker.Agents;
using Dante.Worker.Sessions;
using Xunit.Abstractions;

namespace Dante.Tests;

// Real CLI evidence, explicitly opt-in. The prompts use no tools and run only in disposable workspaces.
public sealed class LiveSessionModeEvidenceTests(ITestOutputHelper output)
{
    [LiveModeFact]
    public Task ClaudeKeepsContextAcrossEveryDirectedModeTransition() => ValidateAsync(AgentKind.Claude);

    [LiveModeFact]
    public Task CodexKeepsContextAcrossEveryDirectedModeTransition() => ValidateAsync(AgentKind.Codex);

    private async Task ValidateAsync(AgentKind agent)
    {
        var root = Directory.CreateTempSubdirectory("dante-live-modes-").FullName;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var launcher = new InteractiveAgentProcessLauncher(new AgentExecutableResolver());
        await using IAgentSessionDriver driver = agent == AgentKind.Claude
            ? new ClaudeSessionDriver(launcher) : new CodexSessionDriver(launcher);
        try
        {
            var started = await driver.StartAsync(new AgentSessionStartOptions(root,
                ModelSelection: new AgentModelSelection(Effort: "high")), timeout.Token);
            output.WriteLine($"{agent} session={started.UpstreamSessionId} process={started.ProcessId} model={started.Model}");
            await using var events = driver.ReadEventsAsync(timeout.Token).GetAsyncEnumerator();
            await driver.StartTurnAsync("Remember this token in our conversation: MODE108. Reply only MODE108. Do not use tools.", timeout.Token);
            await ReadTurnAsync(events);
            // Starting in manual, this visits all six directed transitions exactly once.
            foreach (var mode in new[] { AgentPermissionProfile.Auto, AgentPermissionProfile.Manual,
                AgentPermissionProfile.Plan, AgentPermissionProfile.Auto, AgentPermissionProfile.Plan, AgentPermissionProfile.Manual })
            {
                await driver.ChangeModeAsync(mode, timeout.Token);
                await driver.StartTurnAsync("What token did I ask you to remember earlier? Reply only that token. Do not use tools.", timeout.Token);
                var turn = await ReadTurnAsync(events);
                Assert.Contains(turn.OfType<MessageCompletedEvent>(), message => message.Text.Contains("MODE108"));
                if (agent == AgentKind.Codex) Assert.Equal(mode, turn.OfType<ModeAppliedEvent>().Single().Profile);
                output.WriteLine($"Confirmed {mode}; remembered MODE108; same upstream session/process.");
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task<List<AgentEvent>> ReadTurnAsync(IAsyncEnumerator<AgentEvent> events)
    {
        var result = new List<AgentEvent>();
        while (await events.MoveNextAsync())
        {
            result.Add(events.Current);
            Assert.IsNotType<ApprovalRequestedEvent>(events.Current);
            if (events.Current is TurnCompletedEvent completed)
            {
                Assert.Equal(AgentTurnOutcome.Completed, completed.Outcome);
                return result;
            }
        }
        throw new InvalidOperationException("A CLI encerrou antes de concluir o turno.");
    }

    private sealed class LiveModeFactAttribute : FactAttribute
    {
        public LiveModeFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("DANTE_LIVE_CLI") != "1")
                Skip = "Evidência com CLI real; rode com DANTE_LIVE_CLI=1.";
        }
    }
}
