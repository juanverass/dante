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
public sealed partial class GerenciadorDeWorktreesGit(
    OpcoesDeWorktrees opcoes,
    ICatalogoDeRepositorios catalogo,
    TimeSpan? limite = null,
    string executavel = "git") : IGerenciadorDeWorktrees
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
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        hash.AppendData(System.Text.Encoding.UTF8.GetBytes("formato=1\ndiff\n" + diff.Saida + "\0novos\n"));
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
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(relativo + "\0" + conteudo + "\n"));
        }
        var cortado = diff.Saida.Length > limiteDoDiff;
        return new AlteracoesDaWorktreeDto(cortado ? diff.Saida[..limiteDoDiff] : diff.Saida, cortado, caminhos,
            Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

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
        _ => false
    };

    private async Task<(int Codigo, string Saida, string Erro)> GitAsync(string cwd, CancellationToken cancellationToken,
        params string[] argumentos)
    {
        if (!Permitido(argumentos)) throw new InvalidOperationException("Comando Git fora do conjunto autorizado.");
        var inicio = new ProcessStartInfo(executavel)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argumento in (string[])["-c", "core.fsmonitor=false", "-c", "core.hooksPath=/dev/null", "-c",
                     "core.quotepath=off", .. argumentos])
            inicio.ArgumentList.Add(argumento);
        inicio.Environment["GIT_TERMINAL_PROMPT"] = "0";
        inicio.Environment["LC_ALL"] = "C";
        using var prazo = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        prazo.CancelAfter(limite);
        Process? processo = null;
        try
        {
            processo = Process.Start(inicio) ?? throw new WorktreeInvalidaException("Não foi possível executar o Git.");
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
            try { if (processo is { HasExited: false }) processo.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            processo?.Dispose();
        }
    }

    [GeneratedRegex("^@[a-z][a-z0-9_]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AliasValido();

    [GeneratedRegex("^[a-z][a-z0-9-]{1,30}$", RegexOptions.CultureInvariant)]
    private static partial Regex HandleValido();

    [GeneratedRegex("^E[1-9][0-9]{0,18}$", RegexOptions.CultureInvariant)]
    private static partial Regex NomeDaWorktree();

    [GeneratedRegex("^dante/[a-z][a-z0-9-]{1,30}/e[1-9][0-9]{0,18}$", RegexOptions.CultureInvariant)]
    private static partial Regex BranchValida();
}
