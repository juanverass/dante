using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dante.Worker.Agents;
using Dante.Worker.Repositories;
using Dante.Worker.Sessions;

namespace Dante.Worker.Settings;

public sealed class AssistantSettingsStore
{
    private readonly object gate = new();
    private readonly string filePath;
    private readonly Dictionary<long, string> activeRepositories = [];
    private readonly Dictionary<long, AgentPermissionProfile> sessionModes = [];
    private readonly Dictionary<long, Dictionary<AgentKind, string>> models = [];
    private readonly Dictionary<long, Dictionary<AgentKind, string>> efforts = [];
    private AssistantSettings current;

    public AssistantSettingsStore(string? filePath = null)
    {
        this.filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dante", "settings.json");
        current = File.Exists(this.filePath)
            ? Load(this.filePath, activeRepositories, sessionModes, models, efforts)
            : AssistantSettings.Default;
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

    // Default mode for the user's new sessions (#76); manual when never chosen. Existing sessions keep theirs.
    public AgentPermissionProfile GetSessionMode(long userId)
    {
        lock (gate) return sessionModes.GetValueOrDefault(userId, AgentPermissionProfile.Manual);
    }

    public void SetSessionMode(long userId, AgentPermissionProfile mode)
    {
        if (!AgentSessionModes.All.Contains(mode))
            throw new ArgumentOutOfRangeException(nameof(mode), "Modo inválido. Use manual, auto ou plan.");
        lock (gate)
        {
            var hadPrevious = sessionModes.TryGetValue(userId, out var previous);
            if (hadPrevious && previous == mode) return;
            sessionModes[userId] = mode;
            try { Save(); }
            catch
            {
                if (hadPrevious) sessionModes[userId] = previous;
                else sessionModes.Remove(userId);
                throw;
            }
        }
    }

    // Default model of each agent for the user's new sessions and one-shot jobs (#77); null keeps the CLI default.
    // Only the name's syntax is checked here: whether the CLI offers it is checked when it is chosen and when used.
    public string? GetModel(long userId, AgentKind agent)
    {
        lock (gate) return GetAgentValue(models, userId, agent);
    }

    public void SetModel(long userId, AgentKind agent, string? model)
    {
        if (!Enum.IsDefined(agent))
            throw new ArgumentOutOfRangeException(nameof(agent), "Agente inválido. Use Claude ou Codex.");
        if (model is not null && !AgentModelSelection.IsValidName(model))
            throw new ArgumentException("Nome de modelo inválido.", nameof(model));
        lock (gate) SetAgentValue(models, userId, agent, model);
    }

    public string? GetEffort(long userId, AgentKind agent)
    {
        lock (gate) return GetAgentValue(efforts, userId, agent);
    }

    public void SetEffort(long userId, AgentKind agent, string? effort)
    {
        if (!Enum.IsDefined(agent)) throw new ArgumentOutOfRangeException(nameof(agent));
        if (effort is not null && !AgentModelSelection.IsValidEffort(effort))
            throw new ArgumentException("Nome de esforço inválido.", nameof(effort));
        lock (gate) SetAgentValue(efforts, userId, agent, effort);
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

    private static string? GetAgentValue(Dictionary<long, Dictionary<AgentKind, string>> map, long userId,
        AgentKind agent) => map.TryGetValue(userId, out var byAgent) ? byAgent.GetValueOrDefault(agent) : null;

    // Caller holds the gate; a failed write restores the previous value.
    private void SetAgentValue(Dictionary<long, Dictionary<AgentKind, string>> map, long userId, AgentKind agent,
        string? value)
    {
        var previous = GetAgentValue(map, userId, agent);
        if (previous == value) return;
        Apply(value);
        try { Save(); }
        catch
        {
            Apply(previous);
            throw;
        }

        void Apply(string? applied)
        {
            if (applied is not null)
            {
                if (!map.TryGetValue(userId, out var byAgent)) map[userId] = byAgent = [];
                byAgent[agent] = applied;
            }
            else if (map.TryGetValue(userId, out var byAgent) && byAgent.Remove(agent) && byAgent.Count == 0)
            {
                map.Remove(userId);
            }
        }
    }

    private static AssistantSettings Load(string path, Dictionary<long, string> activeRepositories,
        Dictionary<long, AgentPermissionProfile> sessionModes, Dictionary<long, Dictionary<AgentKind, string>> models,
        Dictionary<long, Dictionary<AgentKind, string>> efforts)
    {
        SettingsFile? saved;
        string? nullMap;
        try
        {
            var content = File.ReadAllText(path);
            saved = JsonSerializer.Deserialize<SettingsFile>(content);
            // An absent map is a legacy file; an explicit null is invalid, not an empty map.
            using var document = JsonDocument.Parse(content);
            nullMap = new[]
                {
                    nameof(SettingsFile.ActiveRepositories), nameof(SettingsFile.SessionModes),
                    nameof(SettingsFile.Models), nameof(SettingsFile.Efforts)
                }
                .FirstOrDefault(name => document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty(name, out var property)
                    && property.ValueKind == JsonValueKind.Null);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"O arquivo de configurações {path} está corrompido: {exception.Message}", exception);
        }
        if (saved is null)
            throw new InvalidDataException($"O arquivo de configurações {path} está vazio ou inválido.");
        if (!TryParseAgent(saved.DefaultAgent, out var agent))
            throw new InvalidDataException(
                $"DefaultAgent inválido em {path}: \"{saved.DefaultAgent}\". Valores permitidos: Claude ou Codex.");
        if (nullMap is not null)
            throw new InvalidDataException($"{nullMap} inválido em {path}: null não é permitido.");
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
        foreach (var (user, mode) in saved.SessionModes ?? [])
        {
            if (!long.TryParse(user, NumberStyles.None, CultureInfo.InvariantCulture, out var userId))
                throw new InvalidDataException($"SessionModes inválido em {path}: usuário \"{user}\".");
            // Only the canonical names are stored; an alias or unknown value is never guessed.
            if (!AgentSessionModes.TryParse(mode, out var parsed) || AgentSessionModes.Name(parsed) != mode)
                throw new InvalidDataException(
                    $"SessionModes inválido em {path}: modo \"{mode}\". Valores permitidos: manual, auto ou plan.");
            sessionModes[userId] = parsed;
        }
        LoadAgentMap(path, nameof(SettingsFile.Models), saved.Models, AgentModelSelection.IsValidName, models);
        LoadAgentMap(path, nameof(SettingsFile.Efforts), saved.Efforts, AgentModelSelection.IsValidEffort, efforts);
        return new AssistantSettings(agent);
    }

    // { "<user id>": { "Claude|Codex": "<value>" } }; anything else stops the Worker instead of being guessed.
    private static void LoadAgentMap(string path, string name, Dictionary<string, Dictionary<string, string>?>? saved,
        Func<string?, bool> isValid, Dictionary<long, Dictionary<AgentKind, string>> target)
    {
        foreach (var (user, byAgent) in saved ?? [])
        {
            if (!long.TryParse(user, NumberStyles.None, CultureInfo.InvariantCulture, out var userId) || byAgent is null)
                throw new InvalidDataException($"{name} inválido em {path}: usuário \"{user}\".");
            foreach (var (agentName, value) in byAgent)
            {
                if (!TryParseAgent(agentName, out var agent))
                    throw new InvalidDataException($"{name} inválido em {path}: agente \"{agentName}\".");
                if (!isValid(value))
                    throw new InvalidDataException($"{name} inválido em {path}: valor \"{value}\" para {agent}.");
                if (!target.TryGetValue(userId, out var values)) target[userId] = values = [];
                values[agent] = value;
            }
        }
    }

    private static Dictionary<string, Dictionary<string, string>?>? SaveAgentMap(
        Dictionary<long, Dictionary<AgentKind, string>> map) =>
        map.Count == 0 ? null : map.OrderBy(entry => entry.Key).ToDictionary(
            entry => entry.Key.ToString(CultureInfo.InvariantCulture),
            Dictionary<string, string>? (entry) => entry.Value
                .OrderBy(value => value.Key.ToString(), StringComparer.Ordinal)
                .ToDictionary(value => value.Key.ToString(), value => value.Value));

    private void Save()
    {
        var directory = Path.GetDirectoryName(filePath)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new SettingsFile(current.DefaultAgent.ToString(),
                    activeRepositories.OrderBy(entry => entry.Key).ToDictionary(
                        entry => entry.Key.ToString(CultureInfo.InvariantCulture), entry => entry.Value),
                    sessionModes.Count == 0 ? null : sessionModes.OrderBy(entry => entry.Key).ToDictionary(
                        entry => entry.Key.ToString(CultureInfo.InvariantCulture),
                        entry => AgentSessionModes.Name(entry.Value)),
                    SaveAgentMap(models), SaveAgentMap(efforts)),
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                }));
            File.Move(temporary, filePath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed record SettingsFile(string? DefaultAgent, Dictionary<string, string>? ActiveRepositories = null,
        Dictionary<string, string>? SessionModes = null,
        Dictionary<string, Dictionary<string, string>?>? Models = null,
        Dictionary<string, Dictionary<string, string>?>? Efforts = null);
}
