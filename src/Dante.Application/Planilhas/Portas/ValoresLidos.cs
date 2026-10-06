namespace Dante.Application.Planilhas;

// Valores exibidos de um retângulo a partir da célula de Origem, sem linhas e colunas vazias à direita/abaixo.
public sealed record ValoresLidos(IntervaloA1 Origem, IReadOnlyList<IReadOnlyList<string>> Linhas);
