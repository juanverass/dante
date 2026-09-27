using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Dante.Worker.Repositories;

public sealed class RepositoryRegistry
{
    private static readonly Regex AliasPattern = new("^@[a-zA-Z][a-zA-Z0-9_]*$", RegexOptions.Compiled);
    private static readonly Regex GitHubPattern = new("^[a-zA-Z0-9_.-]+/[a-zA-Z0-9_.-]+$", RegexOptions.Compiled);
    private readonly object gate = new();
    private readonly string filePath;
    private readonly Dictionary<string, RepositoryDefinition> repositories = new(StringComparer.OrdinalIgnoreCase);

    public RepositoryRegistry(string? filePath = null)
    {
        this.filePath = filePath ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dante", "repositories.json");
        if (File.Exists(this.filePath))
        {
            var saved = JsonSerializer.Deserialize<List<RepositoryDefinition>>(File.ReadAllText(this.filePath))
                ?? throw new InvalidDataException("O catálogo de repositórios está vazio ou inválido.");
            foreach (var item in saved)
            {
                var alias = NormalizeAlias(item.Alias);
                if (!repositories.TryAdd(alias, item with { Alias = alias }))
                    throw new InvalidDataException($"Alias duplicado no catálogo: {alias}.");
            }
        }
    }

    public static string NormalizeAlias(string alias)
    {
        if (string.IsNullOrWhiteSpace(alias) || !AliasPattern.IsMatch(alias))
            throw new ArgumentException("Alias inválido. Use @ seguido de letras, números ou _; comece com letra.", nameof(alias));
        return alias.ToLowerInvariant();
    }

    public RepositoryDefinition Add(string alias, string path, string? gitHub = null)
    {
        alias = NormalizeAlias(alias);
        if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathFullyQualified(path))
            throw new ArgumentException("O path do repositório deve ser absoluto.", nameof(path));
        path = System.IO.Path.GetFullPath(path);
        if (!Directory.Exists(path))
            throw new ArgumentException("O diretório do repositório não existe.", nameof(path));
        var gitRoot = RunGit(path, "rev-parse", "--show-toplevel");
        if (gitRoot is null || !string.Equals(System.IO.Path.GetFullPath(gitRoot), path,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("O diretório não é a raiz de um repositório Git.", nameof(path));
        if (gitHub is not null)
        {
            if (!GitHubPattern.IsMatch(gitHub))
                throw new ArgumentException("Repositório GitHub inválido. Use owner/repo.", nameof(gitHub));
            var origin = RunGit(path, "config", "--local", "--get", "remote.origin.url");
            if (origin is not null && !MatchesGitHub(origin, gitHub))
                throw new ArgumentException("O remote origin não corresponde ao GitHub informado.", nameof(gitHub));
        }

        lock (gate)
        {
            if (repositories.ContainsKey(alias))
                throw new ArgumentException($"O alias {alias} já está cadastrado.", nameof(alias));
            var definition = new RepositoryDefinition(alias, path, gitHub);
            repositories.Add(alias, definition);
            try { Save(); }
            catch { repositories.Remove(alias); throw; }
            return definition;
        }
    }

    public RepositoryDefinition? Get(string alias)
    {
        alias = NormalizeAlias(alias);
        lock (gate) return repositories.GetValueOrDefault(alias);
    }

    public IReadOnlyList<RepositoryDefinition> List()
    {
        lock (gate) return repositories.Values.OrderBy(x => x.Alias, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public bool Remove(string alias)
    {
        alias = NormalizeAlias(alias);
        lock (gate)
        {
            if (!repositories.Remove(alias, out var removed)) return false;
            try { Save(); }
            catch { repositories.Add(alias, removed); throw; }
            return true;
        }
    }

    private void Save()
    {
        var directory = System.IO.Path.GetDirectoryName(filePath)!;
        Directory.CreateDirectory(directory);
        var temporary = System.IO.Path.Combine(directory, $".{System.IO.Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(repositories.Values.OrderBy(x => x.Alias).ToArray(),
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, filePath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string? RunGit(string path, params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo("git")
            {
                WorkingDirectory = path,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 ? output.Trim() : null;
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }

    private static bool MatchesGitHub(string origin, string gitHub)
    {
        var expected = gitHub;
        if (origin.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
            return string.Equals(origin[15..].TrimEnd('/').RemovesuffixGit(), expected,
                StringComparison.OrdinalIgnoreCase);
        if (Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
            string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            return string.Equals(uri.AbsolutePath.Trim('/').RemovesuffixGit(), expected, StringComparison.OrdinalIgnoreCase);
        return false;
    }
}

internal static class RepositoryPathExtensions
{
    public static string RemovesuffixGit(this string value) =>
        value.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? value[..^4] : value;
}
