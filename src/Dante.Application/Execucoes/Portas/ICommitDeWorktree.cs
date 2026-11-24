namespace Dante.Application.Execucoes;

// Escrita Git confiável do D.A.N.T.E. numa worktree de execução (#311, PR 3): só o commit local do artefato aprovado,
// pelo conjunto fechado do GerenciadorDeWorktreesGit. Nenhum processo Git da entrega lê os arquivos da worktree: o
// D.A.N.T.E. os lê, grava os blobs sem filtros e monta a árvore num index privado, então nenhum filtro de conteúdo
// (clean, smudge, process) roda, qualquer que seja a configuração do Git em qualquer momento. O único efeito é a branch
// da execução avançar da base para o commit novo, por compare-and-swap. Nunca push, reset, checkout, merge nem criação
// de branch; nunca no checkout principal (a worktree é validada antes).
public interface ICommitDeWorktree
{
    // HEAD da worktree validada e o hash canônico do conteúdo atual (o mesmo formato da revisão do roteiro), a partir da
    // árvore montada dos bytes da worktree. Lança WorktreeInvalidaException se a worktree não puder ser conferida.
    Task<ArtefatoDaWorktreeDto> LerArtefatoAsync(WorktreePlanejada worktree, CancellationToken cancellationToken = default);

    // Reconfere a worktree (validação, HEAD = base), monta a árvore dos bytes atuais, confere o hash a partir dessa árvore
    // (imutável) e grava exatamente ela: o commit (autor e mensagem dados, filho da base) e a branch movida da base para ele
    // por compare-and-swap. Duas entregas concorrentes nunca geram dois commits. Depois confere pai, árvore e branch, e só
    // então o index da worktree passa a ser a árvore gravada. Recusas e falhas antes do commit são SemEfeito; o que pode ter
    // mudado é Incerto, com o estado observado. Não lança por falha do Git.
    Task<DesfechoDoCommitDto> CommitarAsync(WorktreePlanejada worktree, CommitDoArtefatoDto commit,
        CancellationToken cancellationToken = default);

    // Estado real, para relatar um Incerto: HEAD, branch, commits além da base e o index contra o HEAD (sem ler os
    // arquivos da worktree), consultados no diretório Git da worktree com a identidade conferida pelo handle, como na
    // entrega. Sem identidade comprovada, diz que o estado é ilegível e nada consulta no diretório que ocupa o caminho.
    // Nunca lança; o que não for legível é dito.
    Task<string> DescreverEstadoAsync(WorktreePlanejada worktree, CancellationToken cancellationToken = default);
}
