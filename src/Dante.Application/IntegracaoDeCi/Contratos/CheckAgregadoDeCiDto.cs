namespace Dante.Application.IntegracaoDeCi;

// O check agregado de um head SHA, produzido só pela ConsolidacaoDoCheckDeCi. Execucoes traz apenas a última tentativa
// da execução mais recente de cada workflow e gatilho para o SHA consultado; Descartadas conta as execuções de outro
// SHA, as tentativas anteriores e as execuções substituídas, que nunca entram no estado nem no diagnóstico.
public sealed record CheckAgregadoDeCiDto
{
    public required ConsultaDeCi Consulta { get; init; }
    public required EstadoDoCheckDeCi Estado { get; init; }
    public required DateTimeOffset ObtidoEm { get; init; }
    public IReadOnlyList<ExecucaoDeCiDto> Execucoes { get; init; } = [];
    public int Descartadas { get; init; }
    public bool Completo { get; init; } = true;
    public MotivoDaFalhaNaConsultaDeCi? MotivoDaIndisponibilidade { get; init; }
}
