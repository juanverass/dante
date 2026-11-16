using Dante.Domain.Execucoes;

namespace Dante.Application.Execucoes;

public sealed record EventoDeOrquestracaoDto
{
    public long Sequencia { get; init; }
    public TipoDeEventoDeOrquestracao Tipo { get; init; }
    public Guid? IdExecucao { get; init; }
    public Guid? IdEtapa { get; init; }
    public Guid? IdSessaoDeAgente { get; init; }
    public string? IdTurno { get; init; }
    public string? Dados { get; init; }
    public DateTimeOffset OcorridoEm { get; init; }
}
