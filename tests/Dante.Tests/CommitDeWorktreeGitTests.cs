using System.Text;
using Dante.Application.Contextos;
using Dante.Application.Execucoes;
using Dante.Infrastructure.Contextos;

namespace Dante.Tests;

// #311, PR 3: o commit local do artefato aprovado, com Git e filesystem reais. Exatamente um commit, filho da base, com o
// conteúdo aprovado, o autor configurado e a mensagem exata, na branch da worktree; o checkout principal fica byte a
// byte igual e nada vai ao remoto. Recusas antes do commit não deixam rastro (a árvore é montada num index privado, e
// o index da worktree só muda depois do commit); o que pode ter mudado é Incerto, com o estado observado.
public sealed class CommitDeWorktreeGitTests : IDisposable
{
    private static readonly AutorDoCommit Autor = new("Dante Bot", "dante@example.com");
    private readonly RepositorioGitDeTeste temp = new();

    public void Dispose() => temp.Dispose();

    [Fact]
    public async Task CommitaExatamenteOConteudoAprovadoSemTocarOCheckoutNemORemoto()
    {
        var (repo, w, gerenciador) = await PrepararAsync();
        var remoto = Remoto(repo);
        var antes = RepositorioGitDeTeste.Impressao(repo.Caminho);
        var refsDoRemoto = RepositorioGitDeTeste.Git(remoto, "for-each-ref");
        // Modificação, remoção, arquivos novos (aninhado, binário, executável, nome com espaço e acento) e um link.
        File.WriteAllText(Path.Combine(w.Caminho, "codigo.txt"), "alterado\n");
        File.Delete(Path.Combine(w.Caminho, "README.md"));
        Directory.CreateDirectory(Path.Combine(w.Caminho, "src", "módulo"));
        File.WriteAllText(Path.Combine(w.Caminho, "src", "módulo", "nome com espaço.txt"), "novo\n");
        File.WriteAllBytes(Path.Combine(w.Caminho, "dados.bin"), [0, 1, 2, 255, 0, 10, 13]);
        File.WriteAllText(Path.Combine(w.Caminho, "rodar.sh"), "#!/bin/sh\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Path.Combine(w.Caminho, "rodar.sh"), UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.UserExecute);
            File.CreateSymbolicLink(Path.Combine(w.Caminho, "atalho"), "codigo.txt");
        }
        var aprovado = (await gerenciador.ResumirAlteracoesAsync(w, 0)).Hash;
        var mensagem = "feat: entrega do roteiro E7\n\nCorpo com # que não é comentário.";

        var desfecho = await gerenciador.CommitarAsync(w, new CommitDoArtefatoDto(w.ShaBase, aprovado, Autor, mensagem));

        var commitado = Assert.IsType<DesfechoDoCommitDto.Commitado>(desfecho);
        Assert.Equal(commitado.Sha, RepositorioGitDeTeste.Git(w.Caminho, "rev-parse", "HEAD"));
        Assert.Equal(w.ShaBase, RepositorioGitDeTeste.Git(w.Caminho, "rev-parse", "HEAD^1"));
        Assert.Equal("1", RepositorioGitDeTeste.Git(w.Caminho, "rev-list", "--count", $"{w.ShaBase}..HEAD"));
        Assert.Equal(commitado.Sha, RepositorioGitDeTeste.Git(repo.Caminho, "rev-parse", $"refs/heads/{w.Branch}"));
        Assert.Equal("Dante Bot <dante@example.com>|Dante Bot <dante@example.com>",
            RepositorioGitDeTeste.Git(w.Caminho, "log", "-1", "--format=%an <%ae>|%cn <%ce>"));
        Assert.Equal(mensagem, RepositorioGitDeTeste.Git(w.Caminho, "log", "-1", "--format=%B"));
        // Tudo foi commitado: nada sobra na worktree, e a árvore do commit é o conteúdo aprovado.
        Assert.Equal("", RepositorioGitDeTeste.Git(w.Caminho, "status", "--porcelain", "--untracked-files=all"));
        Assert.Equal("novo", RepositorioGitDeTeste.Git(w.Caminho, "show", "HEAD:src/módulo/nome com espaço.txt"));
        Assert.Contains("D\tREADME.md", RepositorioGitDeTeste.Git(w.Caminho, "show", "--name-status", "--format=", "HEAD"));
        // Checkout principal intocado (arquivos, index, HEAD, config, main) e nenhuma ref nova no remoto.
        Assert.Equal(antes, RepositorioGitDeTeste.Impressao(repo.Caminho));
        Assert.Equal("main", RepositorioGitDeTeste.Git(repo.Caminho, "branch", "--show-current"));
        Assert.Equal(refsDoRemoto, RepositorioGitDeTeste.Git(remoto, "for-each-ref"));
        Assert.Equal("", RepositorioGitDeTeste.Git(repo.Caminho, "for-each-ref", "refs/remotes"));
    }

    [Fact]
    public async Task HashAdulteradoBaseDiferenteEIdentidadeInvalidaNaoTocamEmNada()
    {
        var (repo, w, gerenciador) = await PrepararAsync();
        File.WriteAllText(Path.Combine(w.Caminho, "codigo.txt"), "alterado\n");
        File.WriteAllText(Path.Combine(w.Caminho, "novo.txt"), "novo\n");
        var aprovado = (await gerenciador.ResumirAlteracoesAsync(w, 0)).Hash;
        var estado = Estado(w);

        var adulterado = await gerenciador.CommitarAsync(w, new(w.ShaBase, new string('a', 64), Autor, "m"));
        Assert.Contains("mudou depois da aprovação", Assert.IsType<DesfechoDoCommitDto.SemEfeito>(adulterado).Motivo);
        var outraBase = await gerenciador.CommitarAsync(w, new(new string('b', 40), aprovado, Autor, "m"));
        Assert.Contains("base aprovada", Assert.IsType<DesfechoDoCommitDto.SemEfeito>(outraBase).Motivo);
        foreach (var invalido in new[] { new AutorDoCommit("", "a@b.c"), new AutorDoCommit("Nome <x>", "a@b.c"),
                     new AutorDoCommit("Nome", "sem-arroba"), new AutorDoCommit("Linha\nDupla", "a@b.c") })
            Assert.Contains("Identidade Git inválida", Assert.IsType<DesfechoDoCommitDto.SemEfeito>(
                await gerenciador.CommitarAsync(w, new(w.ShaBase, aprovado, invalido, "m"))).Motivo);

        // Conteúdo alterado depois da aprovação: recusado antes do add.
        File.AppendAllText(Path.Combine(w.Caminho, "novo.txt"), "mais\n");
        Assert.IsType<DesfechoDoCommitDto.SemEfeito>(await gerenciador.CommitarAsync(w, new(w.ShaBase, aprovado, Autor, "m")));
        Assert.Equal(w.ShaBase, RepositorioGitDeTeste.Git(w.Caminho, "rev-parse", "HEAD"));
        Assert.Equal(estado, Estado(w));

        // Sem alterações não há o que commitar.
        var (_, limpa, outro) = await PrepararAsync("limpo", 2);
        var vazio = (await outro.ResumirAlteracoesAsync(limpa, 0)).Hash;
        Assert.Contains("não tem alterações", Assert.IsType<DesfechoDoCommitDto.SemEfeito>(
            await outro.CommitarAsync(limpa, new(limpa.ShaBase, vazio, Autor, "m"))).Motivo);
    }

    [Fact]
    public async Task SegundoCommitDaMesmaEntregaERecusadoPelaBase()
    {
        var (_, w, gerenciador) = await PrepararAsync();
        File.WriteAllText(Path.Combine(w.Caminho, "codigo.txt"), "alterado\n");
        var aprovado = (await gerenciador.ResumirAlteracoesAsync(w, 0)).Hash;
        Assert.IsType<DesfechoDoCommitDto.Commitado>(await gerenciador.CommitarAsync(w, new(w.ShaBase, aprovado, Autor, "m")));
        // Só alterações rastreadas: o diff da worktree contra a base continua o mesmo, mas o HEAD já andou.
        Assert.Equal(aprovado, (await gerenciador.ResumirAlteracoesAsync(w, 0)).Hash);
        var repetido = await gerenciador.CommitarAsync(w, new(w.ShaBase, aprovado, Autor, "m"));
        Assert.Contains("base aprovada", Assert.IsType<DesfechoDoCommitDto.SemEfeito>(repetido).Motivo);
        Assert.Equal("1", RepositorioGitDeTeste.Git(w.Caminho, "rev-list", "--count", $"{w.ShaBase}..HEAD"));
    }

    [Theory]
    [InlineData("hash-object")]
    [InlineData("update-index")]
    [InlineData("write-tree")]
    [InlineData("commit-tree")]
    [InlineData("update-ref")]
    public async Task FalhaDoGitAntesDoCommitNaoTocaNadaESemEfeito(string subcomando)
    {
        if (OperatingSystem.IsWindows()) return;
        var (repo, w, _) = await PrepararAsync();
        var gerenciador = ComGitFalso(repo, Falhar(subcomando));
        File.WriteAllText(Path.Combine(w.Caminho, "codigo.txt"), "alterado\n");
        File.WriteAllText(Path.Combine(w.Caminho, "novo.txt"), "novo\n");
        var aprovado = (await gerenciador.ResumirAlteracoesAsync(w, 0)).Hash;
        var estado = Estado(w);

        var desfecho = await gerenciador.CommitarAsync(w, new(w.ShaBase, aprovado, Autor, "m"));

        Assert.Matches("nada foi feito|nenhum commit foi feito", Assert.IsType<DesfechoDoCommitDto.SemEfeito>(desfecho).Motivo);
        Assert.Equal(w.ShaBase, RepositorioGitDeTeste.Git(w.Caminho, "rev-parse", "HEAD"));
        Assert.Equal(estado, Estado(w));
    }

    // Alguém escreve na worktree enquanto a árvore é montada, antes de os bytes serem lidos: o hash da árvore não é o
    // aprovado, e nada é commitado.
    [Fact]
    public async Task ConteudoAlteradoDuranteORetratoERecusadoPeloHashDaArvore()
    {
        if (OperatingSystem.IsWindows()) return;
        var (repo, w, _) = await PrepararAsync();
        var gerenciador = ComGitFalso(repo,
            $"for a in \"$@\"; do if [ \"$a\" = \"ls-files\" ]; then echo intruso >> '{w.Caminho}/novo.txt'; fi; done\n");
        File.WriteAllText(Path.Combine(w.Caminho, "novo.txt"), "novo\n");
        var aprovado = (await gerenciador.ResumirAlteracoesAsync(w, 0)).Hash;

        var desfecho = await gerenciador.CommitarAsync(w, new(w.ShaBase, aprovado, Autor, "m"));

        Assert.Contains("mudou depois da aprovação", Assert.IsType<DesfechoDoCommitDto.SemEfeito>(desfecho).Motivo);
        Assert.Equal(w.ShaBase, RepositorioGitDeTeste.Git(w.Caminho, "rev-parse", "HEAD"));
        Assert.Equal("?? novo.txt", Estado(w));
    }

    // Alguém escreve na worktree depois de a árvore ser conferida: o commit grava a árvore conferida (o conteúdo
    // aprovado), nunca o que foi escrito depois, que fica como alteração da worktree.
    [Fact]
    public async Task ConteudoAlteradoDepoisDoRetratoNaoEntraNoCommit()
    {
        if (OperatingSystem.IsWindows()) return;
        var (repo, w, _) = await PrepararAsync();
        var gerenciador = ComGitFalso(repo,
            $"for a in \"$@\"; do if [ \"$a\" = \"commit-tree\" ]; then echo intruso >> '{w.Caminho}/novo.txt'; fi; done\n");
        File.WriteAllText(Path.Combine(w.Caminho, "novo.txt"), "novo\n");
        var aprovado = (await gerenciador.ResumirAlteracoesAsync(w, 0)).Hash;

        var desfecho = await gerenciador.CommitarAsync(w, new(w.ShaBase, aprovado, Autor, "m"));

        Assert.IsType<DesfechoDoCommitDto.Commitado>(desfecho);
        Assert.Equal("novo", RepositorioGitDeTeste.Git(w.Caminho, "show", "HEAD:novo.txt"));
        Assert.Equal("M novo.txt", Estado(w));
    }

    [Fact]
    public async Task VariaveisGitHerdadasNaoChegamAoGit()
    {
        if (OperatingSystem.IsWindows()) return;
        var (repo, w, _) = await PrepararAsync();
        // GIT_DIR, GIT_INDEX_FILE ou GIT_CONFIG_PARAMETERS no ambiente do Worker redirecionariam os comandos; uma sonda
        // inofensiva (o Git a ignora) mostra que nenhuma GIT_* herdada chega ao processo.
        var gerenciador = ComGitFalso(repo, "if [ -n \"$GIT_DANTE_SONDA_311\" ]; then echo 'vazou' >&2; exit 99; fi\n");
        File.WriteAllText(Path.Combine(w.Caminho, "codigo.txt"), "alterado\n");
        Environment.SetEnvironmentVariable("GIT_DANTE_SONDA_311", "vazou");
        try
        {
            var aprovado = (await gerenciador.ResumirAlteracoesAsync(w, 0)).Hash;
            Assert.IsType<DesfechoDoCommitDto.Commitado>(await gerenciador.CommitarAsync(w, new(w.ShaBase, aprovado, Autor, "m")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GIT_DANTE_SONDA_311", null);
        }
    }

    [Fact]
    public async Task ErroRelatadoDepoisDeMoverABranchEConferidoPeloEstadoReal()
    {
        if (OperatingSystem.IsWindows()) return;
        var (repo, w, _) = await PrepararAsync();
        // A branch é movida e o processo "falha" em seguida: o estado real (commit novo, filho da base, árvore conferida)
        // é o que vale.
        var gerenciador = ComGitFalso(repo,
            "for a in \"$@\"; do if [ \"$a\" = \"update-ref\" ]; then git \"$@\"; echo 'fatal: falha simulada' >&2; exit 1; fi; done\n");
        File.WriteAllText(Path.Combine(w.Caminho, "codigo.txt"), "alterado\n");
        var aprovado = (await gerenciador.ResumirAlteracoesAsync(w, 0)).Hash;

        var desfecho = await gerenciador.CommitarAsync(w, new(w.ShaBase, aprovado, Autor, "m"));

        Assert.Equal(RepositorioGitDeTeste.Git(w.Caminho, "rev-parse", "HEAD"),
            Assert.IsType<DesfechoDoCommitDto.Commitado>(desfecho).Sha);
        Assert.Equal("1", RepositorioGitDeTeste.Git(w.Caminho, "rev-list", "--count", $"{w.ShaBase}..HEAD"));
    }

    [Fact]
    public async Task EstadoIlegivelDepoisDeMoverABranchEIncertoENadaERepetido()
    {
        if (OperatingSystem.IsWindows()) return;
        var (repo, w, _) = await PrepararAsync();
        // Depois do update-ref, o Git deixa de responder às consultas: o efeito pode existir e não há como conferir.
        var marca = Path.Combine(temp.Raiz, "marca-" + Guid.NewGuid().ToString("N")[..8]);
        var gerenciador = ComGitFalso(repo,
            $"if [ -e '{marca}' ]; then echo 'fatal: falha simulada' >&2; exit 128; fi\n" +
            $"for a in \"$@\"; do if [ \"$a\" = \"update-ref\" ]; then git \"$@\"; touch '{marca}'; exit 1; fi; done\n");
        File.WriteAllText(Path.Combine(w.Caminho, "codigo.txt"), "alterado\n");
        var aprovado = (await gerenciador.ResumirAlteracoesAsync(w, 0)).Hash;

        var desfecho = await gerenciador.CommitarAsync(w, new(w.ShaBase, aprovado, Autor, "m"));

        var incerto = Assert.IsType<DesfechoDoCommitDto.Incerto>(desfecho);
        Assert.Contains("nada será repetido", incerto.Motivo);
        Assert.Contains("HEAD ilegível", incerto.EstadoObservado);
        // O estado real, lido depois por um Git que responde, é o que a recuperação relata.
        var head = RepositorioGitDeTeste.Git(w.Caminho, "rev-parse", "HEAD");
        var real = await new GerenciadorDeWorktreesGit(new OpcoesDeWorktrees { Raiz = temp.RaizDasWorktrees },
            new CatalogoFixo(repo)).DescreverEstadoAsync(w);
        Assert.Contains($"HEAD {head[..12]} na {w.Branch}", real);
        Assert.Contains($"1 commit(s) além da base {w.ShaBase[..12]}", real);
        // A descrição do estado não lança nem com a worktree ausente.
        Assert.Equal("A worktree não existe mais.", await gerenciador.DescreverEstadoAsync(
            w with { Caminho = Path.Combine(Path.GetDirectoryName(w.Caminho)!, "E99") }));
    }

    // Outra entrega move a branch no meio (ao criar o objeto commit, ou imediatamente antes do update-ref): o
    // compare-and-swap do Git contra a base recusa esta, e a outra não é sobrescrita.
    [Theory]
    [InlineData("commit-tree")]
    [InlineData("update-ref")]
    public async Task BranchMovidaPorOutraEntregaNoMeioNaoGanhaSegundoCommit(string momento)
    {
        if (OperatingSystem.IsWindows()) return;
        var (repo, w, _) = await PrepararAsync();
        var gerenciador = ComGitFalso(repo,
            $"for a in \"$@\"; do if [ \"$a\" = \"{momento}\" ]; then " +
            $"o=$(GIT_AUTHOR_NAME=o GIT_AUTHOR_EMAIL=o@o GIT_COMMITTER_NAME=o GIT_COMMITTER_EMAIL=o@o git commit-tree -p {w.ShaBase} -m outra '{w.ShaBase}^{{tree}}'); " +
            $"git update-ref 'refs/heads/{w.Branch}' \"$o\" {w.ShaBase}; fi; done\n");
        File.WriteAllText(Path.Combine(w.Caminho, "codigo.txt"), "alterado\n");
        var aprovado = (await gerenciador.ResumirAlteracoesAsync(w, 0)).Hash;

        var desfecho = await gerenciador.CommitarAsync(w, new(w.ShaBase, aprovado, Autor, "m"));

        Assert.Contains("outra entrega", Assert.IsType<DesfechoDoCommitDto.SemEfeito>(desfecho).Motivo);
        Assert.Equal("1", RepositorioGitDeTeste.Git(w.Caminho, "rev-list", "--count", $"{w.ShaBase}..HEAD"));
        Assert.Equal("outra", RepositorioGitDeTeste.Git(w.Caminho, "log", "-1", "--format=%s"));
    }

    // Outra entrega move a branch e, em seguida, o commit-tree desta falha: esta fica sem efeito e não mexe no index.
    [Fact]
    public async Task FalhaDepoisQueOutraEntregaMoveuABranchNaoMexeNoIndex()
    {
        if (OperatingSystem.IsWindows()) return;
        var (repo, w, _) = await PrepararAsync();
        var gerenciador = ComGitFalso(repo,
            "for a in \"$@\"; do if [ \"$a\" = \"commit-tree\" ]; then " +
            $"o=$(GIT_AUTHOR_NAME=o GIT_AUTHOR_EMAIL=o@o GIT_COMMITTER_NAME=o GIT_COMMITTER_EMAIL=o@o git commit-tree -p {w.ShaBase} -m outra '{w.ShaBase}^{{tree}}'); " +
            $"git update-ref 'refs/heads/{w.Branch}' \"$o\" {w.ShaBase}; echo 'fatal: falha simulada' >&2; exit 128; fi; done\n");
        File.WriteAllText(Path.Combine(w.Caminho, "codigo.txt"), "alterado\n");
        var aprovado = (await gerenciador.ResumirAlteracoesAsync(w, 0)).Hash;

        var desfecho = await gerenciador.CommitarAsync(w, new(w.ShaBase, aprovado, Autor, "m"));

        Assert.Contains("nenhum commit foi feito", Assert.IsType<DesfechoDoCommitDto.SemEfeito>(desfecho).Motivo);
        Assert.Equal("outra", RepositorioGitDeTeste.Git(w.Caminho, "log", "-1", "--format=%s"));
        // O index da worktree não foi tocado por esta entrega: a alteração continua só nos arquivos.
        Assert.Equal("M codigo.txt", Estado(w));
    }

    // Revisão do PR #326, ponto 1: um filtro de conteúdo (clean/smudge/process, como o do Git LFS) é um comando externo
    // que o Git roda ao converter arquivo em objeto. Já configurado, a entrega o recusa com o motivo: o comando nunca
    // roda (a marca não é criada), nada é preparado nem commitado, e a observação do estado também não o aciona.
    [Theory]
    [InlineData("clean")]
    [InlineData("process")]
    public async Task FiltroDeConteudoConfiguradoNuncaRodaEAEntregaERecusada(string tipo)
    {
        if (OperatingSystem.IsWindows()) return;
        var (repo, w, gerenciador) = await PrepararAsync();
        File.WriteAllText(Path.Combine(w.Caminho, "codigo.txt"), "alterado\n");
        var aprovado = (await gerenciador.ResumirAlteracoesAsync(w, 0)).Hash;
        var marca = Path.Combine(temp.Raiz, "filtro-rodou");
        // O filtro vale para todos os arquivos (atributo no diretório Git comum) e marca quando roda.
        RepositorioGitDeTeste.Git(repo.Caminho, "config", $"filter.perigoso.{tipo}", $"sh -c 'touch {marca}; cat'");
        File.WriteAllText(Path.Combine(repo.Caminho, ".git", "info", "attributes"), "* filter=perigoso\n");

        var desfecho = await gerenciador.CommitarAsync(w, new(w.ShaBase, aprovado, Autor, "m"));
        var leitura = await Assert.ThrowsAsync<WorktreeInvalidaException>(() => gerenciador.LerArtefatoAsync(w));
        var estado = await gerenciador.DescreverEstadoAsync(w);

        Assert.False(File.Exists(marca), "o filtro rodou");
        Assert.Contains($"filtros de conteúdo configurados (filter.perigoso.{tipo})",
            Assert.IsType<DesfechoDoCommitDto.SemEfeito>(desfecho).Motivo);
        Assert.Contains("nada foi feito", leitura.Message);
        Assert.Contains("0 arquivo(s) no index diferentes do HEAD (os arquivos da worktree não são lidos)", estado);
        Assert.Contains("0 commit(s) além da base", estado);
        // Sem o filtro, nada tinha sido preparado: o index continua o da base.
        File.Delete(Path.Combine(repo.Caminho, ".git", "info", "attributes"));
        RepositorioGitDeTeste.Git(repo.Caminho, "config", "--unset", $"filter.perigoso.{tipo}");
        Assert.Equal("", RepositorioGitDeTeste.Git(w.Caminho, "diff", "--cached", "--name-only"));
        Assert.Equal("codigo.txt", RepositorioGitDeTeste.Git(w.Caminho, "diff", "--name-only"));
        Assert.False(File.Exists(marca));
    }

    // Revisão do PR #326, ponto 3: outro processo altera o index (aqui, o privado em que a árvore é montada) imediatamente
    // antes do write-tree. A conferência é da árvore já escrita (imutável, a mesma que o commit-tree recebe), então a
    // alteração é recusada e nada é commitado.
    [Fact]
    public async Task IndexAlteradoAntesDoWriteTreeERecusadoPelaArvoreConferida()
    {
        if (OperatingSystem.IsWindows()) return;
        var (repo, w, _) = await PrepararAsync();
        var gerenciador = ComGitFalso(repo,
            "for a in \"$@\"; do if [ \"$a\" = \"write-tree\" ]; then " +
            "git update-index --add --cacheinfo \"100644,$(echo intruso | git hash-object -w --stdin),intruso.txt\"; fi; done\n");
        File.WriteAllText(Path.Combine(w.Caminho, "novo.txt"), "novo\n");
        var aprovado = (await gerenciador.ResumirAlteracoesAsync(w, 0)).Hash;

        var desfecho = await gerenciador.CommitarAsync(w, new(w.ShaBase, aprovado, Autor, "m"));

        Assert.Contains("mudou depois da aprovação", Assert.IsType<DesfechoDoCommitDto.SemEfeito>(desfecho).Motivo);
        Assert.Equal("?? novo.txt", Estado(w));
        Assert.Equal(w.ShaBase, RepositorioGitDeTeste.Git(w.Caminho, "rev-parse", "HEAD"));
        Assert.Equal($"refs/heads/{w.Branch}", "refs/heads/" + RepositorioGitDeTeste.Git(w.Caminho, "branch", "--show-current"));
    }

    // Revisão 2 do PR #326: a configuração de filtros muda entre a conferência e os comandos seguintes. O Git falso é o
    // adversário: a conferência (config --get-regexp) sempre vê a configuração sem o filtro, e todo outro comando Git roda
    // com o filtro configurado para todos os arquivos. Ler o artefato, commitar e observar o estado não rodam o filtro (a
    // marca nunca é criada), e o commit grava exatamente o conteúdo aprovado. O controle no fim prova que o filtro estava
    // armado: comandos que convertem arquivo em objeto o rodam.
    [Theory]
    [InlineData("clean")]
    [InlineData("smudge")]
    [InlineData("process")]
    public async Task FiltroConfiguradoDepoisDaConferenciaNuncaRoda(string tipo)
    {
        if (OperatingSystem.IsWindows()) return;
        var (repo, w, gerenciador) = await PrepararAsync();
        File.WriteAllText(Path.Combine(w.Caminho, "codigo.txt"), "alterado\n");
        File.WriteAllText(Path.Combine(w.Caminho, "novo.txt"), "novo\n");
        File.Delete(Path.Combine(w.Caminho, "README.md"));
        var aprovado = (await gerenciador.ResumirAlteracoesAsync(w, 0)).Hash;
        var marca = Path.Combine(temp.Raiz, "filtro-rodou");
        var configuracao = Path.Combine(repo.Caminho, ".git", "config");
        var comando = tipo == "process" ? $"sh -c 'touch {marca}; exit 1'" : $"sh -c 'touch {marca}; cat'";
        File.WriteAllText(Path.Combine(repo.Caminho, ".git", "info", "attributes"), "* filter=perigoso\n");
        var adversario = ComGitFalso(repo,
            $"case \"$*\" in *--get-regexp*) git config --file '{configuracao}' --unset-all filter.perigoso.{tipo} ;; " +
            $"*) git config --file '{configuracao}' filter.perigoso.{tipo} \"{comando}\" ;; esac\n");

        var artefato = await adversario.LerArtefatoAsync(w);
        var desfecho = await adversario.CommitarAsync(w, new(w.ShaBase, aprovado, Autor, "m"));
        var estado = await adversario.DescreverEstadoAsync(w);

        Assert.False(File.Exists(marca), "o filtro rodou");
        Assert.Equal((w.ShaBase, aprovado, false), (artefato.Head, artefato.Hash, artefato.Vazio));
        var commitado = Assert.IsType<DesfechoDoCommitDto.Commitado>(desfecho);
        Assert.Equal(w.ShaBase, RepositorioGitDeTeste.Git(w.Caminho, "rev-parse", $"{commitado.Sha}^1"));
        Assert.Equal("alterado", RepositorioGitDeTeste.Git(w.Caminho, "cat-file", "blob", "HEAD:codigo.txt"));
        Assert.Equal("novo", RepositorioGitDeTeste.Git(w.Caminho, "cat-file", "blob", "HEAD:novo.txt"));
        Assert.Equal("D\tREADME.md\nM\tcodigo.txt\nA\tnovo.txt",
            RepositorioGitDeTeste.Git(w.Caminho, "diff", "--name-status", "--no-renames", w.ShaBase, "HEAD"));
        Assert.Contains("1 commit(s) além da base", estado);
        Assert.Contains("0 arquivo(s) no index diferentes do HEAD", estado);
        // Controle: o filtro continua configurado e roda em quem converte arquivo em objeto (hash-object de um caminho)
        // ou objeto em arquivo (cat-file --filters).
        Assert.Equal(comando, RepositorioGitDeTeste.Git(repo.Caminho, "config", "--get", $"filter.perigoso.{tipo}"));
        GitSemConferir(w.Caminho, "hash-object", "codigo.txt");
        GitSemConferir(w.Caminho, "cat-file", "--filters", "HEAD:codigo.txt");
        Assert.True(File.Exists(marca), "o controle não acionou o filtro: o teste não provaria nada");
    }

    // Todo processo Git que a entrega abre (ler o artefato, commitar e observar o estado) está no conjunto que não lê
    // arquivo da worktree; a validação da worktree só lista as worktrees do repositório.
    [Fact]
    public async Task EntregaNaoAbreComandoGitQueLeiaAWorktree()
    {
        if (OperatingSystem.IsWindows()) return;
        var (repo, w, _) = await PrepararAsync();
        var registro = Path.Combine(temp.Raiz, "comandos-" + Guid.NewGuid().ToString("N")[..8]);
        var gerenciador = ComGitFalso(repo, $"for a in \"$@\"; do printf '%s\\037' \"$a\" >> '{registro}'; done; " +
            $"printf '\\n' >> '{registro}'\n");
        File.WriteAllText(Path.Combine(w.Caminho, "codigo.txt"), "alterado\n");
        File.WriteAllText(Path.Combine(w.Caminho, "novo.txt"), "novo\n");
        var aprovado = (await new GerenciadorDeWorktreesGit(new OpcoesDeWorktrees { Raiz = temp.RaizDasWorktrees },
            new CatalogoFixo(repo)).ResumirAlteracoesAsync(w, 0)).Hash;

        Assert.Equal(aprovado, (await gerenciador.LerArtefatoAsync(w)).Hash);
        Assert.IsType<DesfechoDoCommitDto.Commitado>(await gerenciador.CommitarAsync(w, new(w.ShaBase, aprovado, Autor, "m")));
        await gerenciador.DescreverEstadoAsync(w);

        var comandos = File.ReadAllLines(registro)
            .Select(linha => linha.Split('\u001f', StringSplitOptions.RemoveEmptyEntries).Skip(10).ToArray()).ToArray();
        Assert.Contains(comandos, c => c is ["hash-object", ..]);
        Assert.Contains(comandos, c => c is ["commit-tree", ..]);
        Assert.All(comandos, c => Assert.True(GerenciadorDeWorktreesGit.SemLeituraDaWorktree(c) ||
            c is ["worktree", "list", "--porcelain"], string.Join(' ', c)));
    }

    [Theory]
    [InlineData(true, "hash-object", "-w", "--no-filters", "--stdin")]
    [InlineData(true, "update-index", "-z", "--index-info")]
    [InlineData(true, "ls-files", "--others", "--exclude-standard", "-z")]
    [InlineData(true, "ls-tree", "-r", "-z", "--full-tree", "0123456789abcdef0123456789abcdef01234567")]
    [InlineData(true, "diff-index", "--cached", "--name-only", "-z", "HEAD")]
    [InlineData(true, "diff", "--no-ext-diff", "--no-textconv", "--no-color", "--binary", "--no-renames", "--diff-filter=a",
        "0123456789abcdef0123456789abcdef01234567", "89abcdef0123456789abcdef0123456789abcdef")]
    [InlineData(true, "config", "--null", "--get-regexp", GerenciadorDeWorktreesGit.ExpressaoDosFiltros)]
    [InlineData(false, "add", "--all")]
    [InlineData(false, "status", "--porcelain", "--untracked-files=all")]
    [InlineData(false, "diff", "--no-ext-diff", "--no-textconv", "--no-color", "--binary",
        "0123456789abcdef0123456789abcdef01234567")]
    [InlineData(false, "worktree", "list", "--porcelain")]
    public void EntregaSoUsaComandosQueNaoLeemAWorktree(bool permitido, params string[] argumentos) =>
        Assert.Equal(permitido, GerenciadorDeWorktreesGit.SemLeituraDaWorktree(argumentos));

    [Theory]
    [InlineData(false, "add", "--all")]
    [InlineData(true, "write-tree")]
    [InlineData(true, "hash-object", "-w", "--no-filters", "--stdin")]
    [InlineData(true, "update-index", "-z", "--index-info")]
    [InlineData(true, "ls-tree", "-r", "-z", "--full-tree", "0123456789abcdef0123456789abcdef01234567")]
    [InlineData(true, "diff-index", "--cached", "--name-only", "-z", "HEAD")]
    [InlineData(false, "hash-object", "-w", "--stdin")]
    [InlineData(false, "hash-object", "-w", "--no-filters", "codigo.txt")]
    [InlineData(false, "hash-object", "-w", "--stdin-paths")]
    [InlineData(false, "update-index", "--add", "codigo.txt")]
    [InlineData(false, "cat-file", "--filters", "HEAD:codigo.txt")]
    [InlineData(false, "ls-files", "-m")]
    [InlineData(true, "read-tree", "0123456789abcdef0123456789abcdef01234567")]
    [InlineData(true, "cat-file", "blob", "0123456789abcdef0123456789abcdef01234567")]
    [InlineData(true, "commit-tree", "--no-gpg-sign", "-p", "0123456789abcdef0123456789abcdef01234567", "-m", "qualquer",
        "89abcdef0123456789abcdef0123456789abcdef")]
    [InlineData(true, "update-ref", "-m", GerenciadorDeWorktreesGit.MensagemDoReflog, "refs/heads/dante/dev/e7",
        "0123456789abcdef0123456789abcdef01234567", "89abcdef0123456789abcdef0123456789abcdef")]
    [InlineData(true, "config", "--null", "--get-regexp", GerenciadorDeWorktreesGit.ExpressaoDosFiltros)]
    [InlineData(false, "config", "--null", "--get-regexp", "^user\\.")]
    [InlineData(false, "config", "filter.x.clean", "cat")]
    [InlineData(false, "diff", "--cached", "--no-ext-diff", "--no-textconv", "--no-color", "--binary", "--no-renames",
        "--diff-filter=a", "0123456789abcdef0123456789abcdef01234567")]
    [InlineData(false, "commit", "-m", "x")]
    [InlineData(false, "commit-tree", "-p", "0123456789abcdef0123456789abcdef01234567", "-m", "x",
        "89abcdef0123456789abcdef0123456789abcdef")]
    [InlineData(false, "commit-tree", "--no-gpg-sign", "-p", "HEAD", "-m", "x", "89abcdef0123456789abcdef0123456789abcdef")]
    [InlineData(false, "update-ref", "-m", GerenciadorDeWorktreesGit.MensagemDoReflog, "refs/heads/main",
        "0123456789abcdef0123456789abcdef01234567", "89abcdef0123456789abcdef0123456789abcdef")]
    [InlineData(false, "update-ref", "-m", GerenciadorDeWorktreesGit.MensagemDoReflog, "refs/heads/dante/dev/e7",
        "0123456789abcdef0123456789abcdef01234567")]
    [InlineData(false, "update-ref", "-d", "refs/heads/dante/dev/e7")]
    [InlineData(false, "update-ref", "-m", GerenciadorDeWorktreesGit.MensagemDoReflog, "refs/remotes/origin/dante/dev/e7",
        "0123456789abcdef0123456789abcdef01234567", "89abcdef0123456789abcdef0123456789abcdef")]
    [InlineData(false, "add", ".")]
    [InlineData(false, "add", "--all", "--force")]
    [InlineData(false, "read-tree", "-u", "HEAD")]
    [InlineData(false, "read-tree", "HEAD")]
    [InlineData(false, "cat-file", "-p", "HEAD")]
    [InlineData(false, "push", "origin", "dante/dev/e1")]
    [InlineData(false, "reset", "--quiet")]
    [InlineData(false, "checkout", "-b", "x")]
    [InlineData(false, "merge", "main")]
    public void SoOConjuntoFechadoDaEntregaERodado(bool permitido, params string[] argumentos) =>
        Assert.Equal(permitido, GerenciadorDeWorktreesGit.Permitido(argumentos));

    // ---- apoio ----

    private async Task<(RepositorioCadastrado Repo, WorktreePlanejada Worktree, GerenciadorDeWorktreesGit Gerenciador)>
        PrepararAsync(string nome = "dante", long numero = 7)
    {
        var repo = temp.Criar(nome);
        var gerenciador = new GerenciadorDeWorktreesGit(new OpcoesDeWorktrees { Raiz = temp.RaizDasWorktrees },
            new CatalogoFixo(repo));
        var w = await gerenciador.PlanejarAsync(repo, numero, "dev");
        await gerenciador.CriarOuAdotarAsync(w);
        return (repo, w, gerenciador);
    }

    // Um remoto bare com a main publicada: a entrega nunca o alcança.
    private string Remoto(RepositorioCadastrado repo)
    {
        var remoto = Path.Combine(temp.Raiz, Path.GetFileName(repo.Caminho) + "-remoto.git");
        RepositorioGitDeTeste.Git(temp.Raiz, "init", "-q", "--bare", remoto);
        RepositorioGitDeTeste.Git(repo.Caminho, "remote", "add", "origin", remoto);
        RepositorioGitDeTeste.Git(repo.Caminho, "push", "-q", "origin", "main");
        // O push de teste cria refs/remotes/origin/main; a impressão do checkout não as inclui, mas a conferência final sim.
        RepositorioGitDeTeste.Git(repo.Caminho, "update-ref", "-d", "refs/remotes/origin/main");
        return remoto;
    }

    private static string Estado(WorktreePlanejada w) =>
        RepositorioGitDeTeste.Git(w.Caminho, "status", "--porcelain", "--untracked-files=all");

    // Git real sem conferir o código de saída (o filtro "process" do controle termina com erro de propósito).
    private static void GitSemConferir(string cwd, params string[] argumentos)
    {
        var inicio = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argumento in argumentos) inicio.ArgumentList.Add(argumento);
        using var processo = System.Diagnostics.Process.Start(inicio)!;
        processo.StandardOutput.ReadToEnd();
        processo.StandardError.ReadToEnd();
        processo.WaitForExit();
    }

    private static string Falhar(string subcomando) =>
        $"for a in \"$@\"; do if [ \"$a\" = \"{subcomando}\" ]; then echo 'fatal: falha simulada' >&2; exit 128; fi; done\n";

    // Git falso com o comportamento escrito no próprio script (sem variável de ambiente compartilhada entre testes):
    // o trecho dado roda antes de delegar ao git real.
    private GerenciadorDeWorktreesGit ComGitFalso(RepositorioCadastrado repo, string trecho)
    {
        var falso = Path.Combine(temp.Raiz, "git-falso-" + Guid.NewGuid().ToString("N")[..8]);
        File.WriteAllText(falso, "#!/bin/sh\n" + trecho + "exec git \"$@\"\n", new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(falso, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return new GerenciadorDeWorktreesGit(new OpcoesDeWorktrees { Raiz = temp.RaizDasWorktrees }, new CatalogoFixo(repo),
            executavel: falso);
    }
}
