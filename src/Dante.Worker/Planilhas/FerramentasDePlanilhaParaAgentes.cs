using System.Reflection;
using Dante.Application.Planilhas;
using Dante.Worker.Sessions;

namespace Dante.Worker.Planilhas;

// Oferece o servidor MCP de planilhas às sessões quando há conta conectada no início da sessão (#224). O servidor é
// este mesmo executável em modo --mcp-planilhas; recebe só caminhos e a origem (usuário/agente) para a auditoria —
// a credencial cifrada é lida do disco pelo próprio processo, nunca passada pela linha de comando.
public sealed class FerramentasDePlanilhaParaAgentes(
    IServiceScopeFactory scopes,
    IConfiguration configuration,
    ILogger<FerramentasDePlanilhaParaAgentes> logger,
    (string Command, IReadOnlyList<string> Prefix)? comando = null) : IAgentToolServers
{
    public async Task<IReadOnlyList<AgentToolServer>> ForAsync(long ownerUserId, AgentKind agent,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var conexao = await scope.ServiceProvider.GetRequiredService<PlanilhasAppService>().ObterConexaoAsync(cancellationToken);
            if (!conexao.Conectada) return [];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Planilhas indisponíveis nunca impedem a sessão de começar.
            logger.LogWarning("Ferramentas de planilha indisponíveis para a sessão ({ErrorType}).", exception.GetType().Name);
            return [];
        }

        var (command, prefix) = comando ?? Executavel();
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["HOME"] = Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        foreach (var nome in new[] { "DOTNET_ROOT", "PATH" })
            if (Environment.GetEnvironmentVariable(nome) is { Length: > 0 } valor) environment[nome] = valor;
        foreach (var nome in new[] { "DANTE_GOOGLE_DIR", "DANTE_PLANILHAS_DIR" })
            if (configuration[nome] is { Length: > 0 } valor) environment[nome] = Path.GetFullPath(valor);
        return
        [
            new AgentToolServer(ServidorMcpDePlanilhas.Nome, command,
                [.. prefix, ServidorMcpDePlanilhas.Argumento, "--origem", $"telegram:{ownerUserId}", "--agente", agent.ToString().ToLowerInvariant()],
                environment, ServidorMcpDePlanilhas.FerramentasDeLeitura)
        ];
    }

    // O apphost (Dante.Worker) roda direto; sob "dotnet Dante.Worker.dll", a dll vai como primeiro argumento.
    private static (string Command, IReadOnlyList<string> Prefix) Executavel()
    {
        var processo = Environment.ProcessPath ?? throw new InvalidOperationException("Executável do Worker desconhecido.");
        var entrada = Assembly.GetEntryAssembly()?.Location;
        return Path.GetFileNameWithoutExtension(processo).Equals("dotnet", StringComparison.OrdinalIgnoreCase) && entrada is { Length: > 0 }
            ? (processo, [entrada])
            : (processo, []);
    }
}
