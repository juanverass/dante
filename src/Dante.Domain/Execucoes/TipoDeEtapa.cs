namespace Dante.Domain.Execucoes;

// ProcessoAvulso: um processo one-shot (o job legado). TurnoDeAgente: turnos numa sessão de agente. AprovacaoHumana: o
// gate do roteiro (#308): nenhum processo, só a decisão de uma pessoa, persistida.
public enum TipoDeEtapa
{
    ProcessoAvulso,
    TurnoDeAgente,
    AprovacaoHumana
}
