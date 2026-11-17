namespace Dante.Application.Execucoes;

// Uma worktree de execução já validada no papel (AD-71): caminhos canônicos, branch e SHA base.
public sealed record WorktreePlanejada(string Alias, string CaminhoDoRepositorio, string Caminho, string Branch,
    string ShaBase);
