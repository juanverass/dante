namespace Dante.Application.Execucoes;

// O dono da execução pediu cancelamento (#305): quem tem a posse não inicia nem retoma a etapa e grava Cancelada.
public sealed class CancelamentoSolicitadoException()
    : InvalidOperationException("Cancelamento solicitado pelo dono da execução.");
