namespace Dante.Application.IntegracaoDeCi;

// Estado indefinido é recusado na fronteira, também por `with`: nunca chega à consolidação.
public sealed record JobDeCiDto
{
    private readonly EstadoDaExecucao estado;

    public required long IdDoJob { get; init; }
    public required string Nome { get; init; }
    public required EstadoDaExecucao Estado { get => estado; init => estado = CiValidator.ExigirDefinido(value, nameof(Estado)); }
    public required Uri UrlOficial { get; init; }
    public DateTimeOffset? IniciadoEm { get; init; }
    public DateTimeOffset? ConcluidoEm { get; init; }
    public IReadOnlyList<EtapaDeJobDeCiDto> Etapas { get; init; } = [];
}
