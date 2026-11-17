namespace Dante.Application.Execucoes;

// Limite global ou por repositório atingido na reserva (#307): nada foi gravado. Não é falha da execução; quem tem a
// posse devolve o trabalho à fila sem contar tentativa e tenta de novo mais tarde.
public sealed class CapacidadeDeWorktreesIndisponivelException(string motivo) : InvalidOperationException(motivo);
