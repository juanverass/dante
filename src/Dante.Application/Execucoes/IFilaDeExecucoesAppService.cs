namespace Dante.Application.Execucoes;

// Casos de uso da fila durável sobre o ledger (AD-67). Toda gravação feita pelo processador é sob posse: a linha do
// trabalho fica travada e a posse (geração, dono, lease) é conferida na mesma transação da mudança de estado; posse
// perdida vira PosseDoTrabalhoPerdidaException e nada é gravado. Nenhum método mantém transação aberta ao retornar.
public interface IFilaDeExecucoesAppService
{
    // Outbox do job legado: execução Avulsa, etapa, evento de criação e trabalho AvancarExecucao numa transação. A
    // chave de deduplicação é verificada antes; numa corrida, o salvamento falha inteiro com
    // ConflitoDeConcorrenciaException e o chamador consulta ObterJobPorChaveAsync num escopo novo.
    Task<JobNaFilaDto> EnfileirarJobAsync(JobRegistradoDto job, string chaveDeDeduplicacao,
        CancellationToken cancellationToken = default);

    Task<JobNaFilaDto?> ObterJobPorChaveAsync(string chaveDeDeduplicacao, CancellationToken cancellationToken = default);

    // Leitura: o que fazer com o trabalho reivindicado.
    Task<AvaliacaoDoTrabalhoDto> AvaliarAsync(TrabalhoReivindicado trabalho, CancellationToken cancellationToken = default);

    // Etapa e execução para EmExecucao (CAS por posse), antes de iniciar o processo. O trabalho continua reivindicado.
    Task IniciarEtapaAsync(PosseDoTrabalho posse, Guid idEtapa, CancellationToken cancellationToken = default);

    // Resultado da etapa, transição da execução, eventos, próximo trabalho (se houver próxima etapa) e fim do trabalho
    // atual numa única transação, antes de qualquer efeito seguinte (entrega da resposta).
    Task ConcluirEtapaAsync(PosseDoTrabalho posse, Guid idEtapa, ResultadoDaEtapaDto resultado,
        CancellationToken cancellationToken = default);

    // A tentativa anterior estava em execução quando a posse se perdeu: etapa e execução interrompidas, e a política
    // da etapa decide. Automatica: nova tentativa enfileirada; ComConfirmacao: fica Interrompida até
    // ConfirmarRetomadaAsync; Nunca: execução Bloqueada (resultado incerto), nunca repetida automaticamente.
    Task<ExecucaoInterrompidaDto> InterromperAsync(PosseDoTrabalho posse, AlvoDoTrabalhoDto alvo, string motivo,
        CancellationToken cancellationToken = default);

    // Execução que ainda não iniciou e que este processo não pode executar (o pedido do job só existia na memória de
    // um boot anterior): cancelada com o motivo, trabalho descartado.
    Task<ExecucaoInterrompidaDto> CancelarNaoIniciadaAsync(PosseDoTrabalho posse, AlvoDoTrabalhoDto alvo, string motivo,
        CancellationToken cancellationToken = default);

    Task DescartarAsync(PosseDoTrabalho posse, string motivo, CancellationToken cancellationToken = default);

    // Confirmação explícita de retomada de uma execução Interrompida ou Bloqueada: nova tentativa da etapa interrompida
    // e novo trabalho, com chave de deduplicação por tentativa (confirmar duas vezes não executa duas vezes).
    // Falso quando a execução não existe ou não está esperando confirmação.
    Task<bool> ConfirmarRetomadaAsync(long numero, CancellationToken cancellationToken = default);
}
