namespace Dante.Domain.Execucoes;

// Ciclo de vida de uma worktree de execução (AD-71). Reservada e Pronta são ativas: contam nos limites e ocupam o
// caminho e a branch. AguardandoDecisao (Roteiro no gate humano, #308, decisão C) também é ativa e ocupa caminho e
// branch, mas não conta nos limites: o slot de escrita fica livre e a worktree continua da execução, sem limpeza nem
// reutilização; volta a Pronta só pela reaquisição sob a trava de capacidade. Liberada aguarda a limpeza física;
// Preservada (alterações ou commits além da base), Removida e Falhou são finais. Falhou nunca reutiliza nem apaga o
// diretório: ele é relatado para conferência humana.
public enum EstadoDaReservaDeWorktree
{
    Reservada,
    Pronta,
    Liberada,
    Preservada,
    Removida,
    Falhou,
    AguardandoDecisao
}
