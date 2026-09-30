using System.ComponentModel;
using System.Diagnostics;

namespace Dante.Worker.Agents;

public sealed class AgentProcessExecutor(IAgentExecutableResolver executableResolver) : IAgentProcessExecutor
{
    public bool IsAvailable(AgentKind agent) => executableResolver.Resolve(agent) is not null;

    public async Task<AgentProcessResult> ExecuteAsync(
        AgentProcessRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Arguments);

        if (cancellationToken.IsCancellationRequested)
        {
            return Result(AgentProcessStatus.Cancelled, null, null, "Execution was cancelled.");
        }

        if (!AgentProcessStartInfo.TryCreate(request, executableResolver, out var startInfo, out var error))
        {
            return Result(AgentProcessStatus.Failed, null, null, error);
        }

        using var process = new Process { StartInfo = startInfo };
        DateTimeOffset? startedAtUtc = null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!process.Start())
            {
                return Result(AgentProcessStatus.Failed, null, null, "Agent process could not be started.");
            }

            startedAtUtc = DateTimeOffset.UtcNow;
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                KillProcessTree(process);
                await process.WaitForExitAsync();
                return Result(AgentProcessStatus.Cancelled, startedAtUtc, process.ExitCode,
                    "Execution was cancelled.", await stdoutTask, await stderrTask);
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return process.ExitCode == 0
                ? Result(AgentProcessStatus.Succeeded, startedAtUtc, process.ExitCode, null, stdout, stderr)
                : Result(AgentProcessStatus.Failed, startedAtUtc, process.ExitCode,
                    $"Agent process exited with code {process.ExitCode}.", stdout, stderr);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result(AgentProcessStatus.Cancelled, startedAtUtc, null, "Execution was cancelled.");
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException)
        {
            return Result(AgentProcessStatus.Failed, startedAtUtc, null,
                $"Agent process could not be started: {exception.Message}");
        }
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between the check and the kill request.
        }
    }

    private static AgentProcessResult Result(
        AgentProcessStatus status,
        DateTimeOffset? startedAtUtc,
        int? exitCode,
        string? errorMessage,
        string standardOutput = "",
        string standardError = "") =>
        new(status, standardOutput, standardError, exitCode, startedAtUtc, DateTimeOffset.UtcNow, errorMessage);
}
