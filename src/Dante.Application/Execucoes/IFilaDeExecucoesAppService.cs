namespace Dante.Application.Execucoes;

// Casos de uso da fila durável sobre o ledger (AD-67). Toda gravação feita pelo processador é sob posse: a linha do
// trabalho fica travada e a posse (geração, dono, lease) é conferida na mesma transação da mudança de estado; posse
// perdida vira PosseDoTrabalhoPerdidaException e nada é gravado. Nenhum método mantém transação aberta ao retornar.
public interface IFilaDeExecucoesAppService
{
    // Outbox do job legado: execução Avulsa, etapa, evento de criação e trabalho AvancarExecucao numa transação. A
    // chave de deduplicação é verificada antes; numa corrida, o salvamento falha inteiro com
    // ConflitoDeConcorrenciaException e o chamador consulta ObterJobPorChaveAsync num escopo novo.
    // origem: o processo que tem o pedido em memória; registrada no trabalho para outro processo não cancelar um job
    // cujo dono pode estar vivo.
    Task<JobNaFilaDto> EnfileirarJobAsync(JobRegistradoDto job, string chaveDeDeduplicacao,
        CancellationToken cancellationToken = default, IdentidadeDoProcessador? origem = null);

    Task<JobNaFilaDto?> ObterJobPorChaveAsync(string chaveDeDeduplicacao, CancellationToken cancellationToken = default);

    // Outbox da /tarefa (AD-68): execução Tarefa com a versão do agente congelada, etapa somente leitura
    // (MontagemDaTarefa.CriarExecucao), evento de criação e trabalho AvancarExecucao numa transação, com o número já
    // reservado. Mesma deduplicação de EnfileirarJobAsync: chave existente devolve a execução dela (Novo=false); numa
    // corrida, ConflitoDeConcorrenciaException e nada é criado. Plano com escrita é recusado antes de gravar.
    Task<ExecucaoNaFilaDto> EnfileirarTarefaAsync(Dante.Application.Agentes.PlanoDaTarefa plano, long numero, string canal,
        long idDoUsuarioNoCanal, string chaveDeDeduplicacao, CancellationToken cancellationToken = default,
        IdentidadeDoProcessador? origem = null);

    // Leitura: o que fazer com o trabalho reivindicado.
    Task<AvaliacaoDoTrabalhoDto> AvaliarAsync(TrabalhoReivindicado trabalho, CancellationToken cancellationToken = default);

    // Etapa e execução para EmExecucao (CAS por posse), antes de iniciar o processo. O trabalho continua reivindicado.
    Task IniciarEtapaAsync(PosseDoTrabalho posse, Guid idEtapa, CancellationToken cancellationToken = default);

    // Resultado da etapa, transição da execução, eventos, próximo trabalho (se houver próxima etapa) e fim do trabalho
    // atual numa única transação, antes de qualquer efeito seguinte (entrega da resposta). Com pedido de cancelamento
    // do dono registrado (#305), grava Cancelada no lugar do resultado. Devolve o estado final gravado da etapa.
    // Também IniciarEtapaAsync confere o pedido: com ele registrado, lança CancelamentoSolicitadoException sem gravar.
    Task<Dante.Domain.Execucoes.EstadoDaEtapa> ConcluirEtapaAsync(PosseDoTrabalho posse, Guid idEtapa,
        ResultadoDaEtapaDto resultado, CancellationToken cancellationToken = default);

    // Pedido de cancelamento do dono (#305) honrado por quem tem a posse: etapa não terminal e execução Cancelada com o
    // motivo do pedido, trabalho Descartado, numa transação sob posse. Execução já terminal: só o trabalho termina, e o
    // resultado volta com PorCancelamento=false (nenhuma transição gravada, nenhum aviso a dar).
    Task<ExecucaoInterrompidaDto> CancelarSolicitadaAsync(PosseDoTrabalho posse, AlvoDoTrabalhoDto alvo,
        CancellationToken cancellationToken = default);

    // Leitura: há pedido de cancelamento registrado para a execução? Conferida no heartbeat de quem tem a posse.
    Task<bool> CancelamentoSolicitadoAsync(Guid idExecucao, CancellationToken cancellationToken = default);

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

    // Tentativas do trabalho esgotadas (cada reivindicação conta, inclusive a de um lease vencido depois de um SIGKILL):
    // nada mais é executado nem retomado, e o trabalho termina Falhou na mesma transação da transição terminal, decidida
    // pelo estado atual da etapa e pela sua política. Etapa que não chegou a iniciar (nenhuma ação externa começou: o
    // início é gravado antes do processo) ou etapa Automatica em execução: execução Falhou. Etapa em execução
    // ComConfirmacao: Interrompida, só com confirmação explícita. Nunca: Bloqueada (resultado incerto), jamais uma falha
    // comum. As duas últimas continuam retomáveis por ConfirmarRetomadaAsync, com trabalho novo.
    Task<ExecucaoInterrompidaDto> EncerrarPorTentativasEsgotadasAsync(PosseDoTrabalho posse, AlvoDoTrabalhoDto alvo,
        string motivo, CancellationToken cancellationToken = default);

    // Confirmação explícita de retomada de uma execução Interrompida ou Bloqueada: nova tentativa da etapa interrompida
    // e novo trabalho, com chave de deduplicação por tentativa (confirmar duas vezes não executa duas vezes).
    // Falso quando a execução não existe, não está esperando confirmação ou tem pedido de cancelamento (#305).
    Task<bool> ConfirmarRetomadaAsync(long numero, CancellationToken cancellationToken = default);
}
