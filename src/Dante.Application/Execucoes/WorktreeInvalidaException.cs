namespace Dante.Application.Execucoes;

// Recusa de validação ou de criação de worktree (AD-71): nada é entregue ao agente e nada é apagado.
public sealed class WorktreeInvalidaException(string motivo) : InvalidOperationException(motivo);
