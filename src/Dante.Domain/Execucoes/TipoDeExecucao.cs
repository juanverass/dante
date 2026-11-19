namespace Dante.Domain.Execucoes;

// Avulsa é o job one-shot legado (AD-06) espelhado no ledger. Tarefa é o pedido explícito a um agente registrado
// (AD-68), com escrita só em worktree reservada (AD-71). Roteiro é a tarefa de um grupo de agentes executada pelo
// orquestrador numa ordem fixa (AD-69, #308). Delegada é a execução subordinada de uma delegação (AD-70, #309): nasce
// junto com a linha da delegação, filha da execução de origem, e só de leitura nesta etapa. Coordenada é a tarefa de um
// grupo no modo coordenador (AD-75, #310): aponta para a versão do grupo e para a versão congelada do coordenador, e cada
// unidade do plano aprovado vira uma delegação somente leitura. Os demais tipos de execução nova entram com as entregas
// que os criam.
public enum TipoDeExecucao
{
    Avulsa,
    Tarefa,
    Roteiro,
    Delegada,
    Coordenada
}
