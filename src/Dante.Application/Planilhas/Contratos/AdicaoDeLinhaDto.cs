namespace Dante.Application.Planilhas;

// Acrescenta uma linha logo após a tabela contida em Intervalo (por exemplo Gastos!A1:D1 ou Gastos!A:D já lida),
// sem inserir linhas na grade nem tocar outras células.
public sealed record AdicaoDeLinhaDto
{
    public string Planilha { get; init; } = string.Empty;
    public string Intervalo { get; init; } = string.Empty;
    public IReadOnlyList<ValorDeCelula> Valores { get; init; } = [];
}
