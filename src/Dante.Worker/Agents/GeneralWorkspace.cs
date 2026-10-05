using Dante.Application.Contextos;

namespace Dante.Worker.Agents;

public sealed class GeneralWorkspace : IWorkspaceGeral
{
    public string Path { get; }

    string IWorkspaceGeral.Caminho => Path;

    public GeneralWorkspace(string? path = null)
    {
        path ??= Environment.GetEnvironmentVariable("DANTE_GENERAL_WORKSPACE");
        path ??= System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dante", "workspaces", "general");
        if (!System.IO.Path.IsPathFullyQualified(path))
            throw new ArgumentException("O workspace geral deve ter path absoluto.", nameof(path));
        Path = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(Path);
    }
}
