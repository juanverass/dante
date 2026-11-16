using Dante.Domain.Agentes;

namespace Dante.Application.Agentes;

// Agente registrado ou embutido (AD-68). Embutido (claude, codex) não tem versão persistida: a configuração dele é
// materializada das preferências do usuário a cada sessão ou execução.
public sealed record AgenteDto
{
    public Guid Id { get; init; }
    public string Handle { get; init; } = "";
    public string? Nome { get; init; }
    public string? Descricao { get; init; }
    public EstadoDoAgente Estado { get; init; }
    public bool Embutido { get; init; }
    public AgentKind? DriverEmbutido { get; init; }
    public int NumeroDaVersaoAtual { get; init; }
    public DateTimeOffset CriadoEm { get; init; }
    public DateTimeOffset AtualizadoEm { get; init; }
    public VersaoDoAgenteDto? VersaoAtual { get; init; }
    // Preenchido só na consulta detalhada (ObterAsync), da mais recente para a mais antiga.
    public IReadOnlyList<VersaoDoAgenteDto> Versoes { get; init; } = [];
}
