using Dante.Domain.Agentes;

namespace Dante.Application.Agentes;

// Agente pronto para uso, com a versão atual congelada; ou o motivo da recusa (nunca há fallback para outro agente).
// Embutido: DriverEmbutido preenchido, sem versão (o host materializa a configuração das preferências).
public sealed record ResolucaoDeAgenteDto
{
    public FalhaNaResolucaoDeAgente Falha { get; init; }
    public string? Erro { get; init; }
    public string Handle { get; init; } = "";
    public AgentKind? DriverEmbutido { get; init; }
    public AgenteDto? Agente { get; init; }
    public VersaoDoAgenteDto? Versao { get; init; }

    public bool Resolvido => Falha == FalhaNaResolucaoDeAgente.Nenhuma;
}
