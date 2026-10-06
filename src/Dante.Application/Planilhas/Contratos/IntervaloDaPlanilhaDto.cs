namespace Dante.Application.Planilhas;

// Leitura de um retângulo: só as células não vazias, em ordem de linha e coluna, e as mesclagens que o tocam.
// Truncado indica que o limite de células interrompeu a leitura; o agente lê uma região menor ou a seguinte.
public sealed record IntervaloDaPlanilhaDto
{
    public string Aba { get; init; } = string.Empty;
    public string Intervalo { get; init; } = string.Empty;
    public IReadOnlyList<CelulaDaPlanilhaDto> Celulas { get; init; } = [];
    public IReadOnlyList<string> Mesclagens { get; init; } = [];
    public bool Truncado { get; init; }
}
