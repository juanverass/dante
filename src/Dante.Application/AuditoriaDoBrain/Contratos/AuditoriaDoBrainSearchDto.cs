using Dante.Domain.Conhecimentos;
namespace Dante.Application.AuditoriaDoBrain;

public sealed record AuditoriaDoBrainSearchDto
{
    public TipoDeConhecimento? Tipo { get; init; }
    public StatusDoConhecimento? Status { get; init; }
    public string? Origem { get; init; }
    public string? Tag { get; init; }
    public bool IncluirInativos { get; init; }
    public int Limite { get; init; } = 100;
    public int Deslocamento { get; init; }
}
