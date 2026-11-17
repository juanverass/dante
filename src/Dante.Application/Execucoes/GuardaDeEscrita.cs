using Dante.Domain.Agentes;
using Dante.Domain.Execucoes;

namespace Dante.Application.Execucoes;

// Guarda fail-closed de quem executa etapas (AD-68, AD-71): chamada imediatamente antes de iniciar o processo ou o turno
// de uma etapa. O job legado (Avulsa) segue como hoje, no checkout principal. Em execução nova, leitura só roda em
// perfil plan; escrita só roda acima de plan e com todas as worktrees da execução reservadas, prontas e dela — uma
// principal e nenhum caminho que seja o checkout principal de um repositório. Sem isso, nada é iniciado.
public static class GuardaDeEscrita
{
    public static void Exigir(Execucao execucao, EtapaDaExecucao etapa, AgentPermissionProfile perfilEfetivo,
        IReadOnlyCollection<WorktreeDaExecucaoDto>? worktrees = null)
    {
        ArgumentNullException.ThrowIfNull(execucao);
        ArgumentNullException.ThrowIfNull(etapa);
        if (etapa.IdExecucao != execucao.Id) throw new InvalidOperationException("A etapa pertence a outra execução.");
        if (!execucao.Nova) return;
        if (!etapa.Escrita)
        {
            if (perfilEfetivo != AgentPermissionProfile.Plan)
                throw new InvalidOperationException(
                    $"Etapa de leitura da execução {execucao.Codigo} só roda em perfil plan; nada foi iniciado.");
            return;
        }
        if (perfilEfetivo == AgentPermissionProfile.Plan)
            throw new InvalidOperationException($"Etapa de escrita da execução {execucao.Codigo} não roda em perfil plan.");
        if (worktrees is not { Count: > 0 }) throw new EscritaSemWorktreeException();
        if (worktrees.Any(w => w.IdExecucao != execucao.Id))
            throw new EscritaSemWorktreeException("worktree de outra execução");
        if (worktrees.Any(w => w.Estado != EstadoDaReservaDeWorktree.Pronta))
            throw new EscritaSemWorktreeException("worktree não validada");
        if (worktrees.Count(w => w.Principal) != 1) throw new EscritaSemWorktreeException("sem worktree principal única");
        if (worktrees.Any(w => string.Equals(w.Caminho, w.CaminhoDoRepositorio, StringComparison.OrdinalIgnoreCase) ||
                               worktrees.Any(o => string.Equals(w.Caminho, o.CaminhoDoRepositorio, StringComparison.OrdinalIgnoreCase))))
            throw new EscritaSemWorktreeException("caminho de checkout principal");
    }
}
