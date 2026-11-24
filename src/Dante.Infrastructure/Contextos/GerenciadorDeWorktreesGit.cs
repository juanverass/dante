using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Dante.Application.Contextos;
using Dante.Application.Execucoes;

namespace Dante.Infrastructure.Contextos;

// Worktrees de execução de escrita (AD-71, #307). Conjunto fechado de comandos Git, conferido antes de cada processo:
// worktree add/remove/list e consultas somente leitura (rev-parse, check-ref-format, status, rev-list). ArgumentList,
// sem shell, core.fsmonitor=false e core.hooksPath=/dev/null (nem a configuração nem os hooks do repositório executam
// comando por esta via), prazo por comando e nunca --force: a recusa do Git em remover worktree suja é a última
// barreira. Caminhos são comparados já canônicos (CaminhoReal): link em qualquer componente da raiz ou da worktree,
// traversal, sobreposição com checkout cadastrado ou diretório Git comum diferente do repositório recusam (fail-closed).
// Layout: <raiz>/<alias sem @>/E<número>, branch dante/<handle>/e<número> a partir do HEAD do checkout principal.
// Entrega (#311, PR 3): a única escrita Git do D.A.N.T.E. é o commit local do artefato aprovado na branch da worktree
// (blobs gravados sem filtro, árvore montada num index privado, commit-tree e update-ref da branch da execução com o
// valor antigo; read-tree do index da worktree só depois do commit), sem push, reset, checkout, merge nem criação de
// branch. Nenhum processo Git da entrega lê arquivo da worktree (SemLeituraDaWorktree), então nenhum filtro de conteúdo
// roda, qualquer que seja a configuração. Variáveis GIT_* herdadas não alcançam nenhum processo (não redirecionam
// diretório, index nem configuração).
public sealed partial class GerenciadorDeWorktreesGit(
    OpcoesDeWorktrees opcoes,
    ICatalogoDeRepositorios catalogo,
    TimeSpan? limite = null,
    string executavel = "git") : IGerenciadorDeWorktrees, ICommitDeWorktree
{
    private readonly TimeSpan limite = limite ?? TimeSpan.FromSeconds(30);

    public async Task<WorktreePlanejada> PlanejarAsync(RepositorioCadastrado repositorio, long numeroDaExecucao,
        string handle, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repositorio);
        if (numeroDaExecucao < 1) throw new WorktreeInvalidaException("Número de execução inválido.");
        if (!AliasValido().IsMatch(repositorio.Alias)) throw new WorktreeInvalidaException("Alias de repositório inválido.");
        if (!HandleValido().IsMatch(handle)) throw new WorktreeInvalidaException("Handle de agente inválido para a branch.");
        var checkout = Canonico(repositorio.Caminho, "checkout do repositório");
        if (!Directory.Exists(checkout)) throw new WorktreeInvalidaException($"O checkout de {repositorio.Alias} não existe.");
        var raizGit = await ConsultarAsync(checkout, cancellationToken, "rev-parse", "--show-toplevel");
        if (raizGit is null || Canonico(raizGit, "raiz Git") != checkout)
            throw new WorktreeInvalidaException($"{repositorio.Alias} não é a raiz de um repositório Git.");
        var sha = await ConsultarAsync(checkout, cancellationToken, "rev-parse", "--verify", "--quiet", "HEAD^{commit}");
        if (sha is not { Length: 40 } || !sha.All(Uri.IsHexDigit))
            throw new WorktreeInvalidaException($"{repositorio.Alias} não tem commit em HEAD para servir de base.");
        var branch = $"dante/{handle}/e{numeroDaExecucao}";
        if (await ConsultarAsync(checkout, cancellationToken, "check-ref-format", "--branch", branch) is null)
            throw new WorktreeInvalidaException("Nome de branch inválido para a worktree.");
        var diretorioDoAlias = DiretorioDoAlias(repositorio.Alias);
        var caminho = Path.Combine(diretorioDoAlias, $"E{numeroDaExecucao}");
        // Traversal: o caminho montado continua exatamente dois níveis abaixo da raiz canônica.
        if (Path.GetDirectoryName(Path.GetFullPath(caminho)) != diretorioDoAlias ||
            Path.GetDirectoryName(diretorioDoAlias) != Raiz())
            throw new WorktreeInvalidaException("Caminho da worktree fora da raiz autorizada.");
        ConferirForaDosCheckouts(caminho);
        return new WorktreePlanejada(repositorio.Alias.ToLowerInvariant(), checkout, caminho, branch,
            sha.ToLowerInvariant());
    }

    public async Task CriarOuAdotarAsync(WorktreePlanejada worktree, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(worktree);
        ConferirForma(worktree);
        var diretorioDoAlias = DiretorioDoAlias(worktree.Alias);
        Directory.CreateDirectory(diretorioDoAlias);
        // Criado agora ou já existente: nenhum dos dois pode ser um link (o caminho real precisa ser o próprio).
        if (Canonico(diretorioDoAlias, "diretório do alias") != diretorioDoAlias)
            throw new WorktreeInvalidaException("O diretório das worktrees do repositório é um link; nada foi criado.");
        if (Directory.Exists(worktree.Caminho) || File.Exists(worktree.Caminho) || EhLink(worktree.Caminho))
        {
            // Morte do Worker entre o add e a gravação de Pronta: só adota exatamente o que foi planejado.
            await ValidarAsync(worktree, cancellationToken);
            return;
        }
        var registrada = await ConsultarAsync(worktree.CaminhoDoRepositorio, cancellationToken, "rev-parse", "--verify",
            "--quiet", $"refs/heads/{worktree.Branch}");
        var resultado = registrada is null
            ? await GitAsync(worktree.CaminhoDoRepositorio, cancellationToken, "worktree", "add", "-b", worktree.Branch,
                worktree.Caminho, worktree.ShaBase)
            // Branch de uma tentativa anterior que não chegou a criar o diretório: só serve se ainda está na base.
            : string.Equals(registrada, worktree.ShaBase, StringComparison.OrdinalIgnoreCase)
                ? await GitAsync(worktree.CaminhoDoRepositorio, cancellationToken, "worktree", "add", worktree.Caminho,
                    worktree.Branch)
                : throw new WorktreeInvalidaException("A branch da worktree já existe fora da base; nada foi criado.");
        if (resultado.Codigo != 0)
            throw new WorktreeInvalidaException("O Git recusou criar a worktree" + Detalhe(resultado.Erro) + ".");
        await ValidarAsync(worktree, cancellationToken);
    }

    public async Task ValidarAsync(WorktreePlanejada worktree, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(worktree);
        ConferirForma(worktree);
        if (!Directory.Exists(worktree.Caminho)) throw new WorktreeInvalidaException("A worktree não existe.");
        if (Canonico(worktree.Caminho, "worktree") != worktree.Caminho)
            throw new WorktreeInvalidaException("O caminho da worktree passa por um link; nada é entregue ao agente.");
        ConferirForaDosCheckouts(worktree.Caminho);
        var topo = await ConsultarAsync(worktree.Caminho, cancellationToken, "rev-parse", "--show-toplevel");
        if (topo is null || Canonico(topo, "raiz Git da worktree") != worktree.Caminho)
            throw new WorktreeInvalidaException("O diretório não é a raiz de uma worktree Git.");
        var comum = await ConsultarAsync(worktree.Caminho, cancellationToken, "rev-parse", "--path-format=absolute",
            "--git-common-dir");
        var comumDoRepositorio = await ConsultarAsync(worktree.CaminhoDoRepositorio, cancellationToken, "rev-parse",
            "--path-format=absolute", "--git-common-dir");
        if (comum is null || comumDoRepositorio is null ||
            Canonico(comum, "diretório Git comum") != Canonico(comumDoRepositorio, "diretório Git comum"))
            throw new WorktreeInvalidaException("A worktree não pertence ao repositório cadastrado.");
        var branch = await ConsultarAsync(worktree.Caminho, cancellationToken, "symbolic-ref", "--quiet", "HEAD");
        if (branch != $"refs/heads/{worktree.Branch}")
            throw new WorktreeInvalidaException("A worktree não está na branch da execução.");
        // O Git do repositório precisa listar este caminho com esta branch (não basta um .git que aponte para lá).
        var lista = await GitAsync(worktree.CaminhoDoRepositorio, cancellationToken, "worktree", "list", "--porcelain");
        if (lista.Codigo != 0 || !Listada(lista.Saida, worktree))
            throw new WorktreeInvalidaException("O repositório não registra a worktree da execução.");
    }

    public async Task<DesfechoDaLimpeza> RemoverSePossivelAsync(WorktreePlanejada worktree,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(worktree);
        ConferirForma(worktree);
        if (!Directory.Exists(worktree.Caminho) && !File.Exists(worktree.Caminho) && !EhLink(worktree.Caminho))
        {
            // Diretório já ausente: o Git limpa só os metadados desta worktree, se ainda registrados.
            var lista = await GitAsync(worktree.CaminhoDoRepositorio, cancellationToken, "worktree", "list", "--porcelain");
            if (lista.Codigo == 0 && Listada(lista.Saida, worktree))
                await GitAsync(worktree.CaminhoDoRepositorio, cancellationToken, "worktree", "remove", worktree.Caminho);
            return new DesfechoDaLimpeza(true, null);
        }
        try
        {
            await ValidarAsync(worktree, cancellationToken);
        }
        catch (WorktreeInvalidaException exception)
        {
            return new DesfechoDaLimpeza(false, $"Preservada: {exception.Message}");
        }
        var status = await GitAsync(worktree.Caminho, cancellationToken, "status", "--porcelain", "--untracked-files=all");
        if (status.Codigo != 0) return new DesfechoDaLimpeza(false, "Preservada: não foi possível ler as alterações.");
        if (status.Saida.Length > 0) return new DesfechoDaLimpeza(false, "Preservada: há alterações não commitadas.");
        var adiante = await ConsultarAsync(worktree.Caminho, cancellationToken, "rev-list", "--count",
            $"{worktree.ShaBase}..HEAD");
        if (adiante is null) return new DesfechoDaLimpeza(false, "Preservada: não foi possível comparar com a base.");
        if (adiante != "0") return new DesfechoDaLimpeza(false, "Preservada: há commits além da base.");
        var remocao = await GitAsync(worktree.CaminhoDoRepositorio, cancellationToken, "worktree", "remove",
            worktree.Caminho);
        return remocao.Codigo == 0
            ? new DesfechoDaLimpeza(true, null)
            : new DesfechoDaLimpeza(false, "Preservada: o Git recusou remover a worktree" + Detalhe(remocao.Erro) + ".");
    }

    public async Task<AlteracoesDaWorktreeDto> ResumirAlteracoesAsync(WorktreePlanejada worktree, int limiteDoDiff,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(worktree);
        if (limiteDoDiff < 0) throw new ArgumentOutOfRangeException(nameof(limiteDoDiff));
        await ValidarAsync(worktree, cancellationToken);
        var diff = await GitAsync(worktree.Caminho, cancellationToken, "diff", "--no-ext-diff", "--no-textconv", "--no-color",
            "--binary", worktree.ShaBase);
        if (diff.Codigo != 0) throw new WorktreeInvalidaException("Não foi possível ler as alterações da worktree.");
        var novos = await GitAsync(worktree.Caminho, cancellationToken, "ls-files", "--others", "--exclude-standard", "-z");
        if (novos.Codigo != 0) throw new WorktreeInvalidaException("Não foi possível listar os arquivos novos da worktree.");
        var caminhos = novos.Saida.Split('\0', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal).ToArray();
        var raiz = Path.GetFullPath(worktree.Caminho) + Path.DirectorySeparatorChar;
        var novosComConteudo = new List<(string Caminho, string Conteudo)>(caminhos.Length);
        foreach (var relativo in caminhos)
        {
            var completo = Path.GetFullPath(Path.Combine(worktree.Caminho, relativo));
            if (!completo.StartsWith(raiz, StringComparison.Ordinal))
                throw new WorktreeInvalidaException("Arquivo novo fora da worktree.");
            var informacao = new FileInfo(completo);
            string conteudo;
            if (informacao.LinkTarget is { } destino)
                conteudo = "link:" + destino;
            else
            {
                await using var arquivo = File.OpenRead(completo);
                conteudo = Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(arquivo, cancellationToken));
            }
            novosComConteudo.Add((relativo, conteudo));
        }
        var cortado = diff.Saida.Length > limiteDoDiff;
        return new AlteracoesDaWorktreeDto(cortado ? diff.Saida[..limiteDoDiff] : diff.Saida, cortado, caminhos,
            HashCanonico(diff.Saida, novosComConteudo));
    }

    // Forma canônica do artefato (#308): o diff rastreado inteiro e, em ordem ordinal, caminho e conteúdo (SHA-256 dos
    // bytes, ou o destino de um link) de cada arquivo novo. A mesma função serve à worktree e ao index (entrega), para que
    // as duas leituras só coincidam com o mesmo conteúdo.
    private static string HashCanonico(string diff, IEnumerable<(string Caminho, string Conteudo)> novos)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        hash.AppendData(System.Text.Encoding.UTF8.GetBytes("formato=1\ndiff\n" + diff + "\0novos\n"));
        foreach (var (caminho, conteudo) in novos)
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(caminho + "\0" + conteudo + "\n"));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public async Task<ArtefatoDaWorktreeDto> LerArtefatoAsync(WorktreePlanejada worktree,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(worktree);
        var head = await HeadDaEntregaAsync(worktree, cancellationToken);
        // Contra a base da worktree, como a revisão do roteiro (ResumirAlteracoesAsync): é o hash aprovado no gate.
        var retrato = await RetratarAsync(worktree, worktree.ShaBase, cancellationToken) ??
            throw new WorktreeInvalidaException("Não foi possível ler o conteúdo da worktree.");
        return new ArtefatoDaWorktreeDto(head, retrato.Hash, retrato.Vazio);
    }

    // Worktree validada, sem filtro de conteúdo configurado, e o HEAD dela.
    private async Task<string> HeadDaEntregaAsync(WorktreePlanejada worktree, CancellationToken cancellationToken)
    {
        await ValidarAsync(worktree, cancellationToken);
        await ConferirSemFiltrosDeConteudoAsync(worktree, cancellationToken);
        return await HeadAsync(worktree.Caminho, cancellationToken) ??
            throw new WorktreeInvalidaException("Não foi possível ler o HEAD da worktree.");
    }

    // Recusa explicativa, não a barreira. Filtros de conteúdo (filter.<driver>.clean, smudge ou process, inclusive o do Git
    // LFS) são comandos externos, e o Git os roda só quando converte um arquivo da worktree em objeto ou o inverso. A
    // entrega nunca faz essa conversão (RetratarAsync), então nenhum filtro roda, mesmo que a configuração mude no meio da
    // operação. Esta consulta só antecipa a recusa com o motivo certo: num repositório com filtro, o hash aprovado no gate
    // foi lido com o filtro aplicado e não coincidiria com os bytes. Só os nomes das chaves aparecem, nunca os comandos.
    private async Task ConferirSemFiltrosDeConteudoAsync(WorktreePlanejada worktree, CancellationToken cancellationToken)
    {
        var filtros = await GitDaEntregaAsync(worktree.Caminho, null, null, cancellationToken, "config", "--null",
            "--get-regexp", ExpressaoDosFiltros);
        // 1: nenhuma chave; 0: há filtros; qualquer outro código: configuração ilegível (fail-closed).
        if (filtros.Codigo == 1) return;
        if (filtros.Codigo != 0)
            throw new WorktreeInvalidaException("Não foi possível ler a configuração de filtros do Git; nada foi feito.");
        var chaves = filtros.Saida.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Split('\n', 2)[0]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(5);
        throw new WorktreeInvalidaException("O Git deste repositório tem filtros de conteúdo configurados (" +
            string.Join(", ", chaves) + "), que executam comandos externos; a entrega não os executa e nada foi feito.");
    }

    public async Task<DesfechoDoCommitDto> CommitarAsync(WorktreePlanejada worktree, CommitDoArtefatoDto commit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(worktree);
        ArgumentNullException.ThrowIfNull(commit);
        if (commit.Autor.Problema() is { } autorInvalido)
            return new DesfechoDoCommitDto.SemEfeito($"Identidade Git inválida ({autorInvalido}); nada foi feito.");
        if (!Sha1().IsMatch(commit.Base) || !Sha256().IsMatch(commit.HashDoArtefato) || string.IsNullOrWhiteSpace(commit.Mensagem))
            return new DesfechoDoCommitDto.SemEfeito("Pedido de commit fora da forma; nada foi feito.");

        // 1. Sem efeito visível: worktree válida e na base aprovada; a árvore montada dos bytes atuais (index privado) e o
        // hash conferido a partir dela. A árvore é um objeto imutável e é exatamente a que o commit-tree recebe: o que foi
        // conferido é o que vai ao commit, e o index da worktree não é tocado até a branch andar.
        Retrato? retrato;
        try
        {
            if (await HeadDaEntregaAsync(worktree, cancellationToken) != commit.Base)
                return new DesfechoDoCommitDto.SemEfeito("A branch da worktree não está mais na base aprovada; nada foi feito.");
            retrato = await RetratarAsync(worktree, commit.Base, cancellationToken);
        }
        catch (Exception exception) when (exception is WorktreeInvalidaException or IOException or UnauthorizedAccessException
                                              or OperationCanceledException)
        {
            return new DesfechoDoCommitDto.SemEfeito($"A worktree não pôde ser conferida ({Curto(exception)}); nada foi feito.");
        }
        if (retrato is null) return new DesfechoDoCommitDto.SemEfeito("O conteúdo da worktree não pôde ser lido; nada foi feito.");
        if (retrato.Hash != commit.HashDoArtefato)
            return new DesfechoDoCommitDto.SemEfeito("O conteúdo da worktree mudou depois da aprovação; nada foi feito.");
        if (retrato.Vazio) return new DesfechoDoCommitDto.SemEfeito("A worktree não tem alterações; nada foi feito.");

        // 2. O objeto commit (commit-tree: só cria o objeto, sem mexer em ref, index ou arquivos), filho da base, com a
        // árvore conferida, o autor configurado e a mensagem exata. Falhar aqui não tem efeito visível.
        string novo;
        try
        {
            var objeto = await GitDaEntregaAsync(worktree.Caminho, Ambiente(commit.Autor), null, cancellationToken,
                "commit-tree", "--no-gpg-sign", "-p", commit.Base, "-m", commit.Mensagem, retrato.Arvore);
            if (objeto.Codigo != 0 || !Sha1().IsMatch(objeto.Saida))
                return SemCommit("O Git recusou criar o commit" + Detalhe(objeto.Erro));
            novo = objeto.Saida;
        }
        catch (Exception exception) when (exception is WorktreeInvalidaException or IOException or OperationCanceledException)
        {
            return SemCommit("A criação do commit foi interrompida");
        }

        // 3. O único efeito: a branch da worktree passa da base para o commit novo, por compare-and-swap do Git
        // (update-ref com o valor antigo). Se a branch já não estiver na base (outra entrega), nada muda. Depois de
        // pedido, só o HEAD diz o que aconteceu.
        (int Codigo, string Saida, string Erro) movida;
        try
        {
            movida = await GitDaEntregaAsync(worktree.Caminho, null, null, cancellationToken, "update-ref", "-m",
                MensagemDoReflog, $"refs/heads/{worktree.Branch}", novo, commit.Base);
        }
        catch (Exception exception) when (exception is WorktreeInvalidaException or IOException or OperationCanceledException)
        {
            return await DepoisDeMoverABranchAsync(worktree, commit.Base, novo, retrato.Arvore,
                "A gravação do commit foi interrompida");
        }
        return await DepoisDeMoverABranchAsync(worktree, commit.Base, novo, retrato.Arvore,
            movida.Codigo == 0 ? null : "O Git recusou mover a branch" + Detalhe(movida.Erro));
    }

    private static DesfechoDoCommitDto.SemEfeito SemCommit(string motivo) =>
        new($"{motivo}; a branch e o index da worktree não foram tocados e nenhum commit foi feito.");

    // Confere o que a branch é agora. No commit novo, filho da base, com a árvore conferida e a worktree nele: Commitado
    // (mesmo que o Git tenha relatado erro depois de gravar), e o index da worktree passa a ser a árvore gravada. Ainda na
    // base: nada foi gravado. Em outro commit: outra entrega moveu a branch e esta não fez nada. Qualquer outra coisa, ou
    // leitura impossível: Incerto, com o estado observado.
    private async Task<DesfechoDoCommitDto> DepoisDeMoverABranchAsync(WorktreePlanejada worktree, string baseDoCommit,
        string novo, string arvore, string? falha)
    {
        var referencia = await TentarAsync(() => ConsultarAsync(worktree.Caminho, CancellationToken.None, "rev-parse",
            "--verify", "--quiet", $"refs/heads/{worktree.Branch}^{{commit}}"));
        if (referencia == novo)
        {
            var head = await TentarAsync(() => HeadAsync(worktree.Caminho, CancellationToken.None));
            var pai = await TentarAsync(() => ConsultarAsync(worktree.Caminho, CancellationToken.None, "rev-parse", "--verify",
                "--quiet", "HEAD^1"));
            var arvoreGravada = await TentarAsync(() => ConsultarAsync(worktree.Caminho, CancellationToken.None, "rev-parse",
                "--verify", "--quiet", "HEAD^{tree}"));
            var branch = await TentarAsync(() => ConsultarAsync(worktree.Caminho, CancellationToken.None, "symbolic-ref",
                "--quiet", "HEAD"));
            if (head == novo && pai == baseDoCommit && arvoreGravada == arvore && branch == $"refs/heads/{worktree.Branch}")
            {
                // O index da worktree acompanha o commit (read-tree sem -u: só o index, nenhum arquivo é lido nem escrito).
                // Se falhar, o commit continua feito; a worktree só aparece com alterações preparadas e não é removida.
                await TentarAsync(async () => (await GitDaEntregaAsync(worktree.Caminho, null, null, CancellationToken.None,
                    "read-tree", arvore)).Codigo == 0 ? "" : null);
                return new DesfechoDoCommitDto.Commitado(novo);
            }
        }
        else if (referencia == baseDoCommit && falha is not null)
            return SemCommit(falha);
        else if (referencia is not null && falha is not null)
            return new DesfechoDoCommitDto.SemEfeito(falha + "; a branch já tinha andado (outra entrega), e esta não gravou " +
                "nada.");
        return new DesfechoDoCommitDto.Incerto((falha ?? "O commit foi gravado, mas não confere com o esperado") +
            "; o resultado não é conhecido e nada será repetido.", await DescreverEstadoAsync(worktree, CancellationToken.None));
    }

    public async Task<string> DescreverEstadoAsync(WorktreePlanejada worktree, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(worktree);
        try
        {
            ConferirForma(worktree);
            if (!Directory.Exists(worktree.Caminho)) return "A worktree não existe mais.";
            var head = await HeadAsync(worktree.Caminho, cancellationToken);
            var adiante = await ConsultarAsync(worktree.Caminho, cancellationToken, "rev-list", "--count",
                $"{worktree.ShaBase}..HEAD");
            var branch = await ConsultarAsync(worktree.Caminho, cancellationToken, "symbolic-ref", "--quiet", "HEAD");
            // Só o index contra o HEAD (objetos): ler os arquivos da worktree acionaria um filtro de conteúdo.
            int? preparados = null;
            if (head is not null)
            {
                var index = await GitDaEntregaAsync(worktree.Caminho, null, null, cancellationToken, "diff-index", "--cached",
                    "--name-only", "-z", "HEAD");
                if (index.Codigo == 0) preparados = index.Saida.Split('\0', StringSplitOptions.RemoveEmptyEntries).Length;
            }
            return $"HEAD {(head is null ? "ilegível" : head[..12])} na " +
                $"{(branch is null ? "branch ilegível" : branch.Replace("refs/heads/", "", StringComparison.Ordinal))}; " +
                $"{adiante ?? "?"} commit(s) além da base {worktree.ShaBase[..12]}; " +
                (preparados is null ? "index ilegível" : $"{preparados} arquivo(s) no index diferentes do HEAD") +
                " (os arquivos da worktree não são lidos).";
        }
        catch (Exception exception) when (exception is WorktreeInvalidaException or IOException or InvalidOperationException
                                              or UnauthorizedAccessException or OperationCanceledException)
        {
            return "Estado da worktree ilegível.";
        }
    }

    // ---- retrato da worktree sem filtros (revisão 2 do PR #326) ----

    private sealed record Retrato(string Arvore, string Hash, bool Vazio);

    private sealed record ArquivoDaWorktree(string Modo, byte[] Bytes);

    // A worktree como árvore Git, sem que nenhum processo Git leia os arquivos dela. O Git só roda um filtro de conteúdo
    // ao converter arquivo da worktree em objeto (add, diff e status contra a worktree, hash-object de um caminho) ou o
    // inverso (checkout). Aqui o D.A.N.T.E. lê os bytes, sem seguir link no meio do caminho. O Git os recebe pela entrada
    // padrão (hash-object --stdin --no-filters), e a árvore é montada num index privado a partir da base (read-tree,
    // update-index --index-info, write-tree). Nada é convertido, então nenhuma configuração, nem a mudada durante a
    // operação, tem filtro a rodar. Mesmo alcance do add --all: os rastreados da base (removidos, alterados, modo
    // executável, link) e os não rastreados não ignorados. Submódulo fica como na base. O index e os arquivos da worktree
    // não mudam; ficam só blobs e uma árvore soltos no banco de objetos. Nulo se algo não puder ser lido ou montado.
    private async Task<Retrato?> RetratarAsync(WorktreePlanejada worktree, string baseDoCommit,
        CancellationToken cancellationToken)
    {
        var privado = Directory.CreateTempSubdirectory("dante-entrega-");
        try
        {
            var indice = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = Path.Combine(privado.FullName, "index") };
            if ((await GitDaEntregaAsync(worktree.Caminho, indice, null, cancellationToken, "read-tree", baseDoCommit))
                .Codigo != 0)
                return null;
            var rastreados = await GitDaEntregaAsync(worktree.Caminho, null, null, cancellationToken, "ls-tree", "-r", "-z",
                "--full-tree", baseDoCommit);
            // Só nomes: os diretórios e as regras de ignore, contra o index privado (a base).
            var novos = await GitDaEntregaAsync(worktree.Caminho, indice, null, cancellationToken, "ls-files", "--others",
                "--exclude-standard", "-z");
            if (rastreados.Codigo != 0 || novos.Codigo != 0) return null;
            var raiz = Path.GetFullPath(worktree.Caminho);
            var entradas = new StringBuilder();
            foreach (var item in rastreados.Saida.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                // "<modo> <tipo> <oid>\t<caminho>"
                var tab = item.IndexOf('\t');
                var campos = tab < 0 ? [] : item[..tab].Split(' ');
                if (campos.Length != 3 || !Sha1().IsMatch(campos[2])) return null;
                var caminho = item[(tab + 1)..];
                if (campos[0] == "160000") continue;
                if (campos[0] is not ("100644" or "100755" or "120000")) return null;
                var atual = LerDaWorktree(raiz, caminho);
                if (atual is null)
                {
                    entradas.Append($"0 {new string('0', 40)}\t{caminho}\0");
                    continue;
                }
                if (atual.Modo == campos[0] && OidDoBlob(atual.Bytes) == campos[2]) continue;
                if (await GravarBlobAsync(worktree.Caminho, atual.Bytes, cancellationToken) is not { } blob) return null;
                entradas.Append($"{atual.Modo} {blob}\t{caminho}\0");
            }
            foreach (var caminho in novos.Saida.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                // Um diretório listado (repositório aninhado) ou um arquivo que sumiu no meio: não há o que gravar como está.
                if (caminho.EndsWith('/') || LerDaWorktree(raiz, caminho) is not { } atual) return null;
                if (await GravarBlobAsync(worktree.Caminho, atual.Bytes, cancellationToken) is not { } blob) return null;
                entradas.Append($"{atual.Modo} {blob}\t{caminho}\0");
            }
            if (entradas.Length > 0 && (await GitDaEntregaAsync(worktree.Caminho, indice,
                    Encoding.UTF8.GetBytes(entradas.ToString()), cancellationToken, "update-index", "-z", "--index-info"))
                .Codigo != 0)
                return null;
            var escrita = await GitDaEntregaAsync(worktree.Caminho, indice, null, cancellationToken, "write-tree");
            if (escrita.Codigo != 0 || !Sha1().IsMatch(escrita.Saida)) return null;
            var daBase = await GitDaEntregaAsync(worktree.Caminho, null, null, cancellationToken, "rev-parse", "--verify",
                "--quiet", $"{baseDoCommit}^{{tree}}");
            if (daBase.Codigo != 0) return null;
            return await HashDaArvoreAsync(worktree, baseDoCommit, escrita.Saida, cancellationToken) is { } hash
                ? new Retrato(escrita.Saida, hash, escrita.Saida == daBase.Saida) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            try { privado.Delete(recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    // Um caminho relativo da worktree como o add o gravaria. Um link vira modo 120000, com o destino como conteúdo. Um
    // arquivo comum vira 100755 se for executável pelo dono, senão 100644. Nulo se o caminho estiver ausente, for um
    // diretório ou ficar atrás de um link num componente intermediário (o Git também não segue esse link). Nunca sai da
    // worktree.
    private static ArquivoDaWorktree? LerDaWorktree(string raiz, string relativo)
    {
        var completo = Path.GetFullPath(Path.Combine(raiz, relativo));
        if (!completo.StartsWith(raiz + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new WorktreeInvalidaException("Arquivo fora da worktree.");
        for (var pai = Path.GetDirectoryName(completo); pai is not null && pai.Length > raiz.Length;
             pai = Path.GetDirectoryName(pai))
            if (!Directory.Exists(pai) || new DirectoryInfo(pai).LinkTarget is not null) return null;
        var informacao = new FileInfo(completo);
        if (informacao.LinkTarget is { } destino) return new("120000", Encoding.UTF8.GetBytes(destino));
        if (!informacao.Exists) return null;
        var executavel = !OperatingSystem.IsWindows() && (informacao.UnixFileMode & UnixFileMode.UserExecute) != 0;
        return new(executavel ? "100755" : "100644", File.ReadAllBytes(completo));
    }

    // O id que o Git daria ao blob (SHA-1 de "blob <tamanho>\0" + bytes): só para não regravar o que não mudou.
    private static string OidDoBlob(byte[] bytes)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA1);
        hash.AppendData(Encoding.ASCII.GetBytes($"blob {bytes.Length}\0"));
        hash.AppendData(bytes);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    // Os bytes lidos pelo D.A.N.T.E. entram no banco de objetos pela entrada padrão, sem filtro. O id devolvido precisa ser
    // o dos próprios bytes.
    private async Task<string?> GravarBlobAsync(string cwd, byte[] bytes, CancellationToken cancellationToken)
    {
        var gravado = await GitDaEntregaAsync(cwd, null, bytes, cancellationToken, "hash-object", "-w", "--no-filters",
            "--stdin");
        return gravado.Codigo == 0 && gravado.Saida == OidDoBlob(bytes) ? gravado.Saida : null;
    }

    // Hash canônico a partir da árvore que o commit vai gravar, na mesma forma de ResumirAlteracoesAsync: o diff
    // rastreado da árvore contra a base, sem os arquivos novos, e cada arquivo novo pelo conteúdo do blob. Só objetos
    // (nenhum filtro roda). Nulo quando algo não pode ser lido ou não confere (um submódulo novo, por exemplo).
    private async Task<string?> HashDaArvoreAsync(WorktreePlanejada worktree, string baseDoCommit, string arvore,
        CancellationToken cancellationToken)
    {
        var diff = await GitDaEntregaAsync(worktree.Caminho, null, null, cancellationToken, "diff", "--no-ext-diff",
            "--no-textconv", "--no-color", "--binary", "--no-renames", "--diff-filter=a", baseDoCommit, arvore);
        if (diff.Codigo != 0) return null;
        var bruto = await GitDaEntregaAsync(worktree.Caminho, null, null, cancellationToken, "diff", "--raw", "-z",
            "--no-abbrev", "--no-renames", "--diff-filter=A", baseDoCommit, arvore);
        if (bruto.Codigo != 0) return null;
        var partes = bruto.Saida.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (partes.Length % 2 != 0) return null;
        var novos = new List<(string Caminho, string Modo, string Blob)>();
        for (var i = 0; i < partes.Length; i += 2)
        {
            // ":000000 <modo> <oid antigo> <oid novo> A"
            var campos = partes[i].TrimStart(':').Split(' ');
            if (campos.Length != 5 || campos[4] != "A" || !Sha1().IsMatch(campos[3])) return null;
            novos.Add((partes[i + 1], campos[1], campos[3]));
        }
        novos.Sort((a, b) => string.CompareOrdinal(a.Caminho, b.Caminho));
        var conteudos = new List<(string Caminho, string Conteudo)>(novos.Count);
        foreach (var (caminho, modo, blob) in novos)
        {
            if (modo is not ("100644" or "100755" or "120000")) return null;
            var link = modo == "120000";
            using var bytes = new MemoryStream();
            using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            if (!await LerBlobAsync(worktree.Caminho, blob, link ? null : hash, link ? bytes : null, cancellationToken))
                return null;
            conteudos.Add((caminho, link ? "link:" + Encoding.UTF8.GetString(bytes.ToArray())
                : Convert.ToHexStringLower(hash.GetHashAndReset())));
        }
        return HashCanonico(diff.Saida, conteudos);
    }

    private async Task<string?> HeadAsync(string cwd, CancellationToken cancellationToken)
    {
        var head = await ConsultarAsync(cwd, cancellationToken, "rev-parse", "--verify", "--quiet", "HEAD^{commit}");
        return head is not null && Sha1().IsMatch(head) ? head : null;
    }

    private static async Task<string?> TentarAsync(Func<Task<string?>> consulta)
    {
        try
        {
            return await consulta();
        }
        catch (Exception exception) when (exception is WorktreeInvalidaException or IOException or OperationCanceledException)
        {
            return null;
        }
    }

    private static string Curto(Exception exception) => exception switch
    {
        WorktreeInvalidaException => exception.Message,
        OperationCanceledException => "interrompida",
        _ => "falha de leitura"
    };

    // O autor e o committer são o autor configurado; nada vem do Git do host nem do repositório.
    private static Dictionary<string, string> Ambiente(AutorDoCommit autor) => new()
    {
        ["GIT_AUTHOR_NAME"] = autor.Nome,
        ["GIT_AUTHOR_EMAIL"] = autor.Email,
        ["GIT_COMMITTER_NAME"] = autor.Nome,
        ["GIT_COMMITTER_EMAIL"] = autor.Email
    };

    public IReadOnlyList<string> ListarDiretoriosDesconhecidos(IReadOnlyCollection<string> conhecidos)
    {
        ArgumentNullException.ThrowIfNull(conhecidos);
        var raiz = Raiz();
        if (!Directory.Exists(raiz)) return [];
        var sabidos = conhecidos.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Directory.EnumerateDirectories(raiz).SelectMany(alias => Directory.EnumerateFileSystemEntries(alias))
            .Select(Path.GetFullPath).Where(caminho => !sabidos.Contains(caminho)).Order(StringComparer.Ordinal).ToArray();
    }

    // Raiz autorizada: absoluta, real (sem link em nenhum componente) e fora de qualquer checkout cadastrado.
    private string Raiz()
    {
        var raiz = Path.TrimEndingDirectorySeparator(Path.GetFullPath(opcoes.Raiz));
        if (Directory.Exists(raiz) && Canonico(raiz, "raiz das worktrees") != raiz)
            throw new WorktreeInvalidaException("A raiz das worktrees passa por um link; nada foi criado.");
        foreach (var repositorio in catalogo.Listar())
            if (CaminhoReal.Sobrepoe(raiz, repositorio.Caminho))
                throw new WorktreeInvalidaException("A raiz das worktrees se sobrepõe a um repositório cadastrado.");
        return raiz;
    }

    private string DiretorioDoAlias(string alias)
    {
        if (!AliasValido().IsMatch(alias)) throw new WorktreeInvalidaException("Alias de repositório inválido.");
        var raiz = Raiz();
        Directory.CreateDirectory(raiz);
        if (Canonico(raiz, "raiz das worktrees") != raiz)
            throw new WorktreeInvalidaException("A raiz das worktrees passa por um link; nada foi criado.");
        return Path.Combine(raiz, alias[1..].ToLowerInvariant());
    }

    // O que vem do ledger é conferido de novo: a reserva só vale para caminho sob a raiz, no layout esperado.
    private void ConferirForma(WorktreePlanejada worktree)
    {
        var esperado = Path.GetFullPath(worktree.Caminho);
        if (esperado != worktree.Caminho || Path.GetDirectoryName(esperado) != DiretorioDoAlias(worktree.Alias) ||
            !NomeDaWorktree().IsMatch(Path.GetFileName(esperado)) || !BranchValida().IsMatch(worktree.Branch) ||
            worktree.ShaBase is not { Length: 40 } || !worktree.ShaBase.All(Uri.IsHexDigit))
            throw new WorktreeInvalidaException("Reserva de worktree fora do layout autorizado.");
        if (string.Equals(worktree.Caminho, worktree.CaminhoDoRepositorio, StringComparison.OrdinalIgnoreCase))
            throw new WorktreeInvalidaException("A worktree não pode ser o checkout principal.");
    }

    // Nenhum checkout cadastrado (principal de qualquer repositório) dentro da worktree nem contendo a worktree.
    private void ConferirForaDosCheckouts(string caminho)
    {
        foreach (var repositorio in catalogo.Listar())
            if (CaminhoReal.Sobrepoe(caminho, repositorio.Caminho))
                throw new WorktreeInvalidaException("A worktree se sobrepõe a um checkout cadastrado.");
    }

    private static string Canonico(string caminho, string oQue)
    {
        try
        {
            return CaminhoReal.Resolver(caminho);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new WorktreeInvalidaException($"Não foi possível resolver o caminho real da {oQue}.");
        }
    }

    private static bool EhLink(string caminho) => new FileInfo(caminho).LinkTarget is not null;

    private static bool Listada(string porcelana, WorktreePlanejada worktree)
    {
        foreach (var bloco in porcelana.Replace("\r", "").Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var linhas = bloco.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (linhas.Contains($"worktree {worktree.Caminho}") && linhas.Contains($"branch refs/heads/{worktree.Branch}"))
                return true;
        }
        return false;
    }

    // Só a primeira linha do stderr do Git, curta: caminhos e mensagens do Git não carregam segredo, mas o motivo vai ao
    // ledger e ao usuário, então fica pequeno.
    private static string Detalhe(string erro)
    {
        var linha = erro.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        return string.IsNullOrEmpty(linha) ? "" : $" ({(linha.Length <= 200 ? linha : linha[..200])})";
    }

    private async Task<string?> ConsultarAsync(string cwd, CancellationToken cancellationToken, params string[] argumentos)
    {
        var resultado = await GitAsync(cwd, cancellationToken, argumentos);
        return resultado.Codigo == 0 ? resultado.Saida : null;
    }

    // Conjunto fechado: qualquer outro comando é erro de programação, recusado antes de existir processo.
    internal static bool Permitido(IReadOnlyList<string> argumentos) => argumentos switch
    {
        ["worktree", "add", ..] or ["worktree", "remove", _] or ["worktree", "list", "--porcelain"] => !argumentos.Contains("--force") && !argumentos.Contains("-f"),
        ["rev-parse", ..] or ["check-ref-format", ..] or ["symbolic-ref", "--quiet", "HEAD"] => true,
        ["status", "--porcelain", ..] or ["rev-list", "--count", _] => true,
        // Leitura das alterações para a revisão de um roteiro (#308).
        ["diff", "--no-ext-diff", "--no-textconv", "--no-color", "--binary", _] => true,
        ["ls-files", "--others", "--exclude-standard", "-z"] => true,
        // Entrega do artefato aprovado (#311, PR 3): só objetos e um index privado (ver SemLeituraDaWorktree). A árvore
        // contra a base, o conteúdo de um blob, os blobs pela entrada padrão sem filtro, a árvore montada e exatamente um
        // commit de forma fixa; read-tree de um objeto, sem -u (nenhum arquivo é lido nem escrito).
        ["diff", "--no-ext-diff", "--no-textconv", "--no-color", "--binary", "--no-renames", "--diff-filter=a", var b, var t]
            => Sha1().IsMatch(b) && Sha1().IsMatch(t),
        ["diff", "--raw", "-z", "--no-abbrev", "--no-renames", "--diff-filter=A", var b, var t] =>
            Sha1().IsMatch(b) && Sha1().IsMatch(t),
        ["diff-index", "--cached", "--name-only", "-z", "HEAD"] => true,
        // Só leitura: quais drivers de filtro de conteúdo estão configurados (a entrega recusa com o motivo).
        ["config", "--null", "--get-regexp", ExpressaoDosFiltros] => true,
        ["cat-file", "blob", var oid] => Sha1().IsMatch(oid),
        ["ls-tree", "-r", "-z", "--full-tree", var b] => Sha1().IsMatch(b),
        ["hash-object", "-w", "--no-filters", "--stdin"] or ["update-index", "-z", "--index-info"] or ["write-tree"] => true,
        ["read-tree", var b] => Sha1().IsMatch(b),
        ["commit-tree", "--no-gpg-sign", "-p", var b, "-m", _, var arvore] => Sha1().IsMatch(b) && Sha1().IsMatch(arvore),
        // O único comando que move uma ref: só a branch de uma execução, com o valor antigo (compare-and-swap).
        ["update-ref", "-m", MensagemDoReflog, var referencia, var novo, var antigo] =>
            referencia.StartsWith("refs/heads/", StringComparison.Ordinal) &&
            BranchValida().IsMatch(referencia["refs/heads/".Length..]) && Sha1().IsMatch(novo) && Sha1().IsMatch(antigo),
        _ => false
    };

    // Os comandos da entrega (revisão 2 do PR #326): nenhum lê arquivo da worktree, então nenhum aciona filtro de
    // conteúdo (clean, smudge, process), qualquer que seja a configuração do Git no momento em que roda. São consultas de
    // refs e objetos, a lista de nomes não rastreados (diretórios e regras de ignore, sem conteúdo), a montagem da árvore
    // num index privado a partir de bytes recebidos pela entrada padrão e a gravação. Ficam de fora o add, o status e o
    // diff contra a worktree, que convertem arquivo em objeto.
    internal static bool SemLeituraDaWorktree(IReadOnlyList<string> argumentos) => Permitido(argumentos) && argumentos switch
    {
        ["rev-parse", ..] or ["symbolic-ref", "--quiet", "HEAD"] or ["rev-list", "--count", _] => true,
        ["config", "--null", "--get-regexp", ExpressaoDosFiltros] => true,
        ["ls-tree", ..] or ["ls-files", "--others", "--exclude-standard", "-z"] => true,
        ["hash-object", "-w", "--no-filters", "--stdin"] or ["update-index", "-z", "--index-info"] => true,
        ["read-tree", _] or ["write-tree"] or ["cat-file", "blob", _] or ["commit-tree", ..] or ["update-ref", ..] => true,
        ["diff", .., var b, var t] => Sha1().IsMatch(b) && Sha1().IsMatch(t),
        ["diff-index", "--cached", ..] => true,
        _ => false
    };

    internal const string MensagemDoReflog = "dante: entrega do artefato aprovado";
    internal const string ExpressaoDosFiltros = "^filter\\..*\\.(clean|smudge|process)$";

    private Task<(int Codigo, string Saida, string Erro)> GitAsync(string cwd, CancellationToken cancellationToken,
        params string[] argumentos) => GitAsync(cwd, null, null, cancellationToken, argumentos);

    // Todo processo Git da entrega passa por aqui: só o que não lê arquivo da worktree, e nunca busca preguiçosa de
    // objetos num clone parcial (GIT_NO_LAZY_FETCH), que executaria o transporte do remoto.
    private Task<(int Codigo, string Saida, string Erro)> GitDaEntregaAsync(string cwd,
        IReadOnlyDictionary<string, string>? ambiente, byte[]? entrada, CancellationToken cancellationToken,
        params string[] argumentos)
    {
        if (!SemLeituraDaWorktree(argumentos))
            throw new InvalidOperationException("Comando Git fora do conjunto da entrega.");
        return GitAsync(cwd, DaEntrega(ambiente), entrada, cancellationToken, argumentos);
    }

    private static Dictionary<string, string> DaEntrega(IReadOnlyDictionary<string, string>? ambiente) =>
        new(ambiente ?? new Dictionary<string, string>()) { ["GIT_NO_LAZY_FETCH"] = "1" };

    private async Task<(int Codigo, string Saida, string Erro)> GitAsync(string cwd,
        IReadOnlyDictionary<string, string>? ambiente, byte[]? entrada, CancellationToken cancellationToken,
        params string[] argumentos)
    {
        using var prazo = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        prazo.CancelAfter(limite);
        Process? processo = null;
        try
        {
            processo = Iniciar(cwd, ambiente, argumentos, entrada is not null);
            if (entrada is not null)
            {
                await processo.StandardInput.BaseStream.WriteAsync(entrada, prazo.Token);
                processo.StandardInput.Close();
            }
            var saida = processo.StandardOutput.ReadToEndAsync(prazo.Token);
            var erro = processo.StandardError.ReadToEndAsync(prazo.Token);
            await processo.WaitForExitAsync(prazo.Token);
            return (processo.ExitCode, (await saida).Trim(), (await erro).Trim());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new WorktreeInvalidaException("O Git não respondeu no prazo.");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or DirectoryNotFoundException)
        {
            throw new WorktreeInvalidaException("Não foi possível executar o Git.");
        }
        finally
        {
            Encerrar(processo);
        }
    }

    // cat-file blob em bytes (o conteúdo pode ser binário): para o hash, ou para os bytes de um link. Falso se o Git falhar.
    private async Task<bool> LerBlobAsync(string cwd, string blob, System.Security.Cryptography.IncrementalHash? hash,
        Stream? bytes, CancellationToken cancellationToken)
    {
        using var prazo = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        prazo.CancelAfter(limite);
        Process? processo = null;
        try
        {
            if (!SemLeituraDaWorktree(["cat-file", "blob", blob]))
                throw new InvalidOperationException("Comando Git fora do conjunto da entrega.");
            processo = Iniciar(cwd, DaEntrega(null), ["cat-file", "blob", blob], false);
            var erro = processo.StandardError.ReadToEndAsync(prazo.Token);
            var buffer = new byte[81920];
            int lidos;
            while ((lidos = await processo.StandardOutput.BaseStream.ReadAsync(buffer, prazo.Token)) > 0)
            {
                hash?.AppendData(buffer, 0, lidos);
                if (bytes is not null) await bytes.WriteAsync(buffer.AsMemory(0, lidos), prazo.Token);
            }
            await processo.WaitForExitAsync(prazo.Token);
            await erro;
            return processo.ExitCode == 0;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new WorktreeInvalidaException("O Git não respondeu no prazo.");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or DirectoryNotFoundException)
        {
            throw new WorktreeInvalidaException("Não foi possível executar o Git.");
        }
        finally
        {
            Encerrar(processo);
        }
    }

    private Process Iniciar(string cwd, IReadOnlyDictionary<string, string>? ambiente, string[] argumentos, bool comEntrada)
    {
        if (!Permitido(argumentos)) throw new InvalidOperationException("Comando Git fora do conjunto autorizado.");
        var inicio = new ProcessStartInfo(executavel)
        {
            WorkingDirectory = cwd,
            RedirectStandardInput = comEntrada,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        // gc e maintenance automáticos desligados: o commit da entrega não dispara outro processo no diretório Git comum.
        foreach (var argumento in (string[])["-c", "core.fsmonitor=false", "-c", "core.hooksPath=/dev/null", "-c",
                     "core.quotepath=off", "-c", "gc.auto=0", "-c", "maintenance.auto=false", .. argumentos])
            inicio.ArgumentList.Add(argumento);
        // Nenhuma variável GIT_* herdada (GIT_DIR, GIT_INDEX_FILE, GIT_CONFIG_PARAMETERS…) redireciona o comando.
        foreach (var herdada in inicio.Environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.Ordinal)).ToArray())
            inicio.Environment.Remove(herdada);
        inicio.Environment["GIT_TERMINAL_PROMPT"] = "0";
        inicio.Environment["LC_ALL"] = "C";
        if (ambiente is not null)
            foreach (var (nome, valor) in ambiente) inicio.Environment[nome] = valor;
        return Process.Start(inicio) ?? throw new WorktreeInvalidaException("Não foi possível executar o Git.");
    }

    private static void Encerrar(Process? processo)
    {
        try { if (processo is { HasExited: false }) processo.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        processo?.Dispose();
    }

    [GeneratedRegex("^[0-9a-f]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha1();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256();

    [GeneratedRegex("^@[a-z][a-z0-9_]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AliasValido();

    [GeneratedRegex("^[a-z][a-z0-9-]{1,30}$", RegexOptions.CultureInvariant)]
    private static partial Regex HandleValido();

    [GeneratedRegex("^E[1-9][0-9]{0,18}$", RegexOptions.CultureInvariant)]
    private static partial Regex NomeDaWorktree();

    [GeneratedRegex("^dante/[a-z][a-z0-9-]{1,30}/e[1-9][0-9]{0,18}$", RegexOptions.CultureInvariant)]
    private static partial Regex BranchValida();
}
