using System.Diagnostics;
using System.Threading.Channels;

namespace Dante.Infrastructure.Agentes;

// Legado movido do Worker na #167: o nome em inglês fica até a migração explícita (AD-38).
public enum AgentOutputStream
{
    StandardOutput,
    StandardError
}

public sealed record AgentOutputLine(AgentOutputStream Stream, string Text);

// Killed is true when the D.A.N.T.E. terminated the process tree instead of letting it exit on its own.
public sealed record AgentProcessExit(int ExitCode, bool Killed);

// Running → InputClosed (stdin closed, waiting for the agent to exit) → Exited; Stopping while StopAsync runs.
// The session lifecycle (idle, running turn, waiting for user…) is AgentSessionState; this is only the process.
public enum InteractiveAgentProcessState
{
    Running,
    InputClosed,
    Stopping,
    Exited
}

// Thrown by WriteLineAsync when stdin no longer accepts input: closed, stopping or the process exited.
public sealed class AgentProcessInputClosedException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

// One long-lived agent process: stdout/stderr are read line by line as they arrive and stdin accepts serialized
// JSONL writes. Interrupting a turn is a protocol message written by the driver; StopAsync/DisposeAsync end the
// native containment before Completion succeeds. Compatibility mode only tracks recorded survivors.
public sealed class InteractiveAgentProcess : IAsyncDisposable
{
    // Bounded: a slow reader stops the pumps, and the full pipe then pauses the agent instead of growing memory.
    private const int OutputCapacity = 256;
    // Observação periódica, complementada antes de fechar stdin. A finalização para o ciclo e aguarda
    // a descoberta em andamento; falha de identidade/observação nunca equivale a árvore vazia.
    private static readonly TimeSpan DescendantTrackingInterval = TimeSpan.FromSeconds(1);

    private readonly Process process;
    private readonly ProcessoContido? contido;
    private readonly StreamWriter entrada;
    private readonly StreamReader saidaPadrao;
    private readonly StreamReader erroPadrao;
    private readonly object limpezaGate = new();
    private Task? limpezaNativa;
    public bool ContencaoNativa => contido is not null;
    public string? AvisoDeContencao => ContencaoNativa ? null :
        "Modo compatibilidade: limpeza limitada aos descendentes observados; filhos entre snapshots podem sobreviver.";
    private readonly Func<int, DateTime, CancellationToken, Task> encerrarDescendente;
    private readonly TimeSpan prazoDaLimpeza;
    private readonly Task concluirSaida;
    private readonly Task rastreamento;
    private readonly CancellationTokenSource pararObservacao = new();
    private readonly Func<int, DateTime, IReadOnlyList<(int Id, DateTime StartTime)>> observarDescendentes;
    // O lock de descendants cobre a descoberta inteira, não apenas a publicação dos resultados.
    private bool observacaoEncerrada;
    private bool observacaoInicialConcluida;
    private Exception? falhaDeObservacao;
    private FimDaObservacaoDaRaiz? fimDaObservacaoDaRaiz;
    internal FimDaObservacaoDaRaiz? FimDaObservacao
    {
        get { lock (descendants) { return fimDaObservacaoDaRaiz; } }
    }
    private readonly Func<string, string>? redactOutput;
    private readonly Channel<AgentOutputLine> output =
        Channel.CreateBounded<AgentOutputLine>(new BoundedChannelOptions(OutputCapacity)
        {
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    private readonly SemaphoreSlim inputGate = new(1, 1);
    private readonly CancellationTokenSource pumpCancellation = new();
    private readonly TaskCompletionSource<AgentProcessExit> completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object stateGate = new();
    private readonly DateTime? startTime;
    private readonly Dictionary<int, DateTime> descendants = [];
    private InteractiveAgentProcessState state = InteractiveAgentProcessState.Running;
    private bool killed;
    private int disposed;

    internal InteractiveAgentProcess(Process process, Func<string, string>? redactOutput,
        Func<int, DateTime, CancellationToken, Task>? encerrarDescendente = null, TimeSpan? prazoDaLimpeza = null,
        Func<int, DateTime?>? obterIdentidade = null,
        Func<int, DateTime, IReadOnlyList<(int Id, DateTime StartTime)>>? observarDescendentes = null, ProcessoContido? contido = null)
    {
        this.process = process;
        this.contido = contido;
        entrada = contido?.Entrada ?? process.StandardInput;
        saidaPadrao = contido?.Saida ?? process.StandardOutput;
        erroPadrao = contido?.Erro ?? process.StandardError;
        this.encerrarDescendente = encerrarDescendente ?? ProcessTree.EncerrarEAguardarAsync;
        this.prazoDaLimpeza = prazoDaLimpeza ?? TimeSpan.FromSeconds(5);
        this.redactOutput = redactOutput;
        ProcessId = process.Id;
        StartedAtUtc = DateTimeOffset.UtcNow;
        // Consulta o Process original dos dois lados: não aceita a identidade de um PID reciclado após sua saída.
        startTime = process.HasExited ? null : (obterIdentidade ?? ProcessTree.TryGetStartTime)(process.Id);
        if (process.HasExited) startTime = null;
        this.observarDescendentes = observarDescendentes ?? ((pid, inicio) =>
            ProcessTree.GetDescendants(pid, inicio, exigirPrincipal: !observacaoInicialConcluida));
        if (startTime is null)
            falhaDeObservacao = new InvalidOperationException("Identidade inicial do processo principal indisponível; limpeza não confirmada.");

        var stdout = PumpAsync(saidaPadrao, AgentOutputStream.StandardOutput);
        var stderr = PumpAsync(erroPadrao, AgentOutputStream.StandardError);
        concluirSaida = CompleteOutputAsync(stdout, stderr);
        // A primeira descoberta ocorre antes de iniciar o monitor de saída.
        rastreamento = contido is null ? TrackDescendantsAsync() : Task.CompletedTask;
        _ = MonitorExitAsync();
    }

    public int ProcessId { get; }

    public DateTimeOffset StartedAtUtc { get; }

    public InteractiveAgentProcessState State
    {
        get { lock (stateGate) { return state; } }
    }

    // Lines from stdout and stderr, in arrival order per stream; completes when both streams reach EOF. If a line
    // cannot be processed (the redaction hook threw), it completes with that exception and the process is killed.
    public ChannelReader<AgentOutputLine> Output => output.Reader;

    // Em modo estrito confirma raiz encerrada e contenção vazia. No modo compatibilidade, só os filhos observados.
    // O consumo da saída é independente da confirmação; falha/timeout nunca confirma limpeza.
    public Task<AgentProcessExit> Completion => completion.Task;

    // Writes one line atomically: concurrent callers are serialized and never interleave. The token only
    // cancels the wait for the turn to write; once started, a line is written whole to keep the framing intact.
    public async Task WriteLineAsync(string line, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.Contains('\n') || line.Contains('\r'))
        {
            throw new ArgumentException("An input line cannot contain line breaks.", nameof(line));
        }

        await inputGate.WaitAsync(cancellationToken);
        try
        {
            EnsureAcceptsInput();
            try
            {
                var input = entrada;
                await input.WriteAsync(line + "\n");
                await input.FlushAsync();
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException
                                                  or InvalidOperationException)
            {
                throw new AgentProcessInputClosedException("Agent process no longer accepts input.", exception);
            }
        }
        finally
        {
            inputGate.Release();
        }
    }

    // Closes stdin after any write in progress; the structured CLIs end the session when stdin reaches EOF.
    public async Task CloseInputAsync()
    {
        TrackDescendants();
        await inputGate.WaitAsync();
        try
        {
            CloseInputCore(InteractiveAgentProcessState.InputClosed);
        }
        finally
        {
            inputGate.Release();
        }
    }

    // Session shutdown: closes stdin, waits up to gracePeriod for a clean exit, then kills the process tree.
    public async Task<AgentProcessExit> StopAsync(TimeSpan gracePeriod)
    {
        lock (stateGate)
        {
            if (state != InteractiveAgentProcessState.Exited)
            {
                state = InteractiveAgentProcessState.Stopping;
            }
        }

        // The agent may exit as soon as stdin closes; its descendants must be known before that.
        TrackDescendants();
        using var grace = new CancellationTokenSource(gracePeriod);
        try
        {
            // A writer blocked on a full pipe holds the gate; the grace period bounds that wait as well.
            await inputGate.WaitAsync(grace.Token);
            try
            {
                CloseInputCore(InteractiveAgentProcessState.Stopping);
            }
            finally
            {
                inputGate.Release();
            }

            await Completion.WaitAsync(grace.Token);
        }
        catch (OperationCanceledException) when (grace.IsCancellationRequested)
        {
            Kill();
        }

        return await Completion;
    }

    // Immediate termination of the whole process tree; stdin stops accepting input right away.
    public void Kill()
    {
        lock (stateGate)
        {
            if (state == InteractiveAgentProcessState.Exited)
            {
                return;
            }

            state = InteractiveAgentProcessState.Stopping;
            killed = true;
        }

        if (contido is not null)
        {
            _ = IniciarLimpezaNativa();
            return;
        }
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The process exited between the check and the kill request.
        }

        KillTrackedDescendants();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 1)
        {
            return;
        }

        try
        {
            Kill();
            await Completion;
        }
        finally
        {
            // Também libera pipes e recursos quando a limpeza falha ou ninguém drena a saída.
            await pumpCancellation.CancelAsync();
            // Libera também os recursos da contenção quando a confirmação tiver falhado.
            contido?.Contencao.Dispose();
            await concluirSaida;
            entrada.Dispose(); saidaPadrao.Dispose(); erroPadrao.Dispose();
            process.Dispose();
            pumpCancellation.Dispose();
            pararObservacao.Dispose();
        }
    }

    private void EnsureAcceptsInput()
    {
        var current = State;
        if (current != InteractiveAgentProcessState.Running)
        {
            throw new AgentProcessInputClosedException(current == InteractiveAgentProcessState.Exited
                ? "Agent process has exited."
                : "Agent process input is closed.");
        }
    }

    // Callers hold inputGate, so no line is cut in half.
    private void CloseInputCore(InteractiveAgentProcessState next)
    {
        lock (stateGate)
        {
            if (state == InteractiveAgentProcessState.Exited)
            {
                return;
            }

            if (state == InteractiveAgentProcessState.Running)
            {
                state = next;
            }
        }

        try
        {
            entrada.Close();
        }
        catch (IOException)
        {
            // The agent already closed its end of the pipe.
        }
    }

    private async Task PumpAsync(StreamReader reader, AgentOutputStream stream)
    {
        var token = pumpCancellation.Token;
        try
        {
            while (await reader.ReadLineAsync(token) is { } line)
            {
                var text = redactOutput is null ? line : redactOutput(line);
                await output.Writer.WriteAsync(new AgentOutputLine(stream, text), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Disposed: remaining output is discarded.
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // The pipe broke together with the process.
        }
        catch (ChannelClosedException)
        {
            // The other stream failed and already completed the output.
        }
        catch (Exception exception)
        {
            // Unprocessed output cannot be published and an undrained pipe would stall the agent: the consumer sees
            // the failure on Output and the process is killed.
            output.Writer.TryComplete(exception);
            Kill();
        }
    }

    private async Task CompleteOutputAsync(Task stdout, Task stderr)
    {
        await Task.WhenAll(stdout, stderr);
        output.Writer.TryComplete();
    }

    private async Task TrackDescendantsAsync()
    {
        try
        {
            while (!pararObservacao.IsCancellationRequested && TrackDescendants())
                await Task.Delay(DescendantTrackingInterval, pararObservacao.Token);
        }
        catch (OperationCanceledException) when (pararObservacao.IsCancellationRequested) { }
    }

    // Serializa descoberta e publicação com a captura final, inclusive nas chamadas síncronas de Stop/CloseInput.
    private bool TrackDescendants()
    {
        if (contido is not null) return false;
        lock (descendants)
        {
            if (observacaoEncerrada || falhaDeObservacao is not null) return false;
            try
            {
                if (startTime is not { } rootStartTime)
                    throw new InvalidOperationException("Identidade inicial do processo principal indisponível.");
                foreach (var (id, descendantStartTime) in observarDescendentes(ProcessId, rootStartTime))
                    descendants.TryAdd(id, descendantStartTime);
                observacaoInicialConcluida = true;
                return true;
            }
            catch (ObservacaoDaRaizEncerradaException exception)
            {
                fimDaObservacaoDaRaiz = exception.Motivo;
                // Após observação válida, a saída da raiz encerra o ciclo; identidade divergente degrada a limpeza.
                if (exception.Motivo != FimDaObservacaoDaRaiz.PrincipalEncerrado || !observacaoInicialConcluida)
                    falhaDeObservacao = exception;
                return false;
            }
            catch (Exception exception)
            {
                falhaDeObservacao = exception;
                return false;
            }
        }
    }

    private void KillTrackedDescendants()
    {
        KeyValuePair<int, DateTime>[] tracked;
        lock (descendants)
        {
            tracked = [.. descendants];
        }

        foreach (var (id, descendantStartTime) in tracked)
        {
            ProcessTree.Kill(id, descendantStartTime);
        }
    }

    private Task IniciarLimpezaNativa()
    {
        lock (limpezaGate) return limpezaNativa ??= LimparContencaoAsync();
    }

    private async Task LimparContencaoAsync()
    {
        try
        {
            using var prazo = new CancellationTokenSource(prazoDaLimpeza);
            try { await contido!.Contencao.EncerrarEConfirmarVaziaAsync(prazo.Token); }
            catch (OperationCanceledException) when (prazo.IsCancellationRequested)
            { throw new TimeoutException("Prazo de limpeza da contenção excedido; grupo vazio não confirmado."); }
        }
        catch (Exception exception)
        {
            output.Writer.TryComplete(exception);
            await pumpCancellation.CancelAsync();
            completion.TrySetException(exception);
            throw;
        }
    }

    private async Task MonitorExitAsync()
    {
        try
        {
            await process.WaitForExitAsync();
            if (contido is not null)
            {
                await IniciarLimpezaNativa();
                bool encerrado;
                lock (stateGate) { state = InteractiveAgentProcessState.Exited; encerrado = killed; }
                completion.TrySetResult(new AgentProcessExit(process.ExitCode, encerrado));
                return;
            }
            // Para o ciclo, aguarda a descoberta em andamento e só então congela a última captura publicada.
            await pararObservacao.CancelAsync();
            await rastreamento;
            KeyValuePair<int, DateTime>[] tracked;
            Exception? falha;
            lock (descendants)
            {
                observacaoEncerrada = true;
                fimDaObservacaoDaRaiz ??= FimDaObservacaoDaRaiz.PrincipalEncerrado;
                tracked = [.. descendants];
                falha = falhaDeObservacao;
                if (!observacaoInicialConcluida && falha is null)
                    falha = new InvalidOperationException("Observação inicial da árvore não confirmada.");
            }
            using var prazo = new CancellationTokenSource(prazoDaLimpeza);
            try
            {
                // Um prazo compartilhado limita a limpeza de todos os descendentes, inclusive em paralelo.
                await Task.WhenAll(tracked.Select(x => encerrarDescendente(x.Key, x.Value, prazo.Token)));
            }
            catch (OperationCanceledException) when (prazo.IsCancellationRequested)
            {
                throw new TimeoutException("Prazo de limpeza dos processos descendentes excedido; saída não confirmada.");
            }
            // Limpa o que foi identificado com segurança, mas nunca transforma observação incompleta em sucesso.
            if (falha is not null)
                throw new InvalidOperationException("Observação da árvore de processos falhou; limpeza não confirmada.", falha);
            bool wasKilled;
            lock (stateGate)
            {
                state = InteractiveAgentProcessState.Exited;
                wasKilled = killed;
            }
            completion.TrySetResult(new AgentProcessExit(process.ExitCode, wasKilled));
        }
        catch (Exception exception)
        {
            await pararObservacao.CancelAsync();
            await rastreamento;
            lock (stateGate) { state = InteractiveAgentProcessState.Exited; }
            output.Writer.TryComplete(exception);
            await pumpCancellation.CancelAsync();
            completion.TrySetException(exception);
        }
    }
}
