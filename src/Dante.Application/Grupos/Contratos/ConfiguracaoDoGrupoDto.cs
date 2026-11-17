using Dante.Domain.Grupos;

namespace Dante.Application.Grupos;

// Configuração completa de uma versão nova de grupo. O modo é sempre Roteiro nesta etapa.
public sealed record ConfiguracaoDoGrupoDto
{
    public IReadOnlyList<ParticipanteInformadoDto> Participantes { get; init; } = [];
    public IReadOnlyList<PassoDoRoteiro> Roteiro { get; init; } = [];
    public int RodadasDeRevisaoMaximas { get; init; }
    public IReadOnlyList<string> Repositorios { get; init; } = [];
    public int? DuracaoMaximaEmMinutos { get; init; }
}
