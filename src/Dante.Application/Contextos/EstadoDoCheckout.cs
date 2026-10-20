namespace Dante.Application.Contextos;

// Retrato somente leitura de um checkout depois de uma execução multirrepositório (AD-58): branch atual, arquivos
// alterados e commits ainda não enviados. Null em cada campo quando o Git não informou; Erro diz por quê.
public sealed record EstadoDoCheckout(string? Branch, int? ArquivosAlterados, int? CommitsNaoEnviados, bool TemUpstream,
    string? Erro = null);
