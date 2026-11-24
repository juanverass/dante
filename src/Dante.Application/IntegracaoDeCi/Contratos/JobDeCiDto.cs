namespace Dante.Application.IntegracaoDeCi;

public sealed record JobDeCiDto
{
    public required long IdDoJob { get; init; }
    public required string Nome { get; init; }
    public required EstadoDaExecucao Estado { get; init; }
    public required Uri UrlOficial { get; init; }
    public DateTimeOffset? IniciadoEm { get; init; }
    public DateTimeOffset? ConcluidoEm { get; init; }
    public IReadOnlyList<EtapaDeJobDeCiDto> Etapas { get; init; } = [];
}
