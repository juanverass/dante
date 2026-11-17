namespace Dante.Domain.Grupos;

// Limites de uma versão de grupo (AD-69). Nesta etapa o roteiro é estritamente sequencial: ParalelismoMaximo é 1
// (limite global operacional 1 até a AD-63 ativa por autorização própria). RodadasDeRevisaoMaximas limita o laço
// revisar → corrigir; esgotado sem convergência, a execução fica bloqueada, nunca aprovada implicitamente.
// RepositoriosPermitidos: aliases que uma execução do grupo pode usar; vazio = os do pedido, sempre dentro das
// concessões de cada agente. DuracaoMaximaEmMinutos: teto da execução inteira, nulo = sem teto próprio.
public sealed record PoliticaDoGrupo(
    int RodadasDeRevisaoMaximas,
    int ParalelismoMaximo = 1,
    IReadOnlyList<string>? RepositoriosPermitidos = null,
    int? DuracaoMaximaEmMinutos = null);
