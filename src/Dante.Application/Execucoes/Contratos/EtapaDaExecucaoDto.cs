using Dante.Domain.Agentes;
using Dante.Domain.Execucoes;

namespace Dante.Application.Execucoes;

public sealed record EtapaDaExecucaoDto
{
    public Guid Id { get; init; }
    public int Numero { get; init; }
    public TipoDeEtapa Tipo { get; init; }
    public EstadoDaEtapa Estado { get; init; }
    public bool Escrita { get; init; }
    public PoliticaDeRetomada PoliticaDeRetomada { get; init; }
    public int Tentativa { get; init; }
    public string ChaveDeIdempotencia { get; init; } = "";
    public AgentKind? Agente { get; init; }
    public string? Modelo { get; init; }
    public string? Esforco { get; init; }
    public Guid? IdSessaoDeAgente { get; init; }
    public string? IdTurno { get; init; }
    public int? CodigoDeSaida { get; init; }
    public string? Erro { get; init; }
    public string? Motivo { get; init; }
    public DateTimeOffset CriadaEm { get; init; }
    public DateTimeOffset? IniciadaEm { get; init; }
    public DateTimeOffset? EncerradaEm { get; init; }
}
