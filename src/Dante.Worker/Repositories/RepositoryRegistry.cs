using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dante.Application.Contextos;

namespace Dante.Worker.Repositories;

public sealed class RepositoryRegistry : ICatalogoDeRepositorios
{
    private static readonly Regex AliasPattern = new("^@[a-zA-Z][a-zA-Z0-9_]*$", RegexOptions.Compiled);
    private static readonly Regex GitHubPattern = new("^[a-zA-Z0-9_.-]+/[a-zA-Z0-9_.-]+$", RegexOptions.Compiled);
    private static readonly Regex EnvironmentNamePattern = new("^[a-zA-Z_][a-zA-Z0-9_]*$", RegexOptions.Compiled);
    private static readonly Regex SensitiveNamePattern = new("SECRET|TOKEN|PASSWORD|PASSWD|PRIVATE|CREDENTIAL|API_KEY|ACCESS_KEY",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
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
        path = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
        if (!Directory.Exists(path))
            throw new ArgumentException("O diretório do repositório não existe.", nameof(path));
        var gitRoot = RunGit(path, "rev-parse", "--show-toplevel");
        if (gitRoot is null || !string.Equals(
                System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(gitRoot)), path,
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

    IReadOnlyList<RepositoryDefinition> ICatalogoDeRepositorios.Listar() => List();

    RepositoryDefinition? ICatalogoDeRepositorios.Obter(string alias) => Get(alias);

    ResolvedRepositoryEnvironment ICatalogoDeRepositorios.ResolverAmbiente(string alias) => ResolveEnvironment(alias);

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

    public RepositoryDefinition SetLiteral(string alias, string key, string value)
    {
        ValidateEnvironmentName(key);
        if (SensitiveNamePattern.IsMatch(key))
            throw new ArgumentException("Use /repo env bind para variáveis sensíveis.", nameof(key));
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("O valor não pode ser vazio.", nameof(value));
        return SetEnvironment(alias, new RepositoryEnvironmentEntry(key, value, null));
    }

    public RepositoryDefinition Bind(string alias, string key, string hostVariable)
    {
        ValidateEnvironmentName(key);
        ValidateEnvironmentName(hostVariable);
        return SetEnvironment(alias, new RepositoryEnvironmentEntry(key, null, hostVariable));
    }

    public bool RemoveEnvironment(string alias, string key)
    {
        ValidateEnvironmentName(key);
        alias = NormalizeAlias(alias);
        lock (gate)
        {
            var repository = RequireRepository(alias);
            var remaining = (repository.Environment ?? []).Where(entry =>
                !string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (remaining.Length == (repository.Environment?.Count ?? 0)) return false;
            repositories[alias] = repository with { Environment = remaining };
            try { Save(); }
            catch { repositories[alias] = repository; throw; }
            return true;
        }
    }

    public ResolvedRepositoryEnvironment ResolveEnvironment(string alias)
    {
        var repository = Get(alias) ?? throw new ArgumentException("Repositório não cadastrado.", nameof(alias));
        var entries = repository.Environment ?? [];
        var values = new Dictionary<string, string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var hasSecrets = false;
        foreach (var entry in entries)
        {
            if (entry.HostVariable is not null)
            {
                var value = System.Environment.GetEnvironmentVariable(entry.HostVariable);
                if (value is null)
                    throw new InvalidOperationException($"Variável do host {entry.HostVariable} não está configurada.");
                values[entry.Key] = value;
                hasSecrets = true;
            }
            else values[entry.Key] = entry.LiteralValue ?? string.Empty;
        }
        return new ResolvedRepositoryEnvironment(values, hasSecrets);
    }

    private RepositoryDefinition SetEnvironment(string alias, RepositoryEnvironmentEntry entry)
    {
        alias = NormalizeAlias(alias);
        lock (gate)
        {
            var repository = RequireRepository(alias);
            var entries = (repository.Environment ?? []).Where(existing =>
                !string.Equals(existing.Key, entry.Key, StringComparison.OrdinalIgnoreCase)).Append(entry).ToArray();
            var updated = repository with { Environment = entries };
            repositories[alias] = updated;
            try { Save(); }
            catch { repositories[alias] = repository; throw; }
            return updated;
        }
    }

    private RepositoryDefinition RequireRepository(string alias) => repositories.TryGetValue(alias, out var repository)
        ? repository : throw new ArgumentException("Repositório não cadastrado.", nameof(alias));

    private static void ValidateEnvironmentName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !EnvironmentNamePattern.IsMatch(name))
            throw new ArgumentException("Nome de variável de ambiente inválido.", nameof(name));
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
