namespace Dante.Domain.Execucoes;

// Avulsa é o job one-shot legado (AD-06) espelhado no ledger. Tarefa é o pedido explícito a um agente registrado
// (AD-68), somente leitura até existir reserva de worktree. Os demais tipos de execução nova (Roteiro...) entram com as
// entregas que os criam (Epic #296).
public enum TipoDeExecucao
{
    Avulsa,
    Tarefa
}
