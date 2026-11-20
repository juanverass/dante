using Dante.Domain.Grupos;

namespace Dante.Application.Grupos;

// Configuração completa de uma versão nova de grupo. Com PapelDoCoordenador, o grupo é do modo Coordenador (AD-75, #310):
// sem roteiro nem rodadas, e nenhum participante escreve; sem ele, Roteiro (AD-69).
public sealed record ConfiguracaoDoGrupoDto
{
    public string? PapelDoCoordenador { get; init; }
    public IReadOnlyList<ParticipanteInformadoDto> Participantes { get; init; } = [];
    public IReadOnlyList<PassoDoRoteiro> Roteiro { get; init; } = [];
    public int RodadasDeRevisaoMaximas { get; init; }
    public IReadOnlyList<string> Repositorios { get; init; } = [];
    public int? DuracaoMaximaEmMinutos { get; init; }
}
