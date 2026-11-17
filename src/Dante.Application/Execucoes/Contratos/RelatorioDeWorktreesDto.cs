namespace Dante.Application.Execucoes;

// Limpeza de worktrees de um host (#307): reservas de execuções encerradas liberadas, diretórios removidos ou
// preservados (com motivo no ledger), falhas de remoção que ficam para a próxima passada e diretórios desconhecidos
// sob a raiz, só relatados.
public sealed record RelatorioDeWorktreesDto(int Liberadas, int Removidas, int Preservadas, int FalhasAoRemover,
    IReadOnlyList<string> Desconhecidos);
