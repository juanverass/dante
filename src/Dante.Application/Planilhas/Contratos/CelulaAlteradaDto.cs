namespace Dante.Application.Planilhas;

public sealed record CelulaAlteradaDto
{
    public string Aba { get; init; } = string.Empty;
    public string Endereco { get; init; } = string.Empty;
    public string? ValorAnterior { get; init; }
    public string? FormulaAnterior { get; init; }
    public string ValorNovo { get; init; } = string.Empty;
}
