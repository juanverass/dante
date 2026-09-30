namespace Dante.Worker.Sessions;

// D.A.N.T.E. permission profiles; each driver maps them to its CLI. Manual is the conservative default. Choosing a
// profile (and whether a full-access profile exists) is the Telegram UX of #67; drivers never infer one.
public enum AgentPermissionProfile
{
    // Works within the CLI's normal limits and forwards every escalation to the user.
    Manual,
    // Fewer interruptions: the CLI decides by itself within its sandbox.
    Auto,
    // Analysis and planning without changes.
    Plan
}
