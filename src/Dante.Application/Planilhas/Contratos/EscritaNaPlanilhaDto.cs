namespace Dante.Application.Planilhas;

// Escrita em lote, aplicada de uma vez ou recusada inteira. Células com fórmula só são sobrescritas com
// PermitirSobrescreverFormulas, e nunca em massa.
public sealed record EscritaNaPlanilhaDto
{
    public string Planilha { get; init; } = string.Empty;
    public IReadOnlyList<AlteracaoDeIntervaloDto> Alteracoes { get; init; } = [];
    public bool PermitirSobrescreverFormulas { get; init; }
}
