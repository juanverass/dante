namespace Dante.Application.Execucoes;

// Escrita Git confiável do D.A.N.T.E. numa worktree de execução (#311, PR 3): só o commit local do artefato aprovado,
// pelo conjunto fechado do GerenciadorDeWorktreesGit (add --all, write-tree, commit-tree, update-ref da branch da
// execução com o valor antigo e read-tree para desfazer o index antes do commit). Nunca push, reset, checkout, merge nem
// criação de branch; nunca no checkout principal (a worktree é validada antes).
public interface ICommitDeWorktree
{
    // HEAD da worktree validada (o commit em que a branch da execução está agora).
    Task<string> LerHeadAsync(WorktreePlanejada worktree, CancellationToken cancellationToken = default);

    // Reconfere a worktree (validação, HEAD = base, hash do conteúdo = o aprovado), põe tudo no index, confere o mesmo
    // hash a partir do index, cria o commit (autor e mensagem dados, filho da base) e move a branch da base para ele por
    // compare-and-swap: duas entregas concorrentes nunca geram dois commits. Depois confere pai, árvore e branch.
    // Recusas antes de qualquer efeito e falhas desfeitas são SemEfeito; o que pode ter mudado é Incerto, com o estado
    // observado. Não lança por falha do Git.
    Task<DesfechoDoCommitDto> CommitarAsync(WorktreePlanejada worktree, CommitDoArtefatoDto commit,
        CancellationToken cancellationToken = default);

    // Estado real, para relatar um Incerto: HEAD, commits além da base, index e alterações. Nunca lança; o que não for
    // legível é dito.
    Task<string> DescreverEstadoAsync(WorktreePlanejada worktree, CancellationToken cancellationToken = default);
}
