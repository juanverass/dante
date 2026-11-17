using Dante.Domain.Agentes;
using Dante.Domain.Execucoes;

namespace Dante.Application.Execucoes;

public sealed record SessaoDeAgenteDto
{
    public Guid Id { get; init; }
    public string Codigo { get; init; } = "";
    public long Numero { get; init; }
    public AgentKind Agente { get; init; }
    public EstadoDaSessaoDeAgente Estado { get; init; }
    public string Canal { get; init; } = "";
    public long IdDoDono { get; init; }
    public string Contexto { get; init; } = "";
    public AgentPermissionProfile Perfil { get; init; }
    public string? Modelo { get; init; }
    public string? Esforco { get; init; }
    public string? ModeloInformado { get; init; }
    public string? IdUpstream { get; init; }
    public Guid? IdExecucao { get; init; }
    public Guid? BootDoWorker { get; init; }
    public string? MaquinaDoWorker { get; init; }
    public string? UnidadeDeContencao { get; init; }
    public string? HandleDoAgente { get; init; }
    public string? HashDaConfiguracaoDoAgente { get; init; }
    public Guid? IdVersaoDoAgente { get; init; }
    public string? MotivoDoFim { get; init; }
    public DateTimeOffset CriadaEm { get; init; }
    public DateTimeOffset? IniciadaEm { get; init; }
    public DateTimeOffset? EncerradaEm { get; init; }
    public IReadOnlyList<EventoDeOrquestracaoDto> Eventos { get; init; } = [];
}
