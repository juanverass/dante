using Dante.Domain.Conhecimentos;

namespace Dante.Application.Conhecimentos;

// Id, autor original, timestamps, histórico e substituto são somente saída. Revisao é esperada na correção.
public sealed record ConhecimentoDto
{
    public Guid Id { get; init; }
    public Guid IdEspacoDeConhecimento { get; init; }
    public Guid? IdProjeto { get; init; }
    public Guid IdAutor { get; init; }
    public TipoDeConhecimento Tipo { get; init; }
    public string? Conteudo { get; init; }
    public string? DadosEstruturados { get; init; }
    public StatusDoConhecimento Status { get; init; } = StatusDoConhecimento.Inferido;
    public double? Confianca { get; init; }
    public Sensibilidade Sensibilidade { get; init; } = Sensibilidade.Pessoal;
    public DateTimeOffset CriadoEm { get; init; }
    public DateTimeOffset AtualizadoEm { get; init; }
    public DateTimeOffset? ValidoDesde { get; init; }
    public DateTimeOffset? ValidoAte { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public ProvenienciaDto Proveniencia { get; init; } = new();
    public int Revisao { get; init; }
    public IReadOnlyList<RevisaoDoConhecimentoDto> Historico { get; init; } = [];
    public Guid? IdConhecimentoSubstituto { get; init; }
}
