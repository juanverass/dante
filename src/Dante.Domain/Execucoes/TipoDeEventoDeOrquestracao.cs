namespace Dante.Domain.Execucoes;

// Eventos-chave do ledger. Deltas de mensagem, textos de resposta e descrições de comandos nunca são eventos.
public enum TipoDeEventoDeOrquestracao
{
    ExecucaoCriada,
    ExecucaoTransitou,
    EtapaTransitou,
    SessaoCriada,
    SessaoTransitou,
    TurnoIniciado,
    TurnoConcluido,
    FerramentaIniciada,
    FerramentaConcluida,
    AprovacaoSolicitada,
    EntradaSolicitada,
    SolicitacaoResolvida,
    SolicitacaoEncerrada,
    // Pedido de cancelamento do dono (#305): append-only, com autor e motivo. É a fonte durável do pedido; quem tem a
    // posse do trabalho o honra e grava Cancelada. Não muda estado por si.
    CancelamentoSolicitado,
    // Worktrees de execução de escrita (#307, AD-71). Dados: alias, branch e estado; nunca caminho nem conteúdo.
    WorktreeReservada,
    WorktreeCriada,
    WorktreeLiberada,
    WorktreeRemovida,
    WorktreePreservada,
    WorktreeFalhou,
    // Gate humano de um Roteiro (#308): a reserva sai da capacidade sem deixar de ser da execução, e volta a ela.
    WorktreeAguardandoDecisao,
    WorktreeReadquirida,
    // Delegação rastreada (#309, AD-70), sempre na execução de origem. Dados: delegação, subordinada (E…), destinatário,
    // contrato e, nas transições, de/para e motivo curto; nunca a entrada, o objetivo nem o resultado.
    DelegacaoSolicitada,
    DelegacaoTransitou,
    // Marco do executor da subordinada (sessão aberta, turno enviado, resultado recebido): só o marco, nunca conteúdo.
    DelegacaoProgrediu,
    // A origem saiu da espera: uma etapa nova entrega os desfechos das delegações da etapa anterior. Dados: etapa de
    // origem e quantidade de delegações; a nova etapa é a referência.
    OrigemRetomada,
    // Coordenação (#310, AD-75): a proposta do coordenador foi recusada pela validação do plano. Dados: a tentativa e os
    // motivos com a unidade; nunca o conteúdo proposto. Contado por etapa de planejamento: é a correção única do ledger.
    PlanoRecusado
}
