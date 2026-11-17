namespace Dante.Domain.Execucoes;

// Ciclo de vida de uma worktree de execução (AD-71). Reservada e Pronta são ativas: contam nos limites e ocupam o
// caminho e a branch. Liberada aguarda a limpeza física; Preservada (alterações ou commits além da base), Removida e
// Falhou são finais. Falhou nunca reutiliza nem apaga o diretório: ele é relatado para conferência humana.
public enum EstadoDaReservaDeWorktree
{
    Reservada,
    Pronta,
    Liberada,
    Preservada,
    Removida,
    Falhou
}
