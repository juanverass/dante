using System.Globalization;
using System.Text.Json;
using Dante.Worker.Agents;
using Dante.Worker.Repositories;

namespace Dante.Worker.Settings;

public sealed class AssistantSettingsStore
{
    private readonly object gate = new();
    private readonly string filePath;
    private readonly Dictionary<long, string> activeRepositories = [];
    private AssistantSettings current;

    public AssistantSettingsStore(string? filePath = null)
    {
        this.filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dante", "settings.json");
        current = File.Exists(this.filePath) ? Load(this.filePath, activeRepositories) : AssistantSettings.Default;
    }

    public AssistantSettings Current
    {
        get { lock (gate) return current; }
    }

    public AssistantSettings SetDefaultAgent(AgentKind agent)
    {
        if (!Enum.IsDefined(agent))
            throw new ArgumentOutOfRangeException(nameof(agent), "Agente padrão inválido. Use Claude ou Codex.");
        lock (gate)
        {
            var previous = current;
            current = current with { DefaultAgent = agent };
            try { Save(); }
            catch { current = previous; throw; }
            return current;
        }
    }

    // Active repository per Telegram user ID; the alias is not checked against the registry here.
    public string? GetActiveRepository(long userId)
    {
        lock (gate) return activeRepositories.GetValueOrDefault(userId);
    }

    public void SetActiveRepository(long userId, string? alias)
    {
        alias = alias is null ? null : RepositoryRegistry.NormalizeAlias(alias);
        lock (gate)
        {
            var previous = activeRepositories.GetValueOrDefault(userId);
            if (previous == alias) return;
            if (alias is null) activeRepositories.Remove(userId);
            else activeRepositories[userId] = alias;
            try { Save(); }
            catch
            {
                if (previous is null) activeRepositories.Remove(userId);
                else activeRepositories[userId] = previous;
                throw;
            }
        }
    }

    public int ClearActiveRepository(string alias)
    {
        alias = RepositoryRegistry.NormalizeAlias(alias);
        lock (gate)
        {
            var users = activeRepositories.Where(entry => entry.Value == alias).Select(entry => entry.Key).ToArray();
            if (users.Length == 0) return 0;
            foreach (var user in users) activeRepositories.Remove(user);
            try { Save(); }
            catch
            {
                foreach (var user in users) activeRepositories[user] = alias;
                throw;
            }
            return users.Length;
        }
    }

    public static bool TryParseAgent(string? value, out AgentKind agent)
    {
        agent = default;
        if (string.Equals(value, nameof(AgentKind.Claude), StringComparison.OrdinalIgnoreCase))
            agent = AgentKind.Claude;
        else if (string.Equals(value, nameof(AgentKind.Codex), StringComparison.OrdinalIgnoreCase))
            agent = AgentKind.Codex;
        else return false;
        return true;
    }

    private static AssistantSettings Load(string path, Dictionary<long, string> activeRepositories)
    {
        SettingsFile? saved;
        try { saved = JsonSerializer.Deserialize<SettingsFile>(File.ReadAllText(path)); }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"O arquivo de configurações {path} está corrompido: {exception.Message}", exception);
        }
        if (saved is null)
            throw new InvalidDataException($"O arquivo de configurações {path} está vazio ou inválido.");
        if (!TryParseAgent(saved.DefaultAgent, out var agent))
            throw new InvalidDataException(
                $"DefaultAgent inválido em {path}: \"{saved.DefaultAgent}\". Valores permitidos: Claude ou Codex.");
        foreach (var (user, alias) in saved.ActiveRepositories ?? [])
        {
            if (!long.TryParse(user, NumberStyles.None, CultureInfo.InvariantCulture, out var userId))
                throw new InvalidDataException($"ActiveRepositories inválido em {path}: usuário \"{user}\".");
            try { activeRepositories[userId] = RepositoryRegistry.NormalizeAlias(alias); }
            catch (ArgumentException)
            {
                throw new InvalidDataException($"ActiveRepositories inválido em {path}: alias \"{alias}\".");
            }
        }
        return new AssistantSettings(agent);
    }

    private void Save()
    {
        var directory = Path.GetDirectoryName(filePath)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new SettingsFile(current.DefaultAgent.ToString(),
                    activeRepositories.OrderBy(entry => entry.Key).ToDictionary(
                        entry => entry.Key.ToString(CultureInfo.InvariantCulture), entry => entry.Value)),
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, filePath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed record SettingsFile(string? DefaultAgent, Dictionary<string, string>? ActiveRepositories = null);
}
