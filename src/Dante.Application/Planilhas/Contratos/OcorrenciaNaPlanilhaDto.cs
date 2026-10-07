namespace Dante.Application.Planilhas;

// Uma célula encontrada pela busca e as demais células não vazias da mesma linha, para o agente reconhecer a região
// candidata sem ler a aba inteira.
public sealed record OcorrenciaNaPlanilhaDto
{
    public string Aba { get; init; } = string.Empty;
    public CelulaDaPlanilhaDto Celula { get; init; } = new();
    public IReadOnlyList<CelulaDaPlanilhaDto> Linha { get; init; } = [];
}
