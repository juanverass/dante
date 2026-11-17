namespace Dante.Application.Execucoes;

// A gravação sob posse foi recusada: outro processador reivindicou o trabalho (nova geração), o lease expirou ou o
// trabalho já saiu de reivindicado. Nada foi gravado; o resultado local deve ser descartado (AD-67).
public sealed class PosseDoTrabalhoPerdidaException()
    : InvalidOperationException("A posse do trabalho foi perdida; nada foi gravado.");
