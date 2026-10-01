namespace Dante.Worker.Agents;

// Model chosen for one agent execution (#77); a null Model keeps the CLI's own default. It is fixed when a session or
// job starts and always belongs to one agent: a Claude model never reaches Codex or vice versa.
public sealed record AgentModelSelection(string? Model = null, string? Effort = null)
{
    private const int MaxNameLength = 100;

    public static AgentModelSelection CliDefault { get; } = new();

    public string EffortLabel => Effort ?? "padrão da CLI";

    public static bool IsValidEffort(string? value) =>
        value is { Length: > 0 and <= 40 } && value[0] != '-' && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    public string ModelLabel => Model ?? "padrão da CLI";

    // Syntax only (the CLI catalog decides whether the model exists): what the CLIs use in model ids and aliases,
    // never whitespace, a leading dash or anything else a command line could interpret.
    public static bool IsValidName(string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= MaxNameLength && name[0] != '-' &&
        name.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' or ':' or '/'
            or '@' or '[' or ']');
}
