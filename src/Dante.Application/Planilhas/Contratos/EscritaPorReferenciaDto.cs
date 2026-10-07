namespace Dante.Application.Planilhas;

// Escrita relativa a uma célula de referência localizada por texto ("a linha da AWS", "o cliente X"): o alvo é a
// mesma linha (mais DeslocamentoDeLinhas) na Coluna informada, ou na coluna da referência mais DeslocamentoDeColunas.
// Mais de uma referência plausível recusa a escrita e devolve os candidatos: nunca se escolhe em silêncio.
public sealed record EscritaPorReferenciaDto
{
    public string Planilha { get; init; } = string.Empty;
    public string Referencia { get; init; } = string.Empty;
    public string? Aba { get; init; }
    public string? Intervalo { get; init; }
    public string? Coluna { get; init; }
    public int DeslocamentoDeColunas { get; init; }
    public int DeslocamentoDeLinhas { get; init; }
    public ValorDeCelula Valor { get; init; } = ValorDeCelula.Vazio;
    public string? ValorEsperado { get; init; }
    public bool PermitirSobrescreverFormulas { get; init; }
}
