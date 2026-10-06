namespace Dante.Application.ConstrucaoDeContexto;

public sealed record RegistroDeContextoDto(string Chave, Guid Id, string Origem, string Estado, string Motivo, int TokensEstimados);
