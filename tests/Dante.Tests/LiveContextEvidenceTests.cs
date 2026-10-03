using Dante.Worker.Agents;
using Dante.Worker.Sessions;
using Xunit.Abstractions;

namespace Dante.Tests;

// Opt-in evidence (#120, #121, AD-32): the production drivers against the real CLIs, in one process per agent. A marker
// told before /clear must not be recoverable after it; a marker and an instruction must survive /compact.
// DANTE_LIVE_CLI=1 dotnet test Dante.sln --filter FullyQualifiedName~LiveContextEvidenceTests
public sealed class LiveContextEvidenceTests(ITestOutputHelper output)
{
    private const string Marker = "ZEBRA-4712";

    [LiveCliTheory]
    [InlineData(AgentKind.Claude)]
    [InlineData(AgentKind.Codex)]
    public async Task ClearForgetsTheMarkerAndKeepsTheSession(AgentKind agent)
    {
        var root = Directory.CreateTempSubdirectory("dante-live-context-").FullName;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            var launcher = new InteractiveAgentProcessLauncher(new AgentExecutableResolver());
            await using IAgentSessionDriver driver = agent == AgentKind.Codex
                ? new CodexSessionDriver(launcher)
                : new ClaudeSessionDriver(launcher);
            var started = await driver.StartAsync(new AgentSessionStartOptions(root, IsGeneral: true,
                Profile: AgentPermissionProfile.Plan), timeout.Token);
            await using var events = driver.ReadEventsAsync(timeout.Token).GetAsyncEnumerator();

            await driver.StartTurnAsync($"Memorize the code word {Marker}. Reply only: OK", timeout.Token);
            output.WriteLine(await ReplyAsync(events));
            var cleared = await driver.ClearContextAsync(timeout.Token);
            output.WriteLine($"{started.UpstreamSessionId} -> {cleared.UpstreamSessionId}");
            await driver.StartTurnAsync("Which code word did I ask you to memorize earlier? If you do not know, " +
                "answer exactly: UNKNOWN", timeout.Token);
            var answer = await ReplyAsync(events);
            output.WriteLine(answer);

            Assert.NotEqual(started.UpstreamSessionId, cleared.UpstreamSessionId);
            Assert.DoesNotContain(Marker, answer);
            Assert.Contains("UNKNOWN", answer);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [LiveCliTheory]
    [InlineData(AgentKind.Claude)]
    [InlineData(AgentKind.Codex)]
    public async Task CompactKeepsTheMarkerAndTheInstruction(AgentKind agent)
    {
        var root = Directory.CreateTempSubdirectory("dante-live-compact-").FullName;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        try
        {
            var launcher = new InteractiveAgentProcessLauncher(new AgentExecutableResolver());
            await using IAgentSessionDriver driver = agent == AgentKind.Codex
                ? new CodexSessionDriver(launcher)
                : new ClaudeSessionDriver(launcher);
            var started = await driver.StartAsync(new AgentSessionStartOptions(root, IsGeneral: true,
                Profile: AgentPermissionProfile.Plan), timeout.Token);
            await using var events = driver.ReadEventsAsync(timeout.Token).GetAsyncEnumerator();

            await driver.StartTurnAsync($"Memorize the code word {Marker}. Instruction for the rest of this " +
                "conversation: always answer in UPPERCASE. Reply only: OK", timeout.Token);
            output.WriteLine(await ReplyAsync(events));
            var compacted = await driver.CompactContextAsync(timeout.Token);
            output.WriteLine($"{started.UpstreamSessionId}: {compacted.PreTokens} -> {compacted.PostTokens} tokens");
            await driver.StartTurnAsync("Which code word did I ask you to memorize? Answer with the code word only.",
                timeout.Token);
            var answer = await ReplyAsync(events);
            output.WriteLine(answer);

            Assert.Contains(Marker, answer);
            Assert.Equal(answer.ToUpperInvariant(), answer);
            Assert.Equal(agent == AgentKind.Claude, compacted.PreTokens is not null && compacted.PostTokens is not null);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<string> ReplyAsync(IAsyncEnumerator<AgentEvent> events)
    {
        var text = "";
        while (await events.MoveNextAsync())
        {
            switch (events.Current)
            {
                case MessageCompletedEvent message:
                    text += message.Text;
                    break;
                case TurnCompletedEvent completed:
                    Assert.Equal(AgentTurnOutcome.Completed, completed.Outcome);
                    return text;
            }
        }

        throw new InvalidOperationException("O agente encerrou antes de concluir o turno.");
    }

    private sealed class LiveCliTheoryAttribute : TheoryAttribute
    {
        public LiveCliTheoryAttribute()
        {
            if (Environment.GetEnvironmentVariable("DANTE_LIVE_CLI") != "1")
                Skip = "Evidência com a CLI real; rode com DANTE_LIVE_CLI=1.";
        }
    }
}
