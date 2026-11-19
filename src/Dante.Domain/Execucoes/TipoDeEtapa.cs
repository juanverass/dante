namespace Dante.Domain.Execucoes;

// ProcessoAvulso: um processo one-shot (o job legado). TurnoDeAgente: turnos numa sessão de agente. AprovacaoHumana: o
// gate do roteiro (#308): nenhum processo, só a decisão de uma pessoa, persistida. UnidadeDoPlano: uma unidade do plano
// aprovado de uma Coordenada (AD-75, #310); também não roda processo próprio: é executada por uma delegação somente
// leitura ao participante do papel, e as dependências entre unidades são as DependenciaDeEtapa da execução.
public enum TipoDeEtapa
{
    ProcessoAvulso,
    TurnoDeAgente,
    AprovacaoHumana,
    UnidadeDoPlano
}
