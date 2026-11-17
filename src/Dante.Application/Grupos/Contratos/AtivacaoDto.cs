namespace Dante.Application.Grupos;

public sealed record AtivacaoDto(int NumeroDaVersao, string HashAprovado, long IdDoAutor, DateTimeOffset Em);
