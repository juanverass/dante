namespace Dante.Application.Planilhas;

public sealed record ValoresParaEscrita(IntervaloA1 Intervalo, IReadOnlyList<IReadOnlyList<ValorDeCelula>> Valores);
