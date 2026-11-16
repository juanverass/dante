using Dante.Domain.Agentes;
using Dante.Domain.Execucoes;

namespace Dante.Application.Execucoes;

// Guarda fail-closed de quem executa etapas (AD-68): chamada imediatamente antes de iniciar o processo ou o turno de
// uma etapa. O job legado (Avulsa) segue como hoje, no checkout principal. Execução nova só lê: etapa de escrita é
// recusada (não existe reserva de worktree nesta entrega) e etapa de leitura só roda em perfil plan. O domínio já
// recusa criar a etapa de escrita; esta guarda cobre o que chegar ao executor por qualquer outro caminho.
public static class GuardaDeEscrita
{
    public static void Exigir(Execucao execucao, EtapaDaExecucao etapa, AgentPermissionProfile perfilEfetivo)
    {
        ArgumentNullException.ThrowIfNull(execucao);
        ArgumentNullException.ThrowIfNull(etapa);
        if (etapa.IdExecucao != execucao.Id) throw new InvalidOperationException("A etapa pertence a outra execução.");
        if (!execucao.Nova) return;
        if (etapa.Escrita) throw new EscritaSemWorktreeException();
        if (perfilEfetivo != AgentPermissionProfile.Plan)
            throw new InvalidOperationException(
                $"Etapa de leitura da execução {execucao.Codigo} só roda em perfil plan; nada foi iniciado.");
    }
}
