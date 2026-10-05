namespace Dante.Domain.Agentes;

// Legado movido do Worker na #167: o nome em inglês fica até a migração explícita (AD-38).
// D.A.N.T.E. permission profiles, independent of how each agent enforces them (the adapters translate them; AD-41).
// Manual is the conservative default; there is no full-access profile, and none is ever inferred.
public enum AgentPermissionProfile
{
    // The agent stays within its normal limits and every escalation goes to the user.
    Manual,
    // Fewer interruptions: the agent decides by itself within its own limits.
    Auto,
    // Analysis and planning without changes.
    Plan
}
