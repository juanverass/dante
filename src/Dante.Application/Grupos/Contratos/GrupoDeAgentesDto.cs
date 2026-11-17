using Dante.Domain.Grupos;

namespace Dante.Application.Grupos;

public sealed record GrupoDeAgentesDto
{
    public Guid Id { get; init; }
    public string Nome { get; init; } = "";
    public string? Descricao { get; init; }
    public EstadoDoGrupo Estado { get; init; }
    public int NumeroDaVersaoAtual { get; init; }
    public int? NumeroDaVersaoAtiva { get; init; }
    public DateTimeOffset CriadoEm { get; init; }
    public DateTimeOffset AtualizadoEm { get; init; }
    public VersaoDoGrupoDto? VersaoAtual { get; init; }
    public VersaoDoGrupoDto? VersaoAtiva { get; init; }
    // Preenchidos só na consulta detalhada (ObterAsync), da mais recente para a mais antiga.
    public IReadOnlyList<VersaoDoGrupoDto> Versoes { get; init; } = [];
    public IReadOnlyList<AtivacaoDto> Ativacoes { get; init; } = [];
}
