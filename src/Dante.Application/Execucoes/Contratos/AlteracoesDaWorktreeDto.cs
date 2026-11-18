namespace Dante.Application.Execucoes;

// O que uma worktree mudou em relação à base (#308): o diff das alterações rastreadas (cortado em Diff para o prompt,
// com Cortado indicando o corte), os arquivos novos não rastreados e o hash canônico de tudo (o diff inteiro mais
// caminho e SHA-256 de cada arquivo novo). O hash é o artefato a que o veredito e a decisão humana se vinculam.
public sealed record AlteracoesDaWorktreeDto(string Diff, bool Cortado, IReadOnlyList<string> NaoRastreados, string Hash)
{
    public bool Vazias => Diff.Length == 0 && NaoRastreados.Count == 0;
}
