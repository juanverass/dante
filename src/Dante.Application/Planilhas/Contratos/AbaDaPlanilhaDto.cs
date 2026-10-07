namespace Dante.Application.Planilhas;

// Linhas/Colunas são a grade da aba; AreaUsada (A1:F37) é o retângulo a partir de A1 que contém dados, quando
// calculada. Mesclagens lista os intervalos mesclados da aba em A1.
public sealed record AbaDaPlanilhaDto
{
    public long IdDaAba { get; init; }
    public string Titulo { get; init; } = string.Empty;
    public int Indice { get; init; }
    public string Tipo { get; init; } = "GRID";
    public int Linhas { get; init; }
    public int Colunas { get; init; }
    public string? AreaUsada { get; init; }
    public IReadOnlyList<string> Mesclagens { get; init; } = [];
}
