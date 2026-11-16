namespace Dante.Domain.Execucoes;

// Etapa de escrita de execução nova sem worktree reservada (AD-68). Até as worktrees existirem, toda execução nova é
// somente leitura: a etapa de escrita é recusada ao ser criada e, de novo, por quem a executaria.
public sealed class EscritaSemWorktreeException() : InvalidOperationException(
    "Execução nova não escreve sem worktree reservada; esta entrega só executa tarefas de leitura.");
