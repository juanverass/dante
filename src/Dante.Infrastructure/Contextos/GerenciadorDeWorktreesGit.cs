using System.Diagnostics;
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
// (add --all, write-tree, commit-tree e update-ref da branch da execução com o valor antigo, read-tree para desfazer o
// index antes do commit), sem push, reset, checkout, merge nem criação de branch. Variáveis GIT_* herdadas não alcançam nenhum processo (não redirecionam diretório, index
// nem configuração).
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

    public async Task<string> LerHeadAsync(WorktreePlanejada worktree, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(worktree);
        await ValidarAsync(worktree, cancellationToken);
        // Antes de qualquer comando que leia o conteúdo da worktree (diff, add): é aqui que um filtro rodaria.
        await ConferirSemFiltrosDeConteudoAsync(worktree, cancellationToken);
        return await HeadAsync(worktree.Caminho, cancellationToken) ??
            throw new WorktreeInvalidaException("Não foi possível ler o HEAD da worktree.");
    }

    // Filtros de conteúdo (filter.<driver>.clean, smudge ou process, inclusive o do Git LFS) são comandos externos que o
    // Git executa ao ler ou preparar arquivos (diff, status, add). A entrega não executa comando do repositório nem do
    // host: com qualquer driver desses configurado em qualquer escopo (sistema, global, repositório, worktree), ela é
    // recusada antes de ler o conteúdo, sem efeito. Só os nomes das chaves aparecem no motivo, nunca os comandos.
    private async Task ConferirSemFiltrosDeConteudoAsync(WorktreePlanejada worktree, CancellationToken cancellationToken)
    {
        var filtros = await GitAsync(worktree.Caminho, cancellationToken, "config", "--null", "--get-regexp",
            ExpressaoDosFiltros);
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

        // 1. Reconferência sem efeito: worktree válida, na base aprovada e com o conteúdo aprovado.
        AlteracoesDaWorktreeDto antes;
        try
        {
            if (await LerHeadAsync(worktree, cancellationToken) != commit.Base)
                return new DesfechoDoCommitDto.SemEfeito("A branch da worktree não está mais na base aprovada; nada foi feito.");
            // Limite 1: o hash cobre o diff inteiro, e um caractere basta para saber se há alteração rastreada.
            antes = await ResumirAlteracoesAsync(worktree, 1, cancellationToken);
        }
        catch (Exception exception) when (exception is WorktreeInvalidaException or IOException or UnauthorizedAccessException
                                              or OperationCanceledException)
        {
            // Nada foi tocado ainda: nem uma interrupção aqui tem efeito.
            return new DesfechoDoCommitDto.SemEfeito($"A worktree não pôde ser conferida ({Curto(exception)}); nada foi feito.");
        }
        if (antes.Hash != commit.HashDoArtefato)
            return new DesfechoDoCommitDto.SemEfeito("O conteúdo da worktree mudou depois da aprovação; nada foi feito.");
        if (antes.Vazias) return new DesfechoDoCommitDto.SemEfeito("A worktree não tem alterações; nada foi feito.");

        // 2. Daqui em diante o index muda. Até o commit, qualquer divergência restaura o index na base (sem tocar nos
        // arquivos) e é SemEfeito; se a restauração falhar, Incerto.
        string arvore;
        try
        {
            // O Git grava o index de forma atômica (arquivo de trava e troca): um add que falha não o alterou.
            var preparo = await GitAsync(worktree.Caminho, cancellationToken, "add", "--all");
            if (preparo.Codigo != 0)
                return new DesfechoDoCommitDto.SemEfeito("O Git recusou preparar as alterações" + Detalhe(preparo.Erro) +
                    "; o index não foi alterado e nenhum commit foi feito.");
            var escrita = await GitAsync(worktree.Caminho, cancellationToken, "write-tree");
            if (escrita.Codigo != 0 || !Sha1().IsMatch(escrita.Saida))
                return await DesfazerIndexAsync(worktree, commit.Base, "O Git não gravou a árvore do commit");
            arvore = escrita.Saida;
            // O commit grava esta árvore, um objeto imutável: é ela que precisa ser exatamente o aprovado. Conferir a
            // árvore já escrita (e não o index, que outro processo poderia mudar entre a conferência e o write-tree)
            // fecha a janela: o que foi conferido é o que o commit-tree recebe.
            if (await HashDaArvoreAsync(worktree, commit.Base, arvore, antes.NaoRastreados, cancellationToken) !=
                commit.HashDoArtefato)
                return await DesfazerIndexAsync(worktree, commit.Base, "O conteúdo preparado difere do aprovado");
        }
        catch (Exception exception) when (exception is WorktreeInvalidaException or IOException or OperationCanceledException)
        {
            return await DesfazerIndexAsync(worktree, commit.Base, "A preparação do commit foi interrompida");
        }

        // 3. O objeto commit (commit-tree: só cria o objeto, sem mexer em ref, index ou arquivos), filho da base, com a
        // árvore conferida, o autor configurado e a mensagem exata. Falhar aqui não tem efeito visível.
        string novo;
        try
        {
            var objeto = await GitAsync(worktree.Caminho, Ambiente(commit.Autor), cancellationToken, "commit-tree",
                "--no-gpg-sign", "-p", commit.Base, "-m", commit.Mensagem, arvore);
            if (objeto.Codigo != 0 || !Sha1().IsMatch(objeto.Saida))
                return await DesfazerIndexAsync(worktree, commit.Base, "O Git recusou criar o commit" + Detalhe(objeto.Erro));
            novo = objeto.Saida;
        }
        catch (Exception exception) when (exception is WorktreeInvalidaException or IOException or OperationCanceledException)
        {
            return await DesfazerIndexAsync(worktree, commit.Base, "A criação do commit foi interrompida");
        }

        // 4. O único efeito: a branch da worktree passa da base para o commit novo, por compare-and-swap do Git
        // (update-ref com o valor antigo). Se a branch já não estiver na base (outra entrega), nada muda. Depois de
        // pedido, só o HEAD diz o que aconteceu.
        (int Codigo, string Saida, string Erro) movida;
        try
        {
            movida = await GitAsync(worktree.Caminho, cancellationToken, "update-ref", "-m", MensagemDoReflog,
                $"refs/heads/{worktree.Branch}", novo, commit.Base);
        }
        catch (Exception exception) when (exception is WorktreeInvalidaException or IOException or OperationCanceledException)
        {
            return await DepoisDeMoverABranchAsync(worktree, commit.Base, novo, arvore, "A gravação do commit foi interrompida");
        }
        return await DepoisDeMoverABranchAsync(worktree, commit.Base, novo, arvore,
            movida.Codigo == 0 ? null : "O Git recusou mover a branch" + Detalhe(movida.Erro));
    }

    // Confere o que a branch é agora. No commit novo, filho da base, com a árvore conferida e a worktree nele: Commitado
    // (mesmo que o Git tenha relatado erro depois de gravar). Ainda na base: nada foi gravado (index restaurado). Em
    // outro commit: outra entrega moveu a branch e esta não fez nada (o index não é tocado, porque já é o da outra).
    // Qualquer outra coisa, ou leitura impossível: Incerto, com o estado observado.
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
                return new DesfechoDoCommitDto.Commitado(novo);
        }
        else if (referencia == baseDoCommit && falha is not null)
            return await DesfazerIndexAsync(worktree, baseDoCommit, falha);
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
            // status lê os arquivos e acionaria um filtro de conteúdo: com filtro configurado, as alterações não são lidas.
            string[]? linhas = null;
            var semFiltros = (await GitAsync(worktree.Caminho, cancellationToken, "config", "--null", "--get-regexp",
                ExpressaoDosFiltros)).Codigo == 1;
            if (semFiltros)
            {
                var status = await GitAsync(worktree.Caminho, cancellationToken, "status", "--porcelain", "--untracked-files=all");
                linhas = status.Codigo == 0 ? status.Saida.Split('\n', StringSplitOptions.RemoveEmptyEntries) : null;
            }
            var preparados = linhas?.Count(l => l.Length > 1 && l[0] is not (' ' or '?'));
            var foraDoIndex = linhas?.Count(l => l.Length > 1 && (l[1] != ' ' || l[0] == '?'));
            return $"HEAD {(head is null ? "ilegível" : head[..12])} na " +
                $"{(branch is null ? "branch ilegível" : branch.Replace("refs/heads/", "", StringComparison.Ordinal))}; " +
                $"{adiante ?? "?"} commit(s) além da base {worktree.ShaBase[..12]}; " +
                (linhas is not null ? $"{preparados} arquivo(s) no index e {foraDoIndex} fora dele."
                    : semFiltros ? "alterações ilegíveis." : "alterações não lidas (filtros de conteúdo configurados).");
        }
        catch (Exception exception) when (exception is WorktreeInvalidaException or IOException or InvalidOperationException
                                              or UnauthorizedAccessException or OperationCanceledException)
        {
            return "Estado da worktree ilegível.";
        }
    }

    // Hash canônico a partir da árvore que o commit vai gravar, na mesma forma de ResumirAlteracoesAsync: o diff
    // rastreado da árvore contra a base, sem os arquivos novos, e cada arquivo novo pelo conteúdo do blob. Os arquivos
    // novos precisam ser exatamente os que eram não rastreados antes do add. Só objetos (nenhum filtro roda). Nulo
    // quando algo não pode ser lido ou não confere (um submódulo, por exemplo).
    private async Task<string?> HashDaArvoreAsync(WorktreePlanejada worktree, string baseDoCommit, string arvore,
        IReadOnlyList<string> naoRastreados, CancellationToken cancellationToken)
    {
        var diff = await GitAsync(worktree.Caminho, cancellationToken, "diff", "--no-ext-diff", "--no-textconv",
            "--no-color", "--binary", "--no-renames", "--diff-filter=a", baseDoCommit, arvore);
        if (diff.Codigo != 0) return null;
        var bruto = await GitAsync(worktree.Caminho, cancellationToken, "diff", "--raw", "-z", "--no-abbrev",
            "--no-renames", "--diff-filter=A", baseDoCommit, arvore);
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
        if (!novos.Select(n => n.Caminho).SequenceEqual(naoRastreados, StringComparer.Ordinal)) return null;
        var conteudos = new List<(string Caminho, string Conteudo)>(novos.Count);
        foreach (var (caminho, modo, blob) in novos)
        {
            if (modo is not ("100644" or "100755" or "120000")) return null;
            var link = modo == "120000";
            using var bytes = new MemoryStream();
            using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            if (!await LerBlobAsync(worktree.Caminho, blob, link ? null : hash, link ? bytes : null, cancellationToken))
                return null;
            conteudos.Add((caminho, link ? "link:" + System.Text.Encoding.UTF8.GetString(bytes.ToArray())
                : Convert.ToHexStringLower(hash.GetHashAndReset())));
        }
        return HashCanonico(diff.Saida, conteudos);
    }

    // Antes do commit: o index volta à árvore da base (read-tree, sem -u: os arquivos não mudam). Sem efeito se der
    // certo; Incerto se não der.
    private async Task<DesfechoDoCommitDto> DesfazerIndexAsync(WorktreePlanejada worktree, string baseDoCommit, string motivo)
    {
        // Só com a branch ainda na base: se outra entrega a moveu, o index já é o dela e não é tocado.
        var head = await TentarAsync(() => HeadAsync(worktree.Caminho, CancellationToken.None));
        if (head is not null && head != baseDoCommit)
            return new DesfechoDoCommitDto.SemEfeito($"{motivo}; a branch já tinha andado (outra entrega), e esta não " +
                "gravou nada.");
        var restaurado = head is null ? null : await TentarAsync(async () =>
            (await GitAsync(worktree.Caminho, CancellationToken.None, "read-tree", baseDoCommit)).Codigo == 0 ? "" : null);
        return restaurado is not null
            ? new DesfechoDoCommitDto.SemEfeito($"{motivo}; o index foi restaurado e nenhum commit foi feito.")
            : new DesfechoDoCommitDto.Incerto($"{motivo}, e o index não pôde ser restaurado.",
                await DescreverEstadoAsync(worktree, CancellationToken.None));
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
        // Entrega do artefato aprovado (#311, PR 3): o index contra a base, o conteúdo de um blob preparado, a árvore e
        // exatamente um commit de forma fixa; read-tree só da base (desfaz o index antes do commit, sem tocar arquivos).
        ["diff", "--no-ext-diff", "--no-textconv", "--no-color", "--binary", "--no-renames", "--diff-filter=a", var b, var t]
            => Sha1().IsMatch(b) && Sha1().IsMatch(t),
        ["diff", "--raw", "-z", "--no-abbrev", "--no-renames", "--diff-filter=A", var b, var t] =>
            Sha1().IsMatch(b) && Sha1().IsMatch(t),
        // Só leitura: quais drivers de filtro de conteúdo estão configurados (a entrega os recusa).
        ["config", "--null", "--get-regexp", ExpressaoDosFiltros] => true,
        ["cat-file", "blob", var oid] => Sha1().IsMatch(oid),
        ["add", "--all"] or ["write-tree"] => true,
        ["read-tree", var b] => Sha1().IsMatch(b),
        ["commit-tree", "--no-gpg-sign", "-p", var b, "-m", _, var arvore] => Sha1().IsMatch(b) && Sha1().IsMatch(arvore),
        // O único comando que move uma ref: só a branch de uma execução, com o valor antigo (compare-and-swap).
        ["update-ref", "-m", MensagemDoReflog, var referencia, var novo, var antigo] =>
            referencia.StartsWith("refs/heads/", StringComparison.Ordinal) &&
            BranchValida().IsMatch(referencia["refs/heads/".Length..]) && Sha1().IsMatch(novo) && Sha1().IsMatch(antigo),
        _ => false
    };

    internal const string MensagemDoReflog = "dante: entrega do artefato aprovado";
    internal const string ExpressaoDosFiltros = "^filter\\..*\\.(clean|smudge|process)$";

    private Task<(int Codigo, string Saida, string Erro)> GitAsync(string cwd, CancellationToken cancellationToken,
        params string[] argumentos) => GitAsync(cwd, null, cancellationToken, argumentos);

    private async Task<(int Codigo, string Saida, string Erro)> GitAsync(string cwd,
        IReadOnlyDictionary<string, string>? ambiente, CancellationToken cancellationToken, params string[] argumentos)
    {
        using var prazo = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        prazo.CancelAfter(limite);
        Process? processo = null;
        try
        {
            processo = Iniciar(cwd, ambiente, argumentos);
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
            processo = Iniciar(cwd, null, ["cat-file", "blob", blob]);
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

    private Process Iniciar(string cwd, IReadOnlyDictionary<string, string>? ambiente, string[] argumentos)
    {
        if (!Permitido(argumentos)) throw new InvalidOperationException("Comando Git fora do conjunto autorizado.");
        var inicio = new ProcessStartInfo(executavel)
        {
            WorkingDirectory = cwd,
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
