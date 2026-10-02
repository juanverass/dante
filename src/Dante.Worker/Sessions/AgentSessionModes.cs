namespace Dante.Worker.Sessions;

// User-facing names of the permission profiles (#76). /mode, /permissions, /session start and the settings file share
// them; the CLI mapping of each mode stays in the drivers (AD-18, AD-19). There is no full-access mode.
public static class AgentSessionModes
{
    public static IReadOnlyList<AgentPermissionProfile> All { get; } =
        [AgentPermissionProfile.Manual, AgentPermissionProfile.Auto, AgentPermissionProfile.Plan];

    public static string Name(AgentPermissionProfile mode) => mode.ToString().ToLowerInvariant();

    public static string Label(AgentPermissionProfile mode) => mode switch
    {
        AgentPermissionProfile.Manual => "manual (aprovação)",
        AgentPermissionProfile.Auto => "auto (automático)",
        AgentPermissionProfile.Plan => "plan (planejamento)",
        _ => Name(mode)
    };

    public static string Description(AgentPermissionProfile mode) => mode switch
    {
        AgentPermissionProfile.Manual => "pede sua aprovação antes de ações fora dos limites da CLI",
        AgentPermissionProfile.Auto => "a CLI avalia permissões automaticamente, com menos interrupções",
        AgentPermissionProfile.Plan => "analisa e planeja sem alterar arquivos",
        _ => string.Empty
    };

    // "approval" is the friendly alias of manual; full or any other name is not a mode.
    public static bool TryParse(string? text, out AgentPermissionProfile mode)
    {
        mode = AgentPermissionProfile.Manual;
        if (string.Equals(text, "manual", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "approval", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(text, "auto", StringComparison.OrdinalIgnoreCase))
        {
            mode = AgentPermissionProfile.Auto;
            return true;
        }
        if (string.Equals(text, "plan", StringComparison.OrdinalIgnoreCase))
        {
            mode = AgentPermissionProfile.Plan;
            return true;
        }
        return false;
    }
}
