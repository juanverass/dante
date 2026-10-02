using Dante.Worker.Agents;
using Dante.Worker.Attachments;
using Dante.Worker.Jobs;
using Dante.Worker.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dante.Tests;

// Audio and video in a session (#96): the turn opens at once and is prepared before it reaches the agent, later
// messages queue behind it, /session stop cancels the tools, and a failed preparation ends only that turn.
public sealed class MediaSessionTurnTests : IAsyncDisposable
{
    private const long Owner = 123;
    private static readonly JobExecutionContext Repository = JobExecutionContext.Repository("@dante", "/repos/dante");
    private readonly string directory = Path.Combine(Path.GetTempPath(), "dante-media-turn-" + Guid.NewGuid().ToString("N"));
    private readonly FakeMediaTools tools = new();
    private readonly FakeSessionDriverFactory drivers = new();
    private readonly RecordingSessionSink sink = new();
    private readonly SessionRegistry registry;

    public MediaSessionTurnTests()
    {
        Directory.CreateDirectory(directory);
        drivers.Media = new MediaPreparer(tools);
        registry = new SessionRegistry(drivers, NullLogger<SessionRegistry>.Instance, sink);
    }

    [Fact]
    public async Task TextTurnsGoStraightToTheAgent()
    {
        var started = await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Claude, Repository));

        Assert.Equal(SubmitOutcome.TurnStarted, (await registry.SubmitAsync(Owner, null, "oi")).Outcome);
        Assert.Equal(["start", "turn:oi"], drivers.Created.Single().Calls);
        Assert.True(started.Accepted);
    }

    [Fact]
    public async Task AudioIsPreparedBeforeTheTurnReachesTheAgentAndLaterMessagesQueueBehindIt()
    {
        await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Claude, Repository));
        var driver = drivers.Created.Single();
        tools.Gate = new TaskCompletionSource();

        var voice = await registry.SubmitAsync(Owner, null, new AgentInput("o que eu disse?", [Voice()]));
        var next = await registry.SubmitAsync(Owner, null, "e depois?");

        Assert.Equal((SubmitOutcome.TurnStarted, SubmitOutcome.Queued), (voice.Outcome, next.Outcome));
        Assert.Equal(["start"], driver.Calls);
        Assert.Equal(AgentSessionState.Running, registry.GetActive(Owner)!.State);
        await Eventually(() => sink.Published.Any(item => item.Event is ToolStartedEvent
            { Description: "Processando 1 áudio localmente (transcrição)" } && item.Event.TurnId == voice.TurnId));

        tools.Gate.SetResult();
        await Eventually(() => driver.Calls.Count == 2);
        var turn = driver.Calls[1];
        Assert.StartsWith("turn:o que eu disse?\n\n[Anexos processados localmente pelo D.A.N.T.E.", turn);
        Assert.Contains("[0:00–0:04] A palavra secreta é girassol.", turn);

        driver.Emit(new TurnStartedEvent());
        driver.Emit(new TurnCompletedEvent(AgentTurnOutcome.Completed));
        await Eventually(() => driver.Calls.Contains("turn:e depois?"));
    }

    [Fact]
    public async Task StopDuringPreparationCancelsTheToolsAndTheAgentNeverSeesTheTurn()
    {
        await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Claude, Repository));
        var driver = drivers.Created.Single();
        tools.Gate = new TaskCompletionSource();
        var voice = await registry.SubmitAsync(Owner, null, new AgentInput("ouça", [Voice()]));
        await registry.SubmitAsync(Owner, null, "na fila");
        await Eventually(() => tools.Calls.Count > 0);

        var stopped = await registry.InterruptAsync(Owner, null);

        Assert.True(stopped.Accepted);
        Assert.Equal(1, stopped.DiscardedMessages);
        await Eventually(() => sink.Published.Any(item => item.Event is TurnCompletedEvent
            { Outcome: AgentTurnOutcome.Interrupted } && item.Event.TurnId == voice.TurnId));
        await Eventually(() => registry.GetActive(Owner)!.State == AgentSessionState.Idle);
        Assert.Equal(["start"], driver.Calls);
        // The tool saw the cancellation: nothing more runs, and the next message reaches the agent normally.
        tools.Gate.SetResult();
        await Task.Delay(100);
        Assert.Equal(["probe:A000001.ogg"], tools.Calls);
        await registry.SubmitAsync(Owner, null, "outra coisa");
        Assert.Equal(["start", "turn:outra coisa"], driver.Calls);
    }

    [Fact]
    public async Task AFailedPreparationEndsOnlyThatTurnWithTheReason()
    {
        await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Codex, Repository));
        var driver = drivers.Created.Single();
        tools.ProbeFails = true;

        var voice = await registry.SubmitAsync(Owner, null, new AgentInput("ouça", [Voice()]));

        await Eventually(() => sink.Published.Any(item => item.Event is TurnCompletedEvent
        {
            Outcome: AgentTurnOutcome.Failed, Error: "Não consegui ler o áudio: arquivo inválido ou corrompido."
        } && item.Event.TurnId == voice.TurnId));
        await Eventually(() => registry.GetActive(Owner)!.State == AgentSessionState.Idle);
        Assert.Equal(SubmitOutcome.TurnStarted, (await registry.SubmitAsync(Owner, null, "texto")).Outcome);
        Assert.Equal(["start", "turn:texto"], driver.Calls);
    }

    [Fact]
    public async Task ASteerDuringPreparationGoesWithThePreparedTurn()
    {
        await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Codex, Repository));
        var driver = drivers.Created.Single();
        tools.Gate = new TaskCompletionSource();
        await registry.SubmitAsync(Owner, null, new AgentInput("resuma", [Voice()]));

        var steer = await registry.SubmitAsync(Owner, null, "foque no final", MessageDelivery.Steer);
        tools.Gate.SetResult();

        Assert.Equal(SubmitOutcome.Steered, steer.Outcome);
        await Eventually(() => driver.Calls.Count == 2);
        Assert.EndsWith("\n\nOrientação enviada durante o processamento: foque no final", driver.Calls[1]);
        Assert.DoesNotContain(driver.Calls, call => call.StartsWith("steer:"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AttachmentsInASteerCannotExpandThePreparingTurnsImageOrByteBudget(bool byteBudget)
    {
        await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Codex, Repository));
        var driver = drivers.Created.Single();
        tools.Gate = new TaskCompletionSource();
        var images = Enumerable.Range(2, byteBudget ? 3 : 4).Select(index =>
        {
            var path = Path.Combine(directory, $"A{index:D6}.png");
            File.WriteAllBytes(path, TestImages.Png(64, 40));
            return new Attachment($"A{index:D6}", Owner, AttachmentKind.Image, "image/png", path,
                byteBudget ? 6 * 1024 * 1024 : new FileInfo(path).Length, 64, 40, null);
        }).ToArray();
        var media = Voice();
        if (!byteBudget)
        {
            tools.DefaultProbe = new MediaProbe(TimeSpan.FromSeconds(42), HasAudio: false, HasVideo: true);
            media = media with { Kind = AttachmentKind.Video };
        }
        var turn = await registry.SubmitAsync(Owner, null, new AgentInput("resuma", [.. images, media]));
        await Eventually(() => tools.Calls.Count > 0);
        var extra = images[0] with { Id = "A000099", Bytes = byteBudget ? 3 * 1024 * 1024 : images[0].Bytes };

        var refused = await registry.SubmitAsync(Owner, null, new AgentInput("inclua esta imagem", [extra]),
            MessageDelivery.Steer);

        Assert.Equal(SubmitOutcome.Rejected, refused.Outcome);
        Assert.StartsWith("Não envie anexos em /steer durante o processamento", refused.Error);
        Assert.Equal(turn.TurnId, registry.GetActive(Owner)!.ActiveTurnId);
        Assert.Equal(AgentSessionState.Running, registry.GetActive(Owner)!.State);
        Assert.Equal(SubmitOutcome.Steered,
            (await registry.SubmitAsync(Owner, null, "foque no final", MessageDelivery.Steer)).Outcome);
        tools.Gate.SetResult();
        await Eventually(() => driver.Calls.Count == 2);
        var prepared = Assert.Single(driver.TurnInputs);
        Assert.Equal(byteBudget ? 3 : MediaPreparer.MaxImages, prepared.Attachments.Count);
        Assert.True(prepared.Attachments.Sum(attachment => attachment.Bytes) <= 20 * 1024 * 1024);
        Assert.DoesNotContain(prepared.Attachments, attachment => attachment.Id == extra.Id);
        Assert.DoesNotContain("inclua esta imagem", prepared.Text);
        Assert.EndsWith("Orientação enviada durante o processamento: foque no final", prepared.Text);
        Assert.DoesNotContain(driver.Calls, call => call.StartsWith("steer:"));
    }

    [Fact]
    public async Task AudioIsNeverASteerAndNeedsTheMediaWrapper()
    {
        await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Codex, Repository));
        await registry.SubmitAsync(Owner, null, "tarefa longa");

        var steer = await registry.SubmitAsync(Owner, null, new AgentInput("ouça", [Voice()]), MessageDelivery.Steer);

        Assert.Equal(SubmitOutcome.Rejected, steer.Outcome);
        Assert.Equal("Áudio e vídeo não vão em /steer; envie como mensagem comum, que entra na fila da sessão.",
            steer.Error);
        Assert.True(registry.GetActive(Owner) is { } active && (await registry.CloseAsync(Owner, active.Id)).Accepted);

        drivers.Media = null;
        await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Codex, Repository));
        var plain = await registry.SubmitAsync(Owner, null, new AgentInput("ouça", [Voice()]));
        Assert.StartsWith("Só imagens chegam ao agente", plain.Error);
    }

    private Attachment Voice()
    {
        var path = Path.Combine(directory, "A000001.ogg");
        File.WriteAllBytes(path, TestMedia.Ogg());
        return new Attachment("A000001", Owner, AttachmentKind.Audio, "audio/ogg", path, 16, null, null, null);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "A condição esperada não ocorreu a tempo.");
            await Task.Delay(10);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await registry.DisposeAsync();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
