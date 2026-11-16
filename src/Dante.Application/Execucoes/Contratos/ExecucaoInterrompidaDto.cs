using Dante.Domain.Execucoes;

namespace Dante.Application.Execucoes;

// Resultado da política de retomada (AD-67). NovoEstado: Pendente (retomada automática, leitura idempotente),
// Interrompida (escrita: só com confirmação explícita), Bloqueada (ação externa de resultado incerto: nunca repetida
// automaticamente) ou Cancelada (a execução nem chegou a iniciar e este processo não pode executá-la).
public sealed record ExecucaoInterrompidaDto(AlvoDoTrabalhoDto Alvo, EstadoDaExecucao NovoEstado, string Motivo);
