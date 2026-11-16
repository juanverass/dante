using Dante.Domain.Execucoes;

namespace Dante.Application.Execucoes;

// Evento-chave de uma sessão já projetado pelo host: Dados só com metadados (códigos, estados, contagens).
public sealed record EventoDeSessaoDto(long NumeroDaSessao, TipoDeEventoDeOrquestracao Tipo, string? IdTurno,
    IReadOnlyDictionary<string, string?>? Dados, DateTimeOffset OcorridoEm);
