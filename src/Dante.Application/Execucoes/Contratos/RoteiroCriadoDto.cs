namespace Dante.Application.Execucoes;

// ObjetivoAlteradoPelaRedacao: o ledger guardou só a versão protegida; depois de um reinício, uma etapa que precise do
// objetivo pede um novo, sanitizado, à pessoa.
public sealed record RoteiroCriadoDto(ExecucaoNaFilaDto Execucao, bool ObjetivoAlteradoPelaRedacao);
