using Dante.Application.Agentes;
using Dante.Application.Contextos;
using Dante.Application.Execucoes;
using Dante.Domain.Execucoes;

namespace Dante.Worker.Sessions;

// Sessão que executa a etapa de uma Tarefa (AD-68). Leitura: perfil plan, contexto resolvido e aviso de somente leitura.
// Escrita (#307): perfil padrão da versão, contexto só com as worktrees prontas da execução (principal como diretório
// de trabalho, vinculados como raízes graváveis), teto no próprio perfil e escrita confinada, que o driver precisa
// confirmar com sandbox de SO. Nunca recebe checkout principal. O SessionRegistry ainda confere perfil, repositórios e
// ferramentas contra a versão.
public static class SolicitacaoDaTarefa
{
    public static SessionStartRequest Para(PlanoDaTarefa plano, long idDoUsuario,
        IReadOnlyDictionary<string, string>? ambiente = null, IReadOnlyList<WorktreeDaExecucaoDto>? worktrees = null)
    {
        ArgumentNullException.ThrowIfNull(plano);
        var configuracao = plano.Versao.Configuracao;
        if (plano.SomenteLeitura)
        {
            if (worktrees is { Count: > 0 }) throw new InvalidOperationException("Tarefa de leitura não usa worktree.");
            return new SessionStartRequest(idDoUsuario, configuracao.Driver, plano.Contexto, ambiente, plano.Perfil,
                new AgentModelSelection(configuracao.Modelo, configuracao.Esforco),
                RegisteredAgent: new AgenteDaSessao(plano.Handle, plano.Versao, MontagemDaTarefa.AvisoDeLeitura)
                {
                    // Nem /mode session tira a sessão da Tarefa de plan.
                    TetoDaExecucao = plano.Perfil
                });
        }
        if (!plano.EscritaValida) throw new EscritaSemWorktreeException("plano de escrita inválido");
        return new SessionStartRequest(idDoUsuario, configuracao.Driver, ContextoIsolado(plano, worktrees), ambiente,
            plano.Perfil, new AgentModelSelection(configuracao.Modelo, configuracao.Esforco),
            RegisteredAgent: new AgenteDaSessao(plano.Handle, plano.Versao, MontagemDaTarefa.AvisoDeEscrita)
            {
                TetoDaExecucao = plano.Perfil,
                EscritaConfinada = true,
                CheckoutsDaEscrita = [.. worktrees!.Select(w => w.CaminhoDoRepositorio).Distinct(StringComparer.Ordinal)]
            });
    }

    // Cada alias do contexto resolvido (principal e vinculados, AD-58) troca o checkout pela worktree dele; alias sem
    // worktree pronta, worktree sobrando ou caminho igual a um checkout principal recusam (fail-closed).
    internal static JobExecutionContext ContextoIsolado(PlanoDaTarefa plano, IReadOnlyList<WorktreeDaExecucaoDto>? worktrees)
    {
        if (worktrees is not { Count: > 0 }) throw new EscritaSemWorktreeException();
        var contexto = plano.Contexto;
        var aliases = contexto.RepositoriosVinculados.Select(v => v.Alias).Prepend(contexto.RepositoryAlias!).ToArray();
        if (worktrees.Count != aliases.Length) throw new EscritaSemWorktreeException("worktrees diferentes dos repositórios");
        var checkouts = contexto.RepositoriosVinculados.Select(v => v.Caminho).Prepend(contexto.WorkingDirectory).ToArray();
        WorktreeDaExecucaoDto Worktree(string alias, bool principal)
        {
            var worktree = worktrees.SingleOrDefault(w => string.Equals(w.Alias, alias, StringComparison.OrdinalIgnoreCase))
                ?? throw new EscritaSemWorktreeException($"sem worktree para {alias}");
            if (worktree.Principal != principal || worktree.Estado != EstadoDaReservaDeWorktree.Pronta ||
                checkouts.Any(c => string.Equals(c, worktree.Caminho, StringComparison.OrdinalIgnoreCase)))
                throw new EscritaSemWorktreeException($"worktree inválida para {alias}");
            return worktree;
        }
        var principal = Worktree(contexto.RepositoryAlias!, true);
        var vinculados = contexto.RepositoriosVinculados
            .Select(v => new RepositorioCadastrado(v.Alias, Worktree(v.Alias, false).Caminho)).ToArray();
        return JobExecutionContext.Repository(contexto.RepositoryAlias!, principal.Caminho, vinculados);
    }
}
