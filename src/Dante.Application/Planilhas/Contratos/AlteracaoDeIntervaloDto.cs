namespace Dante.Application.Planilhas;

// Valores de um retângulo exato, linha a linha. ValoresEsperados, quando informado, tem o mesmo formato e é comparado
// com o valor exibido atual de cada célula antes de escrever: divergência recusa toda a escrita.
public sealed record AlteracaoDeIntervaloDto
{
    public string Intervalo { get; init; } = string.Empty;
    public IReadOnlyList<IReadOnlyList<ValorDeCelula>> Valores { get; init; } = [];
    public IReadOnlyList<IReadOnlyList<string?>>? ValoresEsperados { get; init; }
}
