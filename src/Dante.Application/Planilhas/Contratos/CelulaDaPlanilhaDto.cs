namespace Dante.Application.Planilhas;

// Uma célula com coordenadas, para que o agente raciocine sobre o conteúdo e a escrita final seja exata. Mesclagem é o
// intervalo mesclado ancorado nesta célula (o valor de uma mesclagem vive só na célula superior esquerda). ValorBruto
// é nulo quando a leitura trouxe só valores exibidos (busca).
public sealed record CelulaDaPlanilhaDto
{
    public string Endereco { get; init; } = string.Empty;
    public int Linha { get; init; }
    public int Coluna { get; init; }
    public string? ValorExibido { get; init; }
    public ValorDeCelula? ValorBruto { get; init; }
    public string? Formula { get; init; }
    public string? Mesclagem { get; init; }

    public bool EstaVazia => string.IsNullOrEmpty(ValorExibido) && (ValorBruto?.EstaVazio ?? true) && Formula is null;
}
