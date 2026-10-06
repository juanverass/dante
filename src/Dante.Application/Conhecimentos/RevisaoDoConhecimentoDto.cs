using Dante.Domain.Conhecimentos;

namespace Dante.Application.Conhecimentos;

public sealed record RevisaoDoConhecimentoDto
{
    public int Numero { get; init; }
    public TipoDeConhecimento Tipo { get; init; }
    public string? Conteudo { get; init; }
    public string? DadosEstruturados { get; init; }
    public StatusDoConhecimento Status { get; init; }
    public double? Confianca { get; init; }
    public Sensibilidade Sensibilidade { get; init; }
    public DateTimeOffset? ValidoDesde { get; init; }
    public DateTimeOffset? ValidoAte { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public ProvenienciaDto Proveniencia { get; init; } = new();
    public DateTimeOffset RegistradaEm { get; init; }
    public Guid? IdConhecimentoSubstituto { get; init; }
}
