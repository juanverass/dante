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
    SolicitacaoEncerrada
}
