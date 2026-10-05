namespace Dante.Application.Comum;

public sealed class ConflitoDeConcorrenciaException() : Exception(
    "Os dados foram alterados por outra operação. Recarregue antes de tentar novamente.");
