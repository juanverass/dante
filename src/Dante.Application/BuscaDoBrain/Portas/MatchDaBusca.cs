namespace Dante.Application.BuscaDoBrain;

public sealed record MatchDaBusca(Guid Id, int Revisao, double Score, double ScoreLexical, double? ScoreSemantico);
