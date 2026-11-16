using Dante.Domain.Agentes;

namespace Dante.Application.Agentes;

// Edição parcial: o que é nulo herda da versão atual. Modelo/esforço "default" voltam ao padrão do driver; trocar o
// driver sem informar modelo e esforço também volta os dois ao padrão (um modelo nunca passa de um driver a outro).
// LimparInstrucoes remove as instruções. Nos limites, 0 remove o limite.
public sealed record AlteracaoDoAgenteDto
{
    public AgentKind? Driver { get; init; }
    public string? Modelo { get; init; }
    public string? Esforco { get; init; }
    public string? Instrucoes { get; init; }
    public bool LimparInstrucoes { get; init; }
    public IReadOnlyList<CapacidadeDoAgente>? Capacidades { get; init; }
    public AgentPermissionProfile? PerfilPadrao { get; init; }
    public AgentPermissionProfile? PerfilMaximo { get; init; }
    public bool? PodeEscrever { get; init; }
    public IReadOnlyList<FerramentaDoDante>? Ferramentas { get; init; }
    public IReadOnlyList<string>? Repositorios { get; init; }
    public int? ExecucoesSimultaneas { get; init; }
    public long? TokensPorExecucao { get; init; }
    public int? TurnosPorEtapa { get; init; }
    public int? MinutosPorEtapa { get; init; }
}
