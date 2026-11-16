using Dante.Domain.Execucoes;

namespace Dante.Application.Execucoes;

// EstadoFinal: Concluida, Falhou ou Cancelada. Erro é o texto já seguro para o ledger (sem saída de processo).
public sealed record ResultadoDaEtapaDto(EstadoDaEtapa EstadoFinal, int? CodigoDeSaida = null, string? Erro = null);
