namespace Dante.Application.IntegracaoDeCi;

// O check agregado de um head SHA, produzido só pela ConsolidacaoDoCheckDeCi. Execucoes traz apenas a última tentativa
// da execução mais recente de cada workflow e gatilho para o SHA consultado; Descartadas conta as execuções de outro
// SHA, as tentativas anteriores, as execuções substituídas e, na consulta de um PR, as associadas só a outro PR, que
// nunca entram no estado nem no diagnóstico. SemAssociacaoAoPr conta, na consulta de um PR, as execuções do SHA que o
// provedor não associa a PR nenhum: também não entram, mas são informadas (#238).
public sealed record CheckAgregadoDeCiDto
{
    public required ConsultaDeCi Consulta { get; init; }
    public required EstadoDoCheckDeCi Estado { get; init; }
    public required DateTimeOffset ObtidoEm { get; init; }
    public IReadOnlyList<ExecucaoDeCiDto> Execucoes { get; init; } = [];
    public int Descartadas { get; init; }
    public int SemAssociacaoAoPr { get; init; }
    public bool Completo { get; init; } = true;
    public MotivoDaFalhaNaConsultaDeCi? MotivoDaIndisponibilidade { get; init; }
}
