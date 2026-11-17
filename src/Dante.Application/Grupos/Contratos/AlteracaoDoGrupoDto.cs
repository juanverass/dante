using Dante.Domain.Grupos;

namespace Dante.Application.Grupos;

// Edição parcial: nulo herda da versão atual. Participantes informados substituem a lista inteira.
// DuracaoMaximaEmMinutos 0 remove o teto.
public sealed record AlteracaoDoGrupoDto
{
    public IReadOnlyList<ParticipanteInformadoDto>? Participantes { get; init; }
    public IReadOnlyList<PassoDoRoteiro>? Roteiro { get; init; }
    public int? RodadasDeRevisaoMaximas { get; init; }
    public IReadOnlyList<string>? Repositorios { get; init; }
    public int? DuracaoMaximaEmMinutos { get; init; }
}
