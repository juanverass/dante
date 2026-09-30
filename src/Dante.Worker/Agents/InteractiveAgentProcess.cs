using System.Diagnostics;
using System.Threading.Channels;

namespace Dante.Worker.Agents;

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
// whole process tree, so no child is left orphaned.
public sealed class InteractiveAgentProcess : IAsyncDisposable
{
    // Bounded: a slow reader stops the pumps, and the full pipe then pauses the agent instead of growing memory.
    private const int OutputCapacity = 256;

    private readonly Process process;
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
    private InteractiveAgentProcessState state = InteractiveAgentProcessState.Running;
    private bool killed;
    private int disposed;

    internal InteractiveAgentProcess(Process process, Func<string, string>? redactOutput)
    {
        this.process = process;
        this.redactOutput = redactOutput;
        ProcessId = process.Id;
        StartedAtUtc = DateTimeOffset.UtcNow;

        var stdout = PumpAsync(process.StandardOutput, AgentOutputStream.StandardOutput);
        var stderr = PumpAsync(process.StandardError, AgentOutputStream.StandardError);
        _ = CompleteOutputAsync(stdout, stderr);
        _ = MonitorExitAsync();
    }

    public int ProcessId { get; }

    public DateTimeOffset StartedAtUtc { get; }

    public InteractiveAgentProcessState State
    {
        get { lock (stateGate) { return state; } }
    }

    // Lines from stdout and stderr, in arrival order per stream; completes when both streams reach EOF.
    public ChannelReader<AgentOutputLine> Output => output.Reader;

    // Completes when the process exits, independently of whether the output was consumed.
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
                var input = process.StandardInput;
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

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The process exited between the check and the kill request.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 1)
        {
            return;
        }

        Kill();
        await Completion;
        // Stop the pumps even when nobody drains the output, so they do not wait on a full channel forever.
        await pumpCancellation.CancelAsync();
        process.Dispose();
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
            process.StandardInput.Close();
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
    }

    private async Task CompleteOutputAsync(Task stdout, Task stderr)
    {
        await Task.WhenAll(stdout, stderr);
        output.Writer.TryComplete();
    }

    private async Task MonitorExitAsync()
    {
        await process.WaitForExitAsync();
        bool wasKilled;
        lock (stateGate)
        {
            state = InteractiveAgentProcessState.Exited;
            wasKilled = killed;
        }

        completion.TrySetResult(new AgentProcessExit(process.ExitCode, wasKilled));
    }
}
