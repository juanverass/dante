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

        if (!Path.IsPathFullyQualified(request.WorkingDirectory) ||
            !Directory.Exists(request.WorkingDirectory))
        {
            return Result(AgentProcessStatus.Failed, null, null, "Working directory does not exist or is not absolute.");
        }

        var executable = executableResolver.Resolve(request.Agent);
        if (executable is null)
        {
            return Result(AgentProcessStatus.Failed, null, null,
                $"{request.Agent} executable is unavailable. Install it and add its native binary to PATH.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        if (request.IsGeneral)
        {
            // Keep only the host settings needed to launch/authenticate local CLIs.
            var inherited = new Dictionary<string, string?>(startInfo.Environment,
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            startInfo.Environment.Clear();
            foreach (var name in new[] { "PATH", "HOME", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "TMP", "TEMP",
                         "TMPDIR", "SYSTEMROOT", "WINDIR", "COMSPEC", "LANG", "LC_ALL", "TERM", "OPENAI_API_KEY",
                         "ANTHROPIC_API_KEY", "HTTPS_PROXY", "HTTP_PROXY", "NO_PROXY" })
            {
                if (inherited.TryGetValue(name, out var value) && value is not null)
                    startInfo.Environment[name] = value;
            }
        }

        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
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
