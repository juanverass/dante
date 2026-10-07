namespace Dante.Infrastructure.Google;

// Conteúdo cifrado em disco: o cliente OAuth usado na autorização, o refresh token e a conta lógica (e-mail).
public sealed record CredencialGoogle(
    string ClientId,
    string ClientSecret,
    string RefreshToken,
    string? Conta,
    DateTimeOffset ConectadaEm,
    string Escopos);
