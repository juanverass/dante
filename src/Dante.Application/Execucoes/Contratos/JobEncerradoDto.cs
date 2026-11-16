using Dante.Domain.Execucoes;

namespace Dante.Application.Execucoes;

// EstadoFinal: Concluida, Falhou ou Cancelada, como o JobRegistry decidiu.
public sealed record JobEncerradoDto(long Numero, EstadoDaExecucao EstadoFinal, int? CodigoDeSaida, string? Erro,
    DateTimeOffset EncerradoEm);
