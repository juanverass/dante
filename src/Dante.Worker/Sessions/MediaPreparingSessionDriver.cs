using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Dante.Application.Anexos;
using Dante.Worker.Attachments;

namespace Dante.Worker.Sessions;

// Audio and video of a turn are prepared before the turn reaches the agent (#96). The turn is already open in the
// session while the transcript and frames are produced, so later messages queue behind it in order (AD-16), /status
// shows it running and /session stop cancels the tools. Only then the prepared input starts the turn upstream.
// Inputs without audio or video go straight to the wrapped driver.
public sealed class MediaPreparingSessionDriver(IAgentSessionDriver inner, MediaPreparer preparer) : IAgentSessionDriver
{
    private const string ProgressItem = "dante-media";
    private readonly Channel<AgentEvent> events = Channel.CreateUnbounded<AgentEvent>();
    private readonly CancellationTokenSource lifetime = new();
    // A prepared turn starts upstream in the background: every call to the wrapped driver goes one at a time.
    private readonly SemaphoreSlim upstream = new(1, 1);
    private readonly object gate = new();
    private Preparation? preparing;
    private Task? forwarding;

    public AgentDriverCapabilities Capabilities { get; } = inner.Capabilities with { MediaInput = true };

    public Task<AgentSessionStarted> StartAsync(AgentSessionStartOptions options,
        CancellationToken cancellationToken = default) => inner.StartAsync(options, cancellationToken);

    public Task ChangeModeAsync(AgentPermissionProfile profile, CancellationToken cancellationToken = default) =>
        UpstreamAsync(token => inner.ChangeModeAsync(profile, token), cancellationToken);

    public Task<AgentContextCleared> ClearContextAsync(CancellationToken cancellationToken = default) =>
        inner.ClearContextAsync(cancellationToken);

    public Task<AgentContextCompacted> CompactContextAsync(CancellationToken cancellationToken = default) =>
        inner.CompactContextAsync(cancellationToken);

    public Task StartTurnAsync(AgentInput input, CancellationToken cancellationToken = default)
    {
        if (!MediaPreparer.NeedsPreparation(input.Attachments))
            return UpstreamAsync(token => inner.StartTurnAsync(input, token), cancellationToken);
        var preparation = new Preparation(input, CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token));
        var video = input.Attachments.Any(attachment => attachment.Kind == AttachmentKind.Video);
        lock (gate)
        {
            preparing = preparation;
            // Written before anything can interrupt the turn, so progress never follows its end.
            events.Writer.TryWrite(new ToolStartedEvent(ProgressItem, AgentToolKind.Tool,
                $"Processando {MediaPreparer.Describe(input.Attachments)} localmente " +
                $"({(video ? "quadros e transcrição" : "transcrição")})")
            { Presentation = $"Processando {MediaPreparer.Describe(input.Attachments)}..." });
        }
        _ = Task.Run(() => PrepareAndStartAsync(preparation), CancellationToken.None);
        return Task.CompletedTask;
    }

    // A steer for a turn still being prepared is guidance for the same turn: it goes with the prepared input.
    public Task SteerAsync(AgentInput input, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            if (preparing is { } preparation)
            {
                // Frame and byte budgets belong to the original input. Only text can be added while preparing.
                if (input.Attachments.Count > 0)
                    throw new AgentSteerRejectedException("Não envie anexos em /steer durante o processamento de áudio/vídeo. " +
                        "Reenvie as imagens como mensagem comum para entrar na fila; /steer aceita só texto nesse momento.");
                preparation.Steers.Add(input);
                return Task.CompletedTask;
            }
        }
        return UpstreamAsync(token => inner.SteerAsync(input, token), cancellationToken);
    }

    // Interrupting during preparation stops the tools; the turn never reached the agent, so it ends here.
    public Task InterruptTurnAsync(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            if (preparing is { } preparation)
            {
                preparing = null;
                preparation.Cancellation.Cancel();
                events.Writer.TryWrite(new TurnCompletedEvent(AgentTurnOutcome.Interrupted));
                return Task.CompletedTask;
            }
        }
        return UpstreamAsync(inner.InterruptTurnAsync, cancellationToken);
    }

    public Task RespondAsync(string upstreamRequestId, AgentUserResponse response,
        CancellationToken cancellationToken = default) =>
        UpstreamAsync(token => inner.RespondAsync(upstreamRequestId, response, token), cancellationToken);

    public async IAsyncEnumerable<AgentEvent> ReadEventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        forwarding ??= Task.Run(() => ForwardAsync(cancellationToken), CancellationToken.None);
        await foreach (var agentEvent in events.Reader.ReadAllAsync(cancellationToken)) yield return agentEvent;
    }

    public Task CloseAsync(CancellationToken cancellationToken = default)
    {
        CancelPreparation();
        return UpstreamAsync(inner.CloseAsync, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        CancelPreparation();
        await lifetime.CancelAsync();
        await inner.DisposeAsync();
        events.Writer.TryComplete();
    }

    private async Task PrepareAndStartAsync(Preparation preparation)
    {
        try
        {
            AgentInput prepared;
            try
            {
                prepared = await preparer.PrepareAsync(preparation.Input, preparation.Cancellation.Token);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                lock (gate)
                {
                    if (preparing != preparation) return;
                    preparing = null;
                    events.Writer.TryWrite(new TurnCompletedEvent(AgentTurnOutcome.Failed,
                        exception is MediaPreparationException ? exception.Message
                            : "Falha inesperada ao processar áudio ou vídeo."));
                }
                return;
            }
            lock (gate)
            {
                if (preparing != preparation) return;
                events.Writer.TryWrite(new ToolCompletedEvent(ProgressItem, AgentToolKind.Tool, true));
            }

            await upstream.WaitAsync(lifetime.Token);
            try
            {
                AgentInput input;
                lock (gate)
                {
                    // Interrupted (or closing) while the tools ran: nothing goes upstream.
                    if (preparing != preparation) return;
                    preparing = null;
                    input = preparation.Steers.Aggregate(prepared, (merged, steer) => new AgentInput(
                        merged.Text + "\n\nOrientação enviada durante o processamento: " + steer.Text,
                        [.. merged.Attachments, .. steer.Attachments]));
                }
                await inner.StartTurnAsync(input, lifetime.Token);
            }
            finally
            {
                upstream.Release();
            }
        }
        catch (OperationCanceledException) when (preparation.Cancellation.IsCancellationRequested)
        {
            // Interrupted, closed or disposed: whoever cancelled already reported it.
        }
        catch (Exception)
        {
            // Like a turn the registry could not send: the session cannot go on with a turn that never started.
            events.Writer.TryComplete(new AgentProtocolException(
                "Não foi possível enviar a mensagem ao agente; a sessão foi encerrada."));
        }
    }

    private async Task ForwardAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var agentEvent in inner.ReadEventsAsync(cancellationToken))
                await events.Writer.WriteAsync(agentEvent, cancellationToken);
            events.Writer.TryComplete();
        }
        catch (Exception exception)
        {
            events.Writer.TryComplete(exception);
        }
    }

    private void CancelPreparation()
    {
        lock (gate)
        {
            preparing?.Cancellation.Cancel();
            preparing = null;
        }
    }

    private async Task UpstreamAsync(Func<CancellationToken, Task> call, CancellationToken cancellationToken)
    {
        await upstream.WaitAsync(cancellationToken);
        try { await call(cancellationToken); }
        finally { upstream.Release(); }
    }

    private sealed class Preparation(AgentInput input, CancellationTokenSource cancellation)
    {
        public AgentInput Input { get; } = input;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public List<AgentInput> Steers { get; } = [];
    }
}
