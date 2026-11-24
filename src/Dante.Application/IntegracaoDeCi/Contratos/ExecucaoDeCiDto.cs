namespace Dante.Application.IntegracaoDeCi;

// Uma tentativa de execução de um workflow, como o provedor a descreve. Gatilho é o evento neutro que a disparou
// (push, PR, manual...), só para exibição; NumerosDePr são os PRs que o provedor associa à execução e podem vir vazios
// (o provedor não documenta quando): a correlação é pelo SHA da identidade e, na consulta de um PR, também por essa
// associação (#238). Estado indefinido é recusado, também por `with`: nunca chega à consolidação.
public sealed record ExecucaoDeCiDto
{
    private readonly EstadoDaExecucao estado;

    public required IdentidadeDaExecucaoDeCi Identidade { get; init; }
    public required string NomeDoWorkflow { get; init; }
    public required string Gatilho { get; init; }
    public required EstadoDaExecucao Estado { get => estado; init => estado = CiValidator.ExigirDefinido(value, nameof(Estado)); }
    public required Uri UrlOficial { get; init; }
    public required DateTimeOffset CriadaEm { get; init; }
    public required DateTimeOffset AtualizadaEm { get; init; }
    public IReadOnlyList<int> NumerosDePr { get; init; } = [];
    public IReadOnlyList<JobDeCiDto> Jobs { get; init; } = [];
}
