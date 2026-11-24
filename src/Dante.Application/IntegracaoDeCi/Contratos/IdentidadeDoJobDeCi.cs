namespace Dante.Application.IntegracaoDeCi;

// Job de uma tentativa específica: o mesmo nome de job em outra tentativa é outro job, com outro ID.
public sealed record IdentidadeDoJobDeCi
{
    public IdentidadeDoJobDeCi(IdentidadeDaExecucaoDeCi execucao, long idDoJob)
    {
        Execucao = execucao ?? throw new ArgumentNullException(nameof(execucao));
        IdDoJob = CiValidator.ExigirPositivo(idDoJob, nameof(idDoJob));
    }

    public IdentidadeDaExecucaoDeCi Execucao { get; }
    public long IdDoJob { get; }
}
