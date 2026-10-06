using Dante.Domain.Conhecimentos;
namespace Dante.Application.BuscaDoBrain;

public sealed record BuscaDoBrainSearchDto
{
    public string Texto { get; init; } = "";
    public Guid? IdConhecimento { get; init; }
    public TipoDeConhecimento? Tipo { get; init; }
    public IReadOnlyList<TipoDeConhecimento> Tipos { get; init; } = [];
    public StatusDoConhecimento? Status { get; init; }
    public Sensibilidade? Sensibilidade { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public DateTimeOffset? CriadoDesde { get; init; }
    public DateTimeOffset? CriadoAte { get; init; }
    public DateTimeOffset? ValidoEm { get; init; }
    public int Limite { get; init; } = 20;
    public int Deslocamento { get; init; }
}
