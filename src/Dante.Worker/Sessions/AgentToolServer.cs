namespace Dante.Worker.Sessions;

// Servidor MCP stdio que o D.A.N.T.E. oferece à sessão (#224): a CLI do agente o inicia como processo filho. Os argumentos
// e o ambiente aparecem na linha de comando da CLI e por isso nunca carregam segredos. ReadOnlyTools rodam sem aprovação;
// as demais ferramentas seguem o modo de aprovação da sessão.
public sealed record AgentToolServer(
    string Name,
    string Command,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyList<string> ReadOnlyTools);

// Decide, no início de cada sessão, quais servidores de ferramentas ela recebe.
public interface IAgentToolServers
{
    Task<IReadOnlyList<AgentToolServer>> ForAsync(long ownerUserId, AgentKind agent, CancellationToken cancellationToken = default);
}
