using Dante.Domain.Execucoes;

namespace Dante.Application.Execucoes;

// Resultado da política de retomada (AD-67). NovoEstado: Pendente (retomada automática, leitura idempotente),
// Interrompida (escrita: só com confirmação explícita), Bloqueada (ação externa de resultado incerto: nunca repetida
// automaticamente), Cancelada (a execução nem chegou a iniciar e este processo não pode executá-la) ou Falhou
// (tentativas da fila esgotadas numa etapa que não iniciou ou de retomada automática).
public sealed record ExecucaoInterrompidaDto(AlvoDoTrabalhoDto Alvo, EstadoDaExecucao NovoEstado, string Motivo);
