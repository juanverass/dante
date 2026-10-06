namespace Dante.Application.Planilhas;

public sealed record ResultadoDaEscritaDto
{
    public string Planilha { get; init; } = string.Empty;
    public IReadOnlyList<string> Intervalos { get; init; } = [];
    public IReadOnlyList<CelulaAlteradaDto> Celulas { get; init; } = [];
}
