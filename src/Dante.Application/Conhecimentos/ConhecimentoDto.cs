using Dante.Domain.Conhecimentos;
using Dante.Domain.CapturaDeConhecimento;

namespace Dante.Application.Conhecimentos;

// Id, autor original, timestamps, histórico e substituto são somente saída. Revisao é esperada na correção.
public sealed record ConhecimentoDto
{
    public Guid Id { get; init; }
    public Guid IdEspacoDeConhecimento { get; init; }
    public Guid? IdProjeto { get; init; }
    // Somente saída; criação usa Natureza, correção nunca copia a origem enviada.
    public NaturezaDoConteudo NaturezaDeOrigem { get; init; } = NaturezaDoConteudo.Desconhecida;
    public Guid IdAutor { get; init; }
    public NaturezaDoConteudo? Natureza { get; init; }
    public string Rotulo => RotulagemDoConhecimento.Rotular(NaturezaDeOrigem, Natureza, Status);
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
