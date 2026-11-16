using Dante.Domain.Agentes;

namespace Dante.Application.Agentes;

// Entrada de uma configuração completa (criação). Modelo e esforço nulos usam o padrão do driver; o catálogo confere os
// dois ao salvar. Repositórios: aliases (@alias), "*" para qualquer um, vazio para só o workspace geral.
public sealed record ConfiguracaoDoAgenteDto
{
    public AgentKind Driver { get; init; }
    public string? Modelo { get; init; }
    public string? Esforco { get; init; }
    public string? Instrucoes { get; init; }
    public IReadOnlyList<CapacidadeDoAgente> Capacidades { get; init; } = [];
    public AgentPermissionProfile PerfilPadrao { get; init; } = AgentPermissionProfile.Plan;
    public AgentPermissionProfile PerfilMaximo { get; init; } = AgentPermissionProfile.Plan;
    public bool PodeEscrever { get; init; }
    public IReadOnlyList<FerramentaDoDante> Ferramentas { get; init; } = [];
    public IReadOnlyList<string> Repositorios { get; init; } = [];
    public LimitesDoAgente Limites { get; init; } = LimitesDoAgente.Nenhum;
}
