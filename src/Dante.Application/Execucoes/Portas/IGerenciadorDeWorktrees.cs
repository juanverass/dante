using Dante.Application.Contextos;

namespace Dante.Application.Execucoes;

// Git de worktrees (AD-71): conjunto fechado de comandos (worktree add/remove/list e consultas somente leitura), com
// ArgumentList, sem shell, core.fsmonitor=false, prazo e nunca --force. Nenhum método escreve no checkout principal:
// worktree add grava só o diretório novo e os metadados da worktree no diretório Git comum. Toda recusa é
// WorktreeInvalidaException com motivo exibível (sem segredo); falha transitória do processo git também vira recusa,
// porque nada é iniciado sem validação (fail-closed).
public interface IGerenciadorDeWorktrees
{
    // Caminho canônico da worktree, branch e SHA base (HEAD do checkout principal agora) para um repositório cadastrado.
    // Valida raiz autorizada (diretório real, sem link em nenhum componente, fora de qualquer checkout cadastrado),
    // alias, nome da branch e o repositório (raiz Git igual ao checkout). Não cria nada.
    Task<WorktreePlanejada> PlanejarAsync(RepositorioCadastrado repositorio, long numeroDaExecucao, string handle,
        CancellationToken cancellationToken = default);

    // Cria a worktree da reserva (worktree add -b <branch> <caminho> <sha>) ou adota a que já existe exatamente como
    // planejada (mesma raiz Git, diretório Git comum do repositório, branch e caminho real). Diretório ocupado por outra
    // coisa, branch existente fora da base, link no caminho: recusa, sem apagar nada. Valida antes de devolver.
    Task CriarOuAdotarAsync(WorktreePlanejada worktree, CancellationToken cancellationToken = default);

    // Validação completa, sem criar: o que precisa valer imediatamente antes de entregar o diretório ao agente.
    Task ValidarAsync(WorktreePlanejada worktree, CancellationToken cancellationToken = default);

    // Remove só worktree limpa e sem commits além da base (worktree remove, sem --force; a recusa do Git é a última
    // barreira). Com alterações, commits, ou estado ilegível: preserva e devolve o motivo. Diretório já ausente e não
    // registrado: removida. A branch nunca é apagada.
    Task<DesfechoDaLimpeza> RemoverSePossivelAsync(WorktreePlanejada worktree, CancellationToken cancellationToken = default);

    // Diretórios sob a raiz que não estão entre os conhecidos: relatados, nunca removidos.
    IReadOnlyList<string> ListarDiretoriosDesconhecidos(IReadOnlyCollection<string> conhecidos);
}
