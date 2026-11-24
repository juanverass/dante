namespace Dante.Application.IntegracaoDeCi;

// Uma tentativa de execução de um workflow, como o provedor a descreve. Gatilho é o evento neutro que a disparou
// (push, PR, manual...), só para exibição; NumerosDePr são os PRs que o provedor associa à execução e podem vir vazios
// (PR de fork, por exemplo): a correlação é sempre pelo SHA da identidade.
public sealed record ExecucaoDeCiDto
{
    public required IdentidadeDaExecucaoDeCi Identidade { get; init; }
    public required string NomeDoWorkflow { get; init; }
    public required string Gatilho { get; init; }
    public required EstadoDaExecucao Estado { get; init; }
    public required Uri UrlOficial { get; init; }
    public required DateTimeOffset CriadaEm { get; init; }
    public required DateTimeOffset AtualizadaEm { get; init; }
    public IReadOnlyList<int> NumerosDePr { get; init; } = [];
    public IReadOnlyList<JobDeCiDto> Jobs { get; init; } = [];
}
