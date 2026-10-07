namespace Dante.Application.Planilhas;

public sealed record ResultadoDaBuscaNaPlanilhaDto
{
    public string Termo { get; init; } = string.Empty;
    public IReadOnlyList<string> AbasConsultadas { get; init; } = [];
    public IReadOnlyList<OcorrenciaNaPlanilhaDto> Ocorrencias { get; init; } = [];
    public bool Truncado { get; init; }
    public string? Observacao { get; init; }
}
