using Dante.Application.SegurancaDoBrain;
namespace Dante.Application.BuscaDoBrain;

public sealed record ResultadoDaBuscaDto(LeituraProtegidaDto Item, OrigemDoResultado Origem, double Score,
    double ScoreLexical, double? ScoreSemantico, string Explicacao, Dante.Application.DocumentosFonte.TrechoDaFonteDto? Fonte = null);
