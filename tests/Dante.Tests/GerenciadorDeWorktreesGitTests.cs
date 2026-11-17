using Dante.Application.Contextos;
using Dante.Application.Execucoes;
using Dante.Infrastructure.Contextos;

namespace Dante.Tests;

// #307 (AD-71): worktrees de execução com Git e filesystem reais, em diretório temporário. Recusas são fail-closed e
// nunca apagam nada; o checkout principal fica byte a byte igual (arquivos, index, HEAD, config e a ref main).
public sealed class GerenciadorDeWorktreesGitTests : IDisposable
{
    private readonly RepositorioGitDeTeste temp = new();

    public void Dispose() => temp.Dispose();

    [Fact]
    public async Task CriaWorktreeIsoladaNaBranchDaExecucaoSemTocarOCheckoutPrincipal()
    {
        var repo = temp.Criar("dante");
        var antes = RepositorioGitDeTeste.Impressao(repo.Caminho);
        var gerenciador = Gerenciador(repo);

        var planejada = await gerenciador.PlanejarAsync(repo, 42, "dev");
        Assert.Equal(Path.Combine(temp.RaizDasWorktrees, "dante", "E42"), planejada.Caminho);
        Assert.Equal(("@dante", repo.Caminho, "dante/dev/e42"), (planejada.Alias, planejada.CaminhoDoRepositorio, planejada.Branch));
        Assert.Equal(RepositorioGitDeTeste.Git(repo.Caminho, "rev-parse", "HEAD"), planejada.ShaBase);
        Assert.False(Directory.Exists(planejada.Caminho));

        await gerenciador.CriarOuAdotarAsync(planejada);
        Assert.Equal("original\n", File.ReadAllText(Path.Combine(planejada.Caminho, "codigo.txt")));
        Assert.Equal("dante/dev/e42", RepositorioGitDeTeste.Git(planejada.Caminho, "branch", "--show-current"));
        // A worktree compartilha o diretório Git comum do repositório (é o que a AD-71 documenta).
        Assert.Equal(Path.Combine(repo.Caminho, ".git"),
            RepositorioGitDeTeste.Git(planejada.Caminho, "rev-parse", "--path-format=absolute", "--git-common-dir"));

        // Escrever na worktree não muda o checkout principal.
        File.WriteAllText(Path.Combine(planejada.Caminho, "codigo.txt"), "alterado pelo agente\n");
        File.WriteAllText(Path.Combine(planejada.Caminho, "novo.txt"), "novo\n");
        Assert.Equal(antes, RepositorioGitDeTeste.Impressao(repo.Caminho));
        Assert.Equal("main", RepositorioGitDeTeste.Git(repo.Caminho, "branch", "--show-current"));
        Assert.Equal("", RepositorioGitDeTeste.Git(repo.Caminho, "status", "--porcelain"));

        // Morte do Worker depois do add: a nova tentativa adota a mesma worktree, sem duplicar.
        await gerenciador.CriarOuAdotarAsync(planejada);
        Assert.Equal(2, RepositorioGitDeTeste.Git(repo.Caminho, "worktree", "list", "--porcelain")
            .Split('\n').Count(l => l.StartsWith("worktree ", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task IsolamentoEntreRepositoriosEExecucoes()
    {
        var a = temp.Criar("alfa");
        var b = temp.Criar("beta");
        var gerenciador = Gerenciador(a, b);
        var (impressaoA, impressaoB) = (RepositorioGitDeTeste.Impressao(a.Caminho), RepositorioGitDeTeste.Impressao(b.Caminho));
        var wa1 = await gerenciador.PlanejarAsync(a, 1, "dev");
        var wa2 = await gerenciador.PlanejarAsync(a, 2, "dev");
        var wb1 = await gerenciador.PlanejarAsync(b, 1, "dev");
        foreach (var w in new[] { wa1, wa2, wb1 }) await gerenciador.CriarOuAdotarAsync(w);
        Assert.Equal(3, new[] { wa1.Caminho, wa2.Caminho, wb1.Caminho }.Distinct().Count());
        File.WriteAllText(Path.Combine(wa1.Caminho, "codigo.txt"), "só em E1\n");
        Assert.Equal("original\n", File.ReadAllText(Path.Combine(wa2.Caminho, "codigo.txt")));
        Assert.Equal("original\n", File.ReadAllText(Path.Combine(wb1.Caminho, "codigo.txt")));
        // A worktree de um repositório não é aceita como a de outro.
        await Assert.ThrowsAsync<WorktreeInvalidaException>(() =>
            gerenciador.ValidarAsync(wa1 with { CaminhoDoRepositorio = b.Caminho }));
        Assert.Equal((impressaoA, impressaoB), (RepositorioGitDeTeste.Impressao(a.Caminho), RepositorioGitDeTeste.Impressao(b.Caminho)));
    }

    [Fact]
    public async Task TraversalAliasHandleECaminhoForaDoLayoutSaoRecusados()
    {
        var repo = temp.Criar("dante");
        var gerenciador = Gerenciador(repo);
        await Assert.ThrowsAsync<WorktreeInvalidaException>(() => gerenciador.PlanejarAsync(repo with { Alias = "@../x" }, 1, "dev"));
        await Assert.ThrowsAsync<WorktreeInvalidaException>(() => gerenciador.PlanejarAsync(repo with { Alias = "dante" }, 1, "dev"));
        await Assert.ThrowsAsync<WorktreeInvalidaException>(() => gerenciador.PlanejarAsync(repo, 1, "../dev"));
        await Assert.ThrowsAsync<WorktreeInvalidaException>(() => gerenciador.PlanejarAsync(repo, 1, "dev/x"));
        await Assert.ThrowsAsync<WorktreeInvalidaException>(() => gerenciador.PlanejarAsync(repo, 0, "dev"));

        // Uma reserva adulterada (o que viria do ledger) é conferida de novo antes de criar, validar ou remover.
        var valida = await gerenciador.PlanejarAsync(repo, 7, "dev");
        var fora = Path.Combine(temp.Raiz, "fora");
        foreach (var adulterada in new[]
                 {
                     valida with { Caminho = Path.Combine(temp.RaizDasWorktrees, "dante", "..", "..", "fora") },
                     valida with { Caminho = fora },
                     valida with { Caminho = repo.Caminho },
                     valida with { Caminho = Path.Combine(temp.RaizDasWorktrees, "outro", "E7") },
                     valida with { Branch = "main" },
                     valida with { ShaBase = "HEAD" }
                 })
        {
            await Assert.ThrowsAsync<WorktreeInvalidaException>(() => gerenciador.CriarOuAdotarAsync(adulterada));
            await Assert.ThrowsAsync<WorktreeInvalidaException>(() => gerenciador.RemoverSePossivelAsync(adulterada));
        }
        Assert.False(Directory.Exists(fora));
        Assert.Equal(1, RepositorioGitDeTeste.Git(repo.Caminho, "worktree", "list", "--porcelain")
            .Split('\n').Count(l => l.StartsWith("worktree ", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task SymlinkNaRaizNoDiretorioDoAliasOuNoCaminhoERecusadoSemApagarNada()
    {
        var repo = temp.Criar("dante");
        var alvo = Path.Combine(temp.Raiz, "alvo-externo");
        Directory.CreateDirectory(alvo);
        File.WriteAllText(Path.Combine(alvo, "importante.txt"), "não apagar");

        // Raiz das worktrees é um link para outro lugar.
        Directory.CreateSymbolicLink(temp.RaizDasWorktrees, alvo);
        var planejada = new WorktreePlanejada("@dante", repo.Caminho, Path.Combine(temp.RaizDasWorktrees, "dante", "E1"),
            "dante/dev/e1", RepositorioGitDeTeste.Git(repo.Caminho, "rev-parse", "HEAD"));
        await Assert.ThrowsAsync<WorktreeInvalidaException>(() => Gerenciador(repo).PlanejarAsync(repo, 1, "dev"));
        await Assert.ThrowsAsync<WorktreeInvalidaException>(() => Gerenciador(repo).CriarOuAdotarAsync(planejada));
        File.Delete(temp.RaizDasWorktrees);

        // Diretório do alias é um link.
        Directory.CreateDirectory(temp.RaizDasWorktrees);
        Directory.CreateSymbolicLink(Path.Combine(temp.RaizDasWorktrees, "dante"), alvo);
        var gerenciador = Gerenciador(repo);
        await Assert.ThrowsAsync<WorktreeInvalidaException>(() => gerenciador.CriarOuAdotarAsync(planejada));
        File.Delete(Path.Combine(temp.RaizDasWorktrees, "dante"));

        // O próprio caminho da worktree é um link para um checkout válido (outra worktree do mesmo repositório).
        var legitima = await gerenciador.PlanejarAsync(repo, 2, "dev");
        await gerenciador.CriarOuAdotarAsync(legitima);
        Directory.CreateSymbolicLink(planejada.Caminho, legitima.Caminho);
        await Assert.ThrowsAsync<WorktreeInvalidaException>(() => gerenciador.CriarOuAdotarAsync(planejada));
        await Assert.ThrowsAsync<WorktreeInvalidaException>(() => gerenciador.ValidarAsync(planejada));
        var limpeza = await gerenciador.RemoverSePossivelAsync(planejada);
        Assert.False(limpeza.Removida);

        // Nada foi apagado nem criado do lado de fora.
        Assert.Equal("não apagar", File.ReadAllText(Path.Combine(alvo, "importante.txt")));
        Assert.Single(Directory.EnumerateFileSystemEntries(alvo));
        Assert.True(Directory.Exists(legitima.Caminho));
    }

    [Fact]
    public async Task DiretorioOcupadoBranchForaDaBaseERepositorioInvalidoSaoRecusados()
    {
        var repo = temp.Criar("dante");
        var gerenciador = Gerenciador(repo);

        // Diretório ocupado por algo que não é a worktree planejada: recusado e preservado.
        var ocupada = await gerenciador.PlanejarAsync(repo, 3, "dev");
        Directory.CreateDirectory(ocupada.Caminho);
        File.WriteAllText(Path.Combine(ocupada.Caminho, "do-humano.txt"), "x");
        await Assert.ThrowsAsync<WorktreeInvalidaException>(() => gerenciador.CriarOuAdotarAsync(ocupada));
        Assert.True(File.Exists(Path.Combine(ocupada.Caminho, "do-humano.txt")));

        // Branch da execução já existe e avançou além da base: recusado, a branch fica.
        var avancada = await gerenciador.PlanejarAsync(repo, 4, "dev");
        RepositorioGitDeTeste.Git(repo.Caminho, "branch", avancada.Branch);
        RepositorioGitDeTeste.Git(repo.Caminho, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "--allow-empty",
            "-q", "-m", "x");
        RepositorioGitDeTeste.Git(repo.Caminho, "branch", "-f", avancada.Branch, "HEAD");
        RepositorioGitDeTeste.Git(repo.Caminho, "reset", "-q", "--hard", "HEAD~1");
        await Assert.ThrowsAsync<WorktreeInvalidaException>(() => gerenciador.CriarOuAdotarAsync(avancada));
        Assert.False(Directory.Exists(avancada.Caminho));

        // Branch de uma tentativa anterior ainda na base (o add caiu antes do diretório): reaproveitada.
        var parcial = await gerenciador.PlanejarAsync(repo, 5, "dev");
        RepositorioGitDeTeste.Git(repo.Caminho, "branch", parcial.Branch, parcial.ShaBase);
        await gerenciador.CriarOuAdotarAsync(parcial);
        Assert.Equal(parcial.Branch, RepositorioGitDeTeste.Git(parcial.Caminho, "branch", "--show-current"));

        // Subdiretório (não é a raiz), repositório sem commit e caminho inexistente.
        Directory.CreateDirectory(Path.Combine(repo.Caminho, "sub"));
        var vazio = temp.Criar("vazio", comCommit: false);
        var g = Gerenciador(repo, vazio);
        await Assert.ThrowsAsync<WorktreeInvalidaException>(() => g.PlanejarAsync(repo with { Caminho = Path.Combine(repo.Caminho, "sub") }, 6, "dev"));
        await Assert.ThrowsAsync<WorktreeInvalidaException>(() => g.PlanejarAsync(vazio, 6, "dev"));
        await Assert.ThrowsAsync<WorktreeInvalidaException>(() => g.PlanejarAsync(repo with { Caminho = Path.Combine(temp.Raiz, "nada") }, 6, "dev"));
    }

    [Fact]
    public async Task LimpezaRemoveSoWorktreeLimpaEPreservaAlteracoesECommitsSemApagarBranch()
    {
        var repo = temp.Criar("dante");
        var antes = RepositorioGitDeTeste.Impressao(repo.Caminho);
        var gerenciador = Gerenciador(repo);
        async Task<WorktreePlanejada> Criada(long numero)
        {
            var w = await gerenciador.PlanejarAsync(repo, numero, "dev");
            await gerenciador.CriarOuAdotarAsync(w);
            return w;
        }

        var limpa = await Criada(1);
        var suja = await Criada(2);
        var naoRastreada = await Criada(3);
        var comCommit = await Criada(4);
        var ausente = await Criada(5);
        File.WriteAllText(Path.Combine(suja.Caminho, "codigo.txt"), "alterado\n");
        File.WriteAllText(Path.Combine(naoRastreada.Caminho, "novo.txt"), "novo\n");
        RepositorioGitDeTeste.Git(comCommit.Caminho, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "--allow-empty",
            "-q", "-m", "trabalho");
        Directory.Delete(ausente.Caminho, true);

        Assert.Equal(new DesfechoDaLimpeza(true, null), await gerenciador.RemoverSePossivelAsync(limpa));
        Assert.False(Directory.Exists(limpa.Caminho));
        Assert.Equal("Preservada: há alterações não commitadas.", (await gerenciador.RemoverSePossivelAsync(suja)).MotivoDaPreservacao);
        Assert.Equal("Preservada: há alterações não commitadas.", (await gerenciador.RemoverSePossivelAsync(naoRastreada)).MotivoDaPreservacao);
        Assert.Equal("Preservada: há commits além da base.", (await gerenciador.RemoverSePossivelAsync(comCommit)).MotivoDaPreservacao);
        Assert.True((await gerenciador.RemoverSePossivelAsync(ausente)).Removida);
        Assert.Equal("alterado\n", File.ReadAllText(Path.Combine(suja.Caminho, "codigo.txt")));
        Assert.True(File.Exists(Path.Combine(naoRastreada.Caminho, "novo.txt")));

        // Branches nunca são apagadas; o Git só lista as worktrees que existem.
        var branches = RepositorioGitDeTeste.Git(repo.Caminho, "branch", "--list", "dante/*");
        foreach (var n in Enumerable.Range(1, 5)) Assert.Contains($"dante/dev/e{n}", branches);
        var listadas = RepositorioGitDeTeste.Git(repo.Caminho, "worktree", "list", "--porcelain");
        Assert.DoesNotContain(limpa.Caminho, listadas);
        Assert.DoesNotContain(ausente.Caminho, listadas);
        Assert.Contains(suja.Caminho, listadas);
        Assert.Equal(antes, RepositorioGitDeTeste.Impressao(repo.Caminho));

        // Órfãs: diretórios sob a raiz sem reserva são só relatados.
        var desconhecidos = gerenciador.ListarDiretoriosDesconhecidos([suja.Caminho, naoRastreada.Caminho]);
        Assert.Equal([comCommit.Caminho], desconhecidos);
    }

    [Fact]
    public async Task FalhaDoGitNaCriacaoENaRemocaoEFailClosed()
    {
        // O git falso é um script de shell: só existe fora do Windows.
        if (OperatingSystem.IsWindows()) return;
        var repo = temp.Criar("dante");
        var antes = RepositorioGitDeTeste.Impressao(repo.Caminho);
        var falso = Path.Combine(temp.Raiz, "git-falso");
        // Delega ao git real, exceto o subcomando pedido, que falha como o Git falharia (disco, trava, permissão).
        File.WriteAllText(falso, "#!/bin/sh\nfor a in \"$@\"; do if [ \"$a\" = \"$DANTE_GIT_FALHA\" ]; then echo 'fatal: falha simulada' >&2; exit 128; fi; done\nexec git \"$@\"\n");
        File.SetUnixFileMode(falso, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var gerenciador = new GerenciadorDeWorktreesGit(new OpcoesDeWorktrees { Raiz = temp.RaizDasWorktrees },
            new CatalogoFixo(repo), executavel: falso);
        var w = await gerenciador.PlanejarAsync(repo, 1, "dev");

        Environment.SetEnvironmentVariable("DANTE_GIT_FALHA", "add");
        try
        {
            var recusa = await Assert.ThrowsAsync<WorktreeInvalidaException>(() => gerenciador.CriarOuAdotarAsync(w));
            Assert.Contains("falha simulada", recusa.Message);
            Assert.False(Directory.Exists(w.Caminho));

            Environment.SetEnvironmentVariable("DANTE_GIT_FALHA", null);
            await gerenciador.CriarOuAdotarAsync(w);
            Environment.SetEnvironmentVariable("DANTE_GIT_FALHA", "remove");
            var preservada = await gerenciador.RemoverSePossivelAsync(w);
            Assert.False(preservada.Removida);
            Assert.StartsWith("Preservada: o Git recusou remover a worktree", preservada.MotivoDaPreservacao);
            Assert.True(Directory.Exists(w.Caminho));

            // Git inexistente: recusa, sem exceção de processo vazando.
            var semGit = new GerenciadorDeWorktreesGit(new OpcoesDeWorktrees { Raiz = temp.RaizDasWorktrees },
                new CatalogoFixo(repo), executavel: Path.Combine(temp.Raiz, "nao-existe"));
            await Assert.ThrowsAsync<WorktreeInvalidaException>(() => semGit.PlanejarAsync(repo, 2, "dev"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DANTE_GIT_FALHA", null);
        }
        Assert.Equal(antes, RepositorioGitDeTeste.Impressao(repo.Caminho));
    }

    [Fact]
    public async Task RaizDentroDeUmRepositorioCadastradoERecusada()
    {
        var repo = temp.Criar("dante");
        var gerenciador = new GerenciadorDeWorktreesGit(new OpcoesDeWorktrees { Raiz = Path.Combine(repo.Caminho, "wt") },
            new CatalogoFixo(repo));
        await Assert.ThrowsAsync<WorktreeInvalidaException>(() => gerenciador.PlanejarAsync(repo, 1, "dev"));
        Assert.False(Directory.Exists(Path.Combine(repo.Caminho, "wt")));
    }

    [Theory]
    [InlineData(true, "worktree", "add", "-b", "dante/dev/e1", "/w/E1", "abc")]
    [InlineData(true, "worktree", "remove", "/w/E1")]
    [InlineData(true, "worktree", "list", "--porcelain")]
    [InlineData(true, "rev-parse", "--show-toplevel")]
    [InlineData(false, "worktree", "remove", "--force", "/w/E1")]
    [InlineData(false, "worktree", "add", "-f", "/w/E1", "x")]
    [InlineData(false, "worktree", "prune")]
    [InlineData(false, "push", "origin", "x")]
    [InlineData(false, "reset", "--hard")]
    [InlineData(false, "checkout", "main")]
    [InlineData(false, "branch", "-D", "x")]
    [InlineData(false, "clean", "-fdx")]
    [InlineData(false, "commit", "-m", "x")]
    public void SoOConjuntoFechadoDeComandosGitERodado(bool permitido, params string[] argumentos) =>
        Assert.Equal(permitido, GerenciadorDeWorktreesGit.Permitido(argumentos));

    private GerenciadorDeWorktreesGit Gerenciador(params RepositorioCadastrado[] repositorios) =>
        new(new OpcoesDeWorktrees { Raiz = temp.RaizDasWorktrees }, new CatalogoFixo(repositorios));
}
