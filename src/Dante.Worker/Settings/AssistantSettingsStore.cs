using System.Text.Json;
using Dante.Worker.Agents;

namespace Dante.Worker.Settings;

public sealed class AssistantSettingsStore
{
    private readonly object gate = new();
    private readonly string filePath;
    private AssistantSettings current;

    public AssistantSettingsStore(string? filePath = null)
    {
        this.filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dante", "settings.json");
        current = File.Exists(this.filePath) ? Load(this.filePath) : AssistantSettings.Default;
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

    private static AssistantSettings Load(string path)
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
        return new AssistantSettings(agent);
    }

    private void Save()
    {
        var directory = Path.GetDirectoryName(filePath)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new SettingsFile(current.DefaultAgent.ToString()),
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, filePath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed record SettingsFile(string? DefaultAgent);
}
