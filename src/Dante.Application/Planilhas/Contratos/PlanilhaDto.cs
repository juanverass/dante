namespace Dante.Application.Planilhas;

public sealed record PlanilhaDto
{
    public string IdDaPlanilha { get; init; } = string.Empty;
    public string Titulo { get; init; } = string.Empty;
    public string? Localidade { get; init; }
    public string? FusoHorario { get; init; }
    public string? Url { get; init; }
    public string? Observacao { get; init; }
    public IReadOnlyList<AbaDaPlanilhaDto> Abas { get; init; } = [];
}
