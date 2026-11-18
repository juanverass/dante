using Dante.Application.Agentes;

namespace Dante.Application.Grupos;

public enum FalhaNaResolucaoDeGrupo
{
    Nenhuma,
    NomeInvalido,
    Inexistente,
    Desativado,
    Arquivado,
    SemVersaoAtiva,
    Indisponivel,
    ParticipanteInvalido
}

// Participante com a versão do agente congelada no momento da resolução: é o que a execução grava e usa.
public sealed record ParticipanteResolvidoDto(string Papel, string Handle, bool PodeEscrever, string? Responsabilidades,
    VersaoDoAgenteDto VersaoDoAgente);

// Grupo pronto para uma execução nova: a versão ativa (íntegra e com hash conferido) e cada participante com a versão
// atual do agente; ou o motivo da recusa. Nunca há substituição silenciosa de participante.
public sealed record ResolucaoDeGrupoDto
{
    public FalhaNaResolucaoDeGrupo Falha { get; init; }
    public string? Erro { get; init; }
    public string Nome { get; init; } = "";
    public GrupoDeAgentesDto? Grupo { get; init; }
    public VersaoDoGrupoDto? Versao { get; init; }
    public IReadOnlyList<ParticipanteResolvidoDto> Participantes { get; init; } = [];

    public bool Resolvido => Falha == FalhaNaResolucaoDeGrupo.Nenhuma;
}
