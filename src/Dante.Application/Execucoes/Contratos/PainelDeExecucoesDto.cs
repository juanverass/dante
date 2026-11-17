namespace Dante.Application.Execucoes;

// Contagem das execuções não terminais do proprietário (#305), para um resumo de uma linha.
public sealed record PainelDeExecucoesDto(int EmAndamento, int Aguardando);
