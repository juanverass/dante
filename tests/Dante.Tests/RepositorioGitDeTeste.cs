using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Dante.Application.Contextos;

namespace Dante.Tests;

// Repositórios Git reais em diretório temporário para os testes de worktree (#307). Nada aqui toca checkout do projeto.
internal sealed class RepositorioGitDeTeste : IDisposable
{
    public RepositorioGitDeTeste(string? raiz = null)
    {
        Raiz = raiz ?? Path.Combine(Path.GetTempPath(), "dante-wt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Raiz);
        // Caminho real: no Linux /tmp não é link, mas a comparação dos testes usa sempre o canônico.
        Raiz = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Raiz));
        RaizDasWorktrees = Path.Combine(Raiz, "worktrees");
    }

    public string Raiz { get; }
    public string RaizDasWorktrees { get; }

    // Repositório com um commit (README e um arquivo de código) na branch main.
    public RepositorioCadastrado Criar(string nome, bool comCommit = true)
    {
        var caminho = Path.Combine(Raiz, nome);
        Directory.CreateDirectory(caminho);
        Git(caminho, "init", "-q", "-b", "main");
        if (comCommit)
        {
            File.WriteAllText(Path.Combine(caminho, "README.md"), $"# {nome}\n");
            File.WriteAllText(Path.Combine(caminho, "codigo.txt"), "original\n");
            Git(caminho, "add", "-A");
            Git(caminho, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "inicial");
        }
        return new RepositorioCadastrado("@" + nome, caminho);
    }

    // Impressão digital do checkout principal: arquivos de trabalho, index, HEAD, config e a ref da branch main.
    // Metadados de worktrees (.git/worktrees) e refs dante/* ficam de fora: são o que worktree add grava, no diretório
    // Git comum, e nunca no checkout.
    public static string Impressao(string checkout)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var arquivos = Directory.EnumerateFiles(checkout, "*", SearchOption.AllDirectories)
            .Where(f => !Path.GetRelativePath(checkout, f).StartsWith(".git" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Concat(new[] { ".git/HEAD", ".git/index", ".git/config", ".git/refs/heads/main" }
                .Select(f => Path.Combine(checkout, f)).Where(File.Exists))
            .Order(StringComparer.Ordinal);
        foreach (var arquivo in arquivos)
        {
            sha.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(checkout, arquivo) + "\0"));
            sha.AppendData(File.ReadAllBytes(arquivo));
        }
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    public static string Git(string cwd, params string[] argumentos)
    {
        var inicio = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argumento in argumentos) inicio.ArgumentList.Add(argumento);
        using var processo = Process.Start(inicio)!;
        var saida = processo.StandardOutput.ReadToEnd();
        var erro = processo.StandardError.ReadToEnd();
        processo.WaitForExit();
        if (processo.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', argumentos)} falhou: {erro}");
        return saida.Trim();
    }

    public void Dispose()
    {
        if (!Directory.Exists(Raiz)) return;
        // Links criados pelos testes não são seguidos: só o link é apagado.
        foreach (var link in Directory.EnumerateFileSystemEntries(Raiz, "*", SearchOption.AllDirectories)
                     .Where(e => new FileInfo(e).LinkTarget is not null).ToArray())
            File.Delete(link);
        Directory.Delete(Raiz, true);
    }
}

// Catálogo fixo: só Listar importa ao gerenciador de worktrees.
internal sealed class CatalogoFixo(params RepositorioCadastrado[] repositorios) : ICatalogoDeRepositorios
{
    public List<RepositorioCadastrado> Repositorios { get; } = [.. repositorios];

    public IReadOnlyList<RepositorioCadastrado> Listar() => Repositorios;

    public RepositorioCadastrado? Obter(string alias) =>
        Repositorios.FirstOrDefault(r => string.Equals(r.Alias, alias, StringComparison.OrdinalIgnoreCase));

    public ResolvedRepositoryEnvironment ResolverAmbiente(string alias) => throw new NotSupportedException();

    public IReadOnlyList<RepositorioCadastrado> ResolverVinculos(string alias) => [];
}
