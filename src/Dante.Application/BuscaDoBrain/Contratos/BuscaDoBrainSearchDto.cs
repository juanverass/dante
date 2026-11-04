using Dante.Domain.Conhecimentos;
using Dante.Domain.CapturaDeConhecimento;
namespace Dante.Application.BuscaDoBrain;

public sealed record BuscaDoBrainSearchDto
{
    public string Texto { get; init; } = "";
    // Texto integral de onde Texto foi derivado (ex.: termos em OR da mensagem). Na busca híbrida,
    // correspondência lexical só parcial de Texto exige apoio semântico; a que casa todos os termos
    // de TextoIntegral entra sempre (#280). Ignorado no modo lexical.
    public string? TextoIntegral { get; init; }
    public Guid? IdConhecimento { get; init; }
    public NaturezaDoConteudo? NaturezaDeOrigem { get; init; }
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
