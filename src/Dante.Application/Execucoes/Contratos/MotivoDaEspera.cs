namespace Dante.Application.Execucoes;

// Por que uma execução não terminal não está rodando (#305). Sem posição numérica na fila: com SKIP LOCKED, backoff e
// reivindicações concorrentes, a ordem de reivindicação não é uma posição estável.
public enum MotivoDaEspera
{
    // Trabalho pendente e disponível: será reivindicado quando houver slot.
    NaFila,
    // Trabalho pendente com disponibilidade futura depois de uma tentativa (backoff ou devolução).
    AguardandoNovaTentativa,
    // Interrompida: só volta por confirmação explícita; pode ser cancelada.
    Interrompida,
    // Bloqueada: resultado incerto, exige conferência humana; pode ser cancelada.
    Bloqueada,
    AguardandoAprovacao,
    AguardandoEntrada,
    AguardandoDelegacoes
}
