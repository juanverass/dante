namespace Dante.Worker.Agents;

public sealed record AgentProcessRequest(
    AgentKind Agent,
    string WorkingDirectory,
    IReadOnlyList<string> Arguments,
    bool IsGeneral = false,
    IReadOnlyDictionary<string, string>? EnvironmentVariables = null);
