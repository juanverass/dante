using Dante.Domain.Agentes;
using Dante.Domain.Execucoes;

namespace Dante.Application.Execucoes;

// Etapa no detalhe de uma execução (#305). Erro e motivo já redigidos e cortados; Sessao é o código S… da sessão de
// agente da etapa, quando houver.
public sealed record EtapaExibidaDto
{
    public int Numero { get; init; }
    public TipoDeEtapa Tipo { get; init; }
    public EstadoDaEtapa Estado { get; init; }
    public bool Escrita { get; init; }
    public int Tentativa { get; init; }
    public AgentKind? Agente { get; init; }
    public string? Modelo { get; init; }
    public string? Esforco { get; init; }
    public string? Sessao { get; init; }
    public int? CodigoDeSaida { get; init; }
    public string? Erro { get; init; }
    public string? Motivo { get; init; }
    public DateTimeOffset? IniciadaEm { get; init; }
    public DateTimeOffset? EncerradaEm { get; init; }
}
