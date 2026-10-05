using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Dante.Infrastructure.Agentes;

// Legado movido do Worker na #167: o nome em inglês fica até a migração explícita (AD-38).
// Shared by the one-shot executor and interactive processes: fixed executable, ArgumentList, no shell,
// and the same environment filtering for General Mode and repository environments.
internal static class AgentProcessStartInfo
{
    public static bool TryCreate(
        AgentProcessRequest request,
        IAgentExecutableResolver executableResolver,
        [NotNullWhen(true)] out ProcessStartInfo? startInfo,
        [NotNullWhen(false)] out string? error)
    {
        startInfo = null;
        if (!Path.IsPathFullyQualified(request.WorkingDirectory) ||
            !Directory.Exists(request.WorkingDirectory))
        {
            error = "Working directory does not exist or is not absolute.";
            return false;
        }

        var executable = executableResolver.Resolve(request.Agent);
        if (executable is null)
        {
            error = $"{request.Agent} executable is unavailable. Install it and add its native binary to PATH.";
            return false;
        }

        startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        if (request.IsGeneral || request.EnvironmentVariables is not null)
        {
            // Keep only the host settings needed to launch/authenticate local CLIs.
            var inherited = new Dictionary<string, string?>(startInfo.Environment,
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            startInfo.Environment.Clear();
            foreach (var name in new[] { "PATH", "HOME", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "TMP", "TEMP",
                         "TMPDIR", "SYSTEMROOT", "WINDIR", "COMSPEC", "LANG", "LC_ALL", "TERM",
                         "HTTPS_PROXY", "HTTP_PROXY", "NO_PROXY", "OPENAI_API_KEY", "ANTHROPIC_API_KEY" })
            {
                if (inherited.TryGetValue(name, out var value) && value is not null)
                    startInfo.Environment[name] = value;
            }
        }

        if (request.EnvironmentVariables is not null)
        {
            foreach (var (name, value) in request.EnvironmentVariables)
                startInfo.Environment[name] = value;
        }

        // A conexão do host não pertence ao ambiente dos agentes, inclusive em Repository Mode.
        foreach (var name in startInfo.Environment.Keys.Where(name =>
                     name.Equals("ConnectionStrings__Dante", StringComparison.OrdinalIgnoreCase)).ToArray())
            startInfo.Environment.Remove(name);

        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        error = null;
        return true;
    }
}
