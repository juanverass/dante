using System.Diagnostics;
using Dante.Application.Contextos;
using Dante.Application.Execucoes;
using Dante.Infrastructure.Contextos;

namespace Dante.Tests;

// Revisão 3 do PR #326: a entrega lê os bytes da worktree confinada pelo kernel (openat2 com RESOLVE_BENEATH e
// RESOLVE_NO_SYMLINKS a partir de um handle da raiz). Um adversário troca o arquivo ou o diretório pai por um link para
// um arquivo de fora da worktree com conteúdo sentinela, no ponto mais estreito possível: imediatamente antes da
// abertura, ou entre a classificação do caminho e a abertura para leitura. A leitura é recusada, nada é commitado, nenhum
// objeto com o sentinela existe no banco de objetos e nenhum blob da própria worktree foi gravado antes da recusa (os
// bytes só vão ao Git depois de todos os caminhos lidos e validados). Git e filesystem reais.
public sealed class LeituraConfinadaDaWorktreeTests : IDisposable
{
    private static readonly AutorDoCommit Autor = new("Dante Bot", "dante@example.com");
    private readonly RepositorioGitDeTeste temp = new();

    public void Dispose() => temp.Dispose();

    // alvo: "rastreado" (codigo.txt, alterado), "novo" (pasta/novo.txt, não rastreado) ou "diretorio" (pasta, pai de
    // pasta/novo.txt). etapa: "abrir" (antes da primeira abertura) ou "ler" (entre a classificação e a leitura).
    [Theory]
    [InlineData("rastreado", "abrir", false)]
    [InlineData("rastreado", "ler", false)]
    [InlineData("novo", "abrir", false)]
    [InlineData("novo", "ler", false)]
    [InlineData("diretorio", "abrir", false)]
    [InlineData("diretorio", "ler", false)]
    [InlineData("rastreado", "ler", true)]
    [InlineData("novo", "ler", true)]
    [InlineData("diretorio", "ler", true)]
    public async Task TrocaPorLinkParaForaDaWorktreeERecusadaSemGravarOSentinela(string alvo, string etapa, bool commitar)
    {
        if (!OperatingSystem.IsLinux()) return;
        var (repo, w, gerenciador) = await PrepararAsync();
        // Um arquivo novo legítimo, lido antes do alvo (ordem dos caminhos): o blob dele também não pode existir.
        File.WriteAllText(Path.Combine(w.Caminho, "a-legitimo.txt"), "legitimo " + Guid.NewGuid().ToString("N") + "\n");
        File.WriteAllText(Path.Combine(w.Caminho, "codigo.txt"), "alterado\n");
        Directory.CreateDirectory(Path.Combine(w.Caminho, "pasta"));
        File.WriteAllText(Path.Combine(w.Caminho, "pasta", "novo.txt"), "novo\n");
        var aprovado = (await gerenciador.ResumirAlteracoesAsync(w, 0)).Hash;

        // Fora da worktree: o sentinela, e um diretório com o mesmo nome de arquivo do alvo, contendo o sentinela.
        var sentinela = "SENTINELA " + Guid.NewGuid().ToString("N") + "\n";
        var externo = Path.Combine(temp.Raiz, "externo");
        Directory.CreateDirectory(externo);
        File.WriteAllText(Path.Combine(externo, "sentinela.txt"), sentinela);
        File.WriteAllText(Path.Combine(externo, "novo.txt"), sentinela);
        var oidDoSentinela = RepositorioGitDeTeste.Git(repo.Caminho, "hash-object", Path.Combine(externo, "sentinela.txt"));
        var oidDoLegitimo = RepositorioGitDeTeste.Git(repo.Caminho, "hash-object", Path.Combine(w.Caminho, "a-legitimo.txt"));
        Assert.False(ObjetoExiste(repo, oidDoSentinela));

        var caminhoDoAlvo = alvo switch { "rastreado" => "codigo.txt", _ => "pasta/novo.txt" };
        var atacou = false;
        var adversario = new GerenciadorDeWorktreesGit(new OpcoesDeWorktrees { Raiz = temp.RaizDasWorktrees },
            new CatalogoFixo(repo))
        {
            IntervencaoNaLeitura = (momento, relativo) =>
            {
                if (atacou || momento != etapa || relativo != caminhoDoAlvo) return;
                atacou = true;
                if (alvo == "diretorio")
                {
                    Directory.Move(Path.Combine(w.Caminho, "pasta"), Path.Combine(temp.Raiz, "pasta-movida"));
                    File.CreateSymbolicLink(Path.Combine(w.Caminho, "pasta"), externo);
                }
                else
                {
                    File.Delete(Path.Combine(w.Caminho, caminhoDoAlvo));
                    File.CreateSymbolicLink(Path.Combine(w.Caminho, caminhoDoAlvo), Path.Combine(externo, "sentinela.txt"));
                }
            }
        };

        if (commitar)
            Assert.IsType<DesfechoDoCommitDto.SemEfeito>(
                await adversario.CommitarAsync(w, new(w.ShaBase, aprovado, Autor, "m")));
        else
        {
            // Ou a leitura é recusada, ou (link no meio do caminho, que o Git trata como caminho ausente) o conteúdo não
            // é o aprovado: em nenhum caso o artefato lido é o de fora.
            try
            {
                Assert.NotEqual(aprovado, (await adversario.LerArtefatoAsync(w)).Hash);
            }
            catch (WorktreeInvalidaException)
            {
            }
        }

        Assert.True(atacou, "a intervenção não aconteceu: o teste não provaria nada");
        Assert.False(ObjetoExiste(repo, oidDoSentinela), "bytes de fora da worktree viraram blob");
        Assert.Equal(w.ShaBase, RepositorioGitDeTeste.Git(w.Caminho, "rev-parse", "HEAD"));
        // Na etapa "ler" a recusa vem antes de qualquer gravação: nem o blob legítimo existe.
        if (etapa == "ler") Assert.False(ObjetoExiste(repo, oidDoLegitimo), "um blob foi gravado antes da recusa");
    }

    // Um hard link não é um link simbólico, mas pode apontar para um arquivo de fora da worktree: a entrega não o lê.
    [Fact]
    public async Task ArquivoComMaisDeUmLinkFisicoERecusadoSemGravarOConteudo()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (repo, w, gerenciador) = await PrepararAsync();
        var sentinela = Path.Combine(temp.Raiz, "sentinela-fisico.txt");
        File.WriteAllText(sentinela, "SENTINELA " + Guid.NewGuid().ToString("N") + "\n");
        Assert.Equal(0, Executar("ln", sentinela, Path.Combine(w.Caminho, "novo.txt")));
        var oid = RepositorioGitDeTeste.Git(repo.Caminho, "hash-object", sentinela);

        var recusa = await Assert.ThrowsAsync<WorktreeInvalidaException>(() => gerenciador.LerArtefatoAsync(w));

        Assert.Contains("mais de um link físico", recusa.Message);
        Assert.False(ObjetoExiste(repo, oid));
    }

    // Links legítimos continuam funcionando: um link dentro da worktree é gravado como link (o destino, não o conteúdo),
    // mesmo que aponte para fora, e um caminho atrás de um link de diretório é tratado como ausente, como o Git faz.
    [Fact]
    public async Task LinkDaWorktreeEGravadoComoLinkENuncaSeguido()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (repo, w, gerenciador) = await PrepararAsync();
        var sentinela = Path.Combine(temp.Raiz, "sentinela-link.txt");
        File.WriteAllText(sentinela, "SENTINELA " + Guid.NewGuid().ToString("N") + "\n");
        File.CreateSymbolicLink(Path.Combine(w.Caminho, "para-fora"), sentinela);
        var aprovado = (await gerenciador.ResumirAlteracoesAsync(w, 0)).Hash;

        var desfecho = await gerenciador.CommitarAsync(w, new(w.ShaBase, aprovado, Autor, "m"));

        Assert.IsType<DesfechoDoCommitDto.Commitado>(desfecho);
        Assert.Equal("120000", RepositorioGitDeTeste.Git(w.Caminho, "ls-tree", "HEAD", "para-fora").Split(' ')[0]);
        Assert.Equal(sentinela, RepositorioGitDeTeste.Git(w.Caminho, "cat-file", "blob", "HEAD:para-fora"));
        Assert.False(ObjetoExiste(repo, RepositorioGitDeTeste.Git(repo.Caminho, "hash-object", sentinela)));
    }

    // ---- apoio ----

    private async Task<(RepositorioCadastrado Repo, WorktreePlanejada Worktree, GerenciadorDeWorktreesGit Gerenciador)>
        PrepararAsync()
    {
        var repo = temp.Criar("dante");
        var gerenciador = new GerenciadorDeWorktreesGit(new OpcoesDeWorktrees { Raiz = temp.RaizDasWorktrees },
            new CatalogoFixo(repo));
        var w = await gerenciador.PlanejarAsync(repo, 7, "dev");
        await gerenciador.CriarOuAdotarAsync(w);
        return (repo, w, gerenciador);
    }

    private static bool ObjetoExiste(RepositorioCadastrado repo, string oid) =>
        Executar("git", "-C", repo.Caminho, "cat-file", "-e", oid) == 0;

    private static int Executar(string programa, params string[] argumentos)
    {
        var inicio = new ProcessStartInfo(programa)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argumento in argumentos) inicio.ArgumentList.Add(argumento);
        using var processo = Process.Start(inicio)!;
        processo.StandardOutput.ReadToEnd();
        processo.StandardError.ReadToEnd();
        processo.WaitForExit();
        return processo.ExitCode;
    }
}
