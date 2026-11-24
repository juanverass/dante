namespace Dante.Application.IntegracaoDeCi;

// Identidade imutável de uma tentativa de execução: repositório, workflow, execução (run), tentativa e o SHA que ela
// testou. Uma reexecução é outra tentativa da mesma execução, com estados próprios; nunca a continuação da anterior.
public sealed record IdentidadeDaExecucaoDeCi
{
    public IdentidadeDaExecucaoDeCi(RepositorioDeCi repositorio, long idDoWorkflow, long idDaExecucao, int tentativa,
        string sha)
    {
        Repositorio = repositorio ?? throw new ArgumentNullException(nameof(repositorio));
        if (repositorio.IdNoProvedor is null)
            throw new ArgumentException("Execução devolvida pelo provedor precisa do ID do repositório.", nameof(repositorio));
        IdDoWorkflow = CiValidator.ExigirPositivo(idDoWorkflow, nameof(idDoWorkflow));
        IdDaExecucao = CiValidator.ExigirPositivo(idDaExecucao, nameof(idDaExecucao));
        Tentativa = tentativa > 0 ? tentativa : throw new ArgumentOutOfRangeException(nameof(tentativa), "Tentativa começa em 1.");
        Sha = CiValidator.ExigirSha(sha);
    }

    public RepositorioDeCi Repositorio { get; }
    public long IdDoWorkflow { get; }
    public long IdDaExecucao { get; }
    public int Tentativa { get; }
    public string Sha { get; }
}
