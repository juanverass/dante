using Dante.Domain.Agentes;

namespace Dante.Application.Agentes;

// Quais drivers podem executar etapa de escrita (#307). Uma worktree não é sandbox: só escreve o driver cuja CLI confina
// o filesystem ao diretório de trabalho por mecanismo do SO, conferido pelo D.A.N.T.E. no início da sessão.
// Codex: sandbox workspace-write (Landlock/bubblewrap) com as worktrees como únicas raízes graváveis, /tmp e TMPDIR
// excluídos, rede desligada e approvalPolicy never; o diretório Git comum fica fora das raízes e, portanto, só leitura
// (commit e alteração de refs negados). O driver recusa a sessão se a thread não confirmar exatamente essa política.
// Claude: o fluxo atual do Claude CLI não tem confinamento de filesystem; instrução no prompt não é garantia, então a
// escrita fica recusada até existir sandbox de SO implementada e comprovada por teste.
public static class ConfinamentoDeEscrita
{
    public const string RecusaDoClaude =
        "Escrita via Claude está desabilitada: o fluxo atual do Claude CLI não confina o sistema de arquivos à worktree " +
        "(AD-71). Use um agente Codex para tarefas de escrita, ou uma tarefa de leitura.";

    public static bool Suporta(AgentKind driver) => driver == AgentKind.Codex;

    public static string? Recusa(AgentKind driver) => driver switch
    {
        AgentKind.Codex => null,
        AgentKind.Claude => RecusaDoClaude,
        _ => $"O driver {driver} não tem confinamento de escrita comprovado."
    };
}
