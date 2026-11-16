using Dante.Domain.Execucoes;

namespace Dante.Application.Execucoes;

// EstadoFinal: Encerrada, Falhou ou Interrompida. IdUpstream é o último, quando uma limpeza o trocou.
public sealed record SessaoEncerradaDto(long Numero, EstadoDaSessaoDeAgente EstadoFinal, string? Motivo,
    string? IdUpstream, DateTimeOffset EncerradaEm);
