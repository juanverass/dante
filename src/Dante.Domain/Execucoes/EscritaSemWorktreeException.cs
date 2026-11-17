namespace Dante.Domain.Execucoes;

// Etapa de escrita de execução nova sem worktree reservada, pronta e da própria execução (AD-68, AD-71). Quem executa
// recusa antes de qualquer processo; o checkout principal nunca é usado como alternativa.
public sealed class EscritaSemWorktreeException(string? detalhe = null) : InvalidOperationException(
    "Execução nova não escreve sem worktree reservada e validada" + (detalhe is null ? "." : $": {detalhe}"));
