namespace Dante.Application.BuscaDoBrain;

public sealed record BuscaDoBrainDto(IReadOnlyList<ResultadoDaBuscaDto> Resultados, string Modo,
    bool TemMais, string? ModeloEmbedding);
