using Dante.Domain.Agentes;

namespace Dante.Application.Agentes;

// claude e codex como agentes embutidos (AD-68): não editáveis e sem versão persistida. A configuração é materializada
// das preferências atuais do usuário no início de cada sessão ou execução (AD-24/25/26), com tudo o que esses
// caminhos já permitem hoje, então /claude, /codex, /agent set e a conversa comum não mudam de comportamento; o hash
// dessa configuração vai para o registro da sessão.
public static class AgentesEmbutidos
{
    public static IReadOnlyList<AgentKind> Todos { get; } = [AgentKind.Claude, AgentKind.Codex];

    public static string Handle(AgentKind driver) => driver.ToString().ToLowerInvariant();

    public static bool TryObter(string? handle, out AgentKind driver)
    {
        foreach (var embutido in Todos)
            if (string.Equals(handle, Handle(embutido), StringComparison.OrdinalIgnoreCase))
            {
                driver = embutido;
                return true;
            }
        driver = default;
        return false;
    }

    public static ConfiguracaoDoAgente Materializar(AgentKind driver, AgentModelSelection? selecao,
        AgentPermissionProfile perfil) =>
        new(driver, perfil, AgentPermissionProfile.Auto, podeEscrever: true, selecao?.Model, selecao?.Effort,
            instrucoes: null, Enum.GetValues<CapacidadeDoAgente>(), Enum.GetValues<FerramentaDoDante>(),
            [ConfiguracaoDoAgente.QualquerRepositorio]);

    public static AgenteDto Descrever(AgentKind driver) => new()
    {
        Handle = Handle(driver),
        Nome = driver.ToString(),
        Descricao = "Agente embutido: segue suas preferências de modelo, esforço e modo; não editável.",
        Estado = EstadoDoAgente.Ativo,
        Embutido = true,
        DriverEmbutido = driver
    };
}
