namespace Dante.Application.MetricasDoBrain;

// Métricas locais da continuidade (#148): contagens, estimativas e IDs de escopo/sessão, nunca conteúdo.
// Campo nulo é dado indisponível ou não avaliado, nunca zero presumido.
public sealed record RetomadaMedidaDto(string IdSessao, string? Agente, int TokensDoBrain, int TokensDeReferencia, double Razao,
    MetricaDoBrainDto? Avaliacao);
