namespace Dante.Application.MetricasDoBrain;

// Métricas locais da continuidade (#148): contagens, estimativas e IDs de escopo/sessão, nunca conteúdo.
// Campo nulo é dado indisponível ou não avaliado, nunca zero presumido.
public sealed record ResumoDeMetricasDto(int Sessoes, int Envios, int EnviosIniciais, int Turnos, int Recuperados, int Selecionados,
    int Injetados, int Descartados, double TokensMediosDoPacote, int TokensMaximosDoPacote, double? TokensMediosDoSnapshot,
    int? ConhecimentosNoEscopo, long? CaracteresNoEscopo, double CaracteresMediosDoPacote, long? EntradaReportada, long? SaidaReportada,
    int TurnosSemUsoReportado, IReadOnlyList<RetomadaMedidaDto> Retomadas, double? Razao, string Indicacao, IReadOnlyList<string> AlertasDeQualidade,
    double? Precisao);
