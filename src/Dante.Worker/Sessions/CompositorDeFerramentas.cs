namespace Dante.Worker.Sessions;

public interface IProvedorDeFerramentas : IAgentToolServers;

public sealed class CompositorDeFerramentas(IEnumerable<IProvedorDeFerramentas> provedores) : IAgentToolServers
{
    public async Task<IReadOnlyList<AgentToolServer>> ForAsync(long ownerUserId, AgentKind agent, CancellationToken cancellationToken = default)
    {
        var resultado = new List<AgentToolServer>();
        foreach (var provedor in provedores) resultado.AddRange(await provedor.ForAsync(ownerUserId, agent, cancellationToken));
        return resultado;
    }
    public async Task<IReadOnlyList<AgentToolServer>> ForSessionAsync(string sessionId, SessionStartRequest request, CancellationToken ct = default)
    {
        var resultado = new List<AgentToolServer>();
        foreach (var provedor in provedores) resultado.AddRange(await provedor.ForSessionAsync(sessionId, request, ct));
        return resultado;
    }
    public void BeginTurn(string sessionId, AgentInput input)
    {
        foreach (var provedor in provedores) provedor.BeginTurn(sessionId, input);
    }
    public void EndSession(string sessionId)
    {
        foreach (var provedor in provedores) provedor.EndSession(sessionId);
    }
}
