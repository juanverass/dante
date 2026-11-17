using Dante.Domain.Execucoes;

namespace Dante.Application.Execucoes;

// Linha de uma listagem de execuções do proprietário (#305): só fatos, nada de diretório, pedido ou saída.
// Agente: "handle vN" para execução de agente registrado, ou o driver (claude/codex) do job legado.
// Espera: nulo quando a execução está rodando ou encerrada.
public sealed record ResumoDaExecucaoDto
{
    public string Codigo { get; init; } = "";
    public long Numero { get; init; }
    public TipoDeExecucao Tipo { get; init; }
    public EstadoDaExecucao Estado { get; init; }
    public bool Terminal { get; init; }
    public string? Agente { get; init; }
    public int? EtapaAtual { get; init; }
    public int TotalDeEtapas { get; init; }
    public EstadoDaEtapa? EstadoDaEtapaAtual { get; init; }
    public int? Tentativa { get; init; }
    public MotivoDaEspera? Espera { get; init; }
    public bool CancelamentoSolicitado { get; init; }
    public DateTimeOffset CriadaEm { get; init; }
    public DateTimeOffset? EncerradaEm { get; init; }
}
