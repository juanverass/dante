namespace Dante.Application.Planilhas;

// Autorização iniciada: o link que o usuário abre no navegador deste computador e a conclusão, que termina quando o
// provedor chama o callback local (ou falha por recusa, state inválido ou expiração).
public sealed record AutorizacaoDeConexao(Uri Url, DateTimeOffset ExpiraEm, Task<EstadoDaConexaoDto> Conclusao);
