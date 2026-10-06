namespace Dante.Application.MetricasDoBrain;

// Métricas locais da continuidade (#148): contagens, estimativas e IDs de escopo/sessão, nunca conteúdo.
// Campo nulo é dado indisponível ou não avaliado, nunca zero presumido.
public sealed record MetricaDoBrainDto
{
    public const string Envio="envio", Turno="turno", Avaliacao="avaliacao";
    public string Tipo { get; init; } = "";
    public DateTimeOffset Em { get; init; }
    public Guid IdTenant { get; init; }
    public Guid IdUsuario { get; init; }
    public Guid IdEspacoDeConhecimento { get; init; }
    public Guid? IdProjeto { get; init; }
    public string IdSessao { get; init; } = "";
    public string? Agente { get; init; }
    // Envio: pacote aceito pela sessão e mensagem do usuário sem o pacote.
    public bool? Bootstrap { get; init; }
    public int? Recuperados { get; init; }
    public int? Selecionados { get; init; }
    public int? Injetados { get; init; }
    public int? Descartados { get; init; }
    public int? TokensDoPacote { get; init; }
    public int? TokensDoSnapshot { get; init; }
    public int? CaracteresDoPacote { get; init; }
    public int? TokensDoPedido { get; init; }
    public int? ConhecimentosNoEscopo { get; init; }
    public long? CaracteresNoEscopo { get; init; }
    // Turno: resposta do agente e uso informado pela CLI.
    public string? Resultado { get; init; }
    public int? TokensDaResposta { get; init; }
    public long? EntradaReportada { get; init; }
    public long? SaidaReportada { get; init; }
    public long? EntradaEmCache { get; init; }
    // Avaliação humana da retomada.
    public int? Repeticoes { get; init; }
    public int? Esclarecimentos { get; init; }
    public bool? Concluida { get; init; }
    public bool? BuscouContextoAdicional { get; init; }
    public int? Incorretos { get; init; }
    public int? Irrelevantes { get; init; }
    public int? Relevantes { get; init; }
}
public interface IRegistroDeMetricasDoBrain
{
    Task RegistrarAsync(MetricaDoBrainDto metrica, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MetricaDoBrainDto>> ListarAsync(Guid idTenant, Guid idUsuario, Guid idEspaco, Guid? idProjeto,
        CancellationToken cancellationToken = default);
}
public sealed record RetomadaMedidaDto(string IdSessao, string? Agente, int TokensDoBrain, int TokensDeReferencia, double Razao,
    MetricaDoBrainDto? Avaliacao);
public sealed record ResumoDeMetricasDto(int Sessoes, int Envios, int EnviosIniciais, int Turnos, int Recuperados, int Selecionados,
    int Injetados, int Descartados, double TokensMediosDoPacote, int TokensMaximosDoPacote, double? TokensMediosDoSnapshot,
    int? ConhecimentosNoEscopo, long? CaracteresNoEscopo, double CaracteresMediosDoPacote, long? EntradaReportada, long? SaidaReportada,
    int TurnosSemUsoReportado, IReadOnlyList<RetomadaMedidaDto> Retomadas, double? Razao, string Indicacao, IReadOnlyList<string> AlertasDeQualidade,
    double? Precisao);
