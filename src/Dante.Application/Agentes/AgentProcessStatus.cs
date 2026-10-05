namespace Dante.Application.Agentes;

// Legado movido do Worker na #167: o nome em inglês fica até a migração explícita (AD-38).
public enum AgentProcessStatus
{
    Succeeded,
    Failed,
    Cancelled
}
