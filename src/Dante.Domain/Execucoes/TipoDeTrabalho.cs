namespace Dante.Domain.Execucoes;

// O que um trabalho da fila durável pede ao processador (AD-67). AvancarExecucao executa a próxima etapa pronta de
// uma execução, ou aplica a política de retomada quando a tentativa anterior foi interrompida. Timers (expiração de
// aprovação, entrega de evento) entram com as entregas que os criam.
public enum TipoDeTrabalho
{
    AvancarExecucao
}
