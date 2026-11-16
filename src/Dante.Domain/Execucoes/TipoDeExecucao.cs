namespace Dante.Domain.Execucoes;

// Avulsa é o job one-shot legado (AD-06) espelhado no ledger. Os tipos de execução nova (Tarefa, Roteiro...) entram
// com as entregas que os criam (Epic #296).
public enum TipoDeExecucao
{
    Avulsa
}
