using Dante.Domain.Agentes;

namespace Dante.Infrastructure.Agentes;

// Legado movido do Worker na #167: o nome em inglês fica até a migração explícita (AD-38).
// Only these fixed program names can be launched by the production resolver.
public sealed class AgentExecutableResolver : IAgentExecutableResolver
{
    public string? Resolve(AgentKind agent)
    {
        var executableName = agent switch
        {
            AgentKind.Codex => "codex",
            AgentKind.Claude => "claude",
            _ => null
        };

        if (executableName is null)
        {
            return null;
        }

        var names = OperatingSystem.IsWindows()
            ? new[] { executableName + ".exe", executableName + ".com" }
            : new[] { executableName };

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            // An empty or relative PATH entry would make the current directory executable.
            if (!Path.IsPathFullyQualified(directory))
            {
                continue;
            }

            foreach (var name in names)
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate) && IsExecutable(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static bool IsExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        try
        {
            const UnixFileMode executeBits = UnixFileMode.UserExecute |
                                             UnixFileMode.GroupExecute |
                                             UnixFileMode.OtherExecute;
            return (File.GetUnixFileMode(path) & executeBits) != 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
