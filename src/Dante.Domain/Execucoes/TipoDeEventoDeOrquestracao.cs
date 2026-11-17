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
    CancelamentoSolicitado
}
