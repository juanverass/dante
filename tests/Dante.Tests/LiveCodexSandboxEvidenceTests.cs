using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Dante.Application.Execucoes;
using Dante.Infrastructure.Agentes;
using Dante.Infrastructure.Contextos;
using Dante.Worker.Sessions;
using Xunit.Abstractions;

namespace Dante.Tests;

// #307: evidência com o Codex CLI real de que a escrita confinada é imposta pelo sandbox do SO, não pelo prompt. Nenhum
// modelo é chamado (nenhum token): o driver real abre a sessão confinada (o thread/start real precisa confirmar a
// política), e command/exec do app-server roda comandos reais sob a mesma política, em worktrees reais criadas pelo
// GerenciadorDeWorktreesGit. Escrever nas worktrees passa; checkout principal, diretório Git comum (commit, refs), o
// arquivo .git da worktree, /tmp e o resto do disco são negados.
// DANTE_LIVE_CODEX_SANDBOX=1 dotnet test Dante.sln --filter FullyQualifiedName~LiveCodexSandboxEvidenceTests
public sealed class LiveCodexSandboxEvidenceTests(ITestOutputHelper output)
{
    [LiveCodexSandboxFact]
    public async Task EscritaConfinadaDoCodexSoAlcancaAsWorktrees()
    {
        using var temp = new RepositorioGitDeTeste(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "dante-live-sandbox-" + Guid.NewGuid().ToString("N")));
        var configDoCodex = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "config.toml");
        var configAntes = Hash(configDoCodex);
        var principal = temp.Criar("principal");
        var vinculado = temp.Criar("vinculado");
        var impressoes = (RepositorioGitDeTeste.Impressao(principal.Caminho), RepositorioGitDeTeste.Impressao(vinculado.Caminho));
        var gerenciador = new GerenciadorDeWorktreesGit(new OpcoesDeWorktrees { Raiz = temp.RaizDasWorktrees },
            new CatalogoFixo(principal, vinculado));
        var wp = await gerenciador.PlanejarAsync(principal, 1, "dev");
        var wv = await gerenciador.PlanejarAsync(vinculado, 1, "dev");
        await gerenciador.CriarOuAdotarAsync(wp);
        await gerenciador.CriarOuAdotarAsync(wv);
        var foraDoTmp = Path.Combine(temp.Raiz, "fora");
        Directory.CreateDirectory(foraDoTmp);
        var noTmp = Path.Combine(Path.GetTempPath(), "dante-live-sandbox-" + Guid.NewGuid().ToString("N"));
        using var prazo = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        // 1. O driver real só abre a sessão se o Codex real confirmar a política confinada (exceção, caso contrário).
        // Contenção de processo em compatibilidade: a evidência é do sandbox de filesystem, não da AD-63, e o teste não
        // cria unidade systemd.
        await using (var driver = new CodexSessionDriver(new InteractiveAgentProcessLauncher(new AgentExecutableResolver(),
                         new OpcoesContencaoAgentes { Modo = ModoContencaoAgentes.Compatibilidade })))
        {
            await driver.StartAsync(new AgentSessionStartOptions(wp.Caminho, Profile: AgentPermissionProfile.Auto,
                AdditionalDirectories: [wv.Caminho], EscritaConfinada: true,
                ProjetosNaoConfiaveis: [principal.Caminho, vinculado.Caminho]), prazo.Token);
        }

        // 2. Comandos reais sob a mesma política.
        await using var servidor = await AppServer.IniciarAsync(wp.Caminho, prazo.Token);
        var politica = CodexSessionDriver.ConfinedSandboxPolicy([wv.Caminho]);
        async Task<int> Rodar(params string[] comando)
        {
            var resultado = await servidor.ExecutarAsync(comando, wp.Caminho, politica, prazo.Token);
            output.WriteLine($"{string.Join(' ', comando)} -> {resultado}");
            return (int)resultado["exitCode"]!;
        }
        var git = new[] { "git", "-c", "user.name=t", "-c", "user.email=t@t" };
        Assert.Equal(0, await Rodar("sh", "-c", "echo agente > dentro.txt"));
        Assert.Equal(0, await Rodar("sh", "-c", $"echo agente > '{Path.Combine(wv.Caminho, "vinculado.txt")}'"));
        Assert.NotEqual(0, await Rodar("sh", "-c", $"echo x > '{Path.Combine(principal.Caminho, "invasao.txt")}'"));
        Assert.NotEqual(0, await Rodar("sh", "-c", $"echo x > '{Path.Combine(vinculado.Caminho, "invasao.txt")}'"));
        Assert.NotEqual(0, await Rodar("sh", "-c", $"echo x > '{Path.Combine(principal.Caminho, ".git", "invasao")}'"));
        Assert.NotEqual(0, await Rodar("sh", "-c", "echo x > .git"));
        Assert.NotEqual(0, await Rodar([.. git, "add", "dentro.txt"]));
        Assert.NotEqual(0, await Rodar([.. git, "commit", "--allow-empty", "-qm", "x"]));
        Assert.NotEqual(0, await Rodar("git", "update-ref", "refs/heads/invasao", "HEAD"));
        Assert.NotEqual(0, await Rodar("git", "-C", principal.Caminho, "checkout", "-q", "-b", "invasao"));
        Assert.NotEqual(0, await Rodar("sh", "-c", $"echo x > '{Path.Combine(foraDoTmp, "invasao.txt")}'"));
        Assert.NotEqual(0, await Rodar("sh", "-c", $"mkdir '{noTmp}'"));

        Assert.True(File.Exists(Path.Combine(wp.Caminho, "dentro.txt")));
        Assert.True(File.Exists(Path.Combine(wv.Caminho, "vinculado.txt")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(foraDoTmp));
        Assert.False(Directory.Exists(noTmp));
        Assert.False(File.Exists(Path.Combine(principal.Caminho, ".git", "invasao")));
        Assert.DoesNotContain("invasao", RepositorioGitDeTeste.Git(principal.Caminho, "branch", "--list"));
        Assert.Equal("", RepositorioGitDeTeste.Git(wp.Caminho, "diff", "--cached", "--name-only"));
        Assert.Equal(impressoes, (RepositorioGitDeTeste.Impressao(principal.Caminho), RepositorioGitDeTeste.Impressao(vinculado.Caminho)));
        // O Codex não acrescentou nada à configuração do usuário (por exemplo, confiar no projeto temporário).
        Assert.Equal(configAntes, Hash(configDoCodex));
    }

    private static string? Hash(string arquivo) =>
        File.Exists(arquivo) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(arquivo))) : null;

    // Cliente JSON-RPC mínimo do app-server: initialize e command/exec.
    private sealed class AppServer : IAsyncDisposable
    {
        private readonly Process processo;
        private long proximo;

        private AppServer(Process processo) => this.processo = processo;

        public static async Task<AppServer> IniciarAsync(string cwd, CancellationToken cancellationToken)
        {
            var executavel = new AgentExecutableResolver().Resolve(AgentKind.Codex) ?? "codex";
            var inicio = new ProcessStartInfo(executavel)
            {
                WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var argumento in new[] { "app-server", "--listen", "stdio://", "-c", "mcp_servers={}" })
                inicio.ArgumentList.Add(argumento);
            var servidor = new AppServer(Process.Start(inicio)!);
            _ = servidor.processo.StandardError.ReadToEndAsync(cancellationToken);
            await servidor.PedirAsync("initialize", new JsonObject
            {
                ["clientInfo"] = new JsonObject { ["name"] = "dante", ["version"] = "1.0" },
                ["capabilities"] = new JsonObject { ["experimentalApi"] = true }
            }, cancellationToken);
            await servidor.processo.StandardInput.WriteLineAsync(
                new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "initialized" }.ToJsonString());
            return servidor;
        }

        public Task<JsonObject> ExecutarAsync(string[] comando, string cwd, JsonObject politica,
            CancellationToken cancellationToken) => PedirAsync("command/exec", new JsonObject
        {
            ["command"] = new JsonArray(comando.Select(c => (JsonNode?)c).ToArray()),
            ["cwd"] = cwd,
            ["sandboxPolicy"] = politica.DeepClone(),
            ["timeoutMs"] = 20000
        }, cancellationToken);

        private async Task<JsonObject> PedirAsync(string metodo, JsonObject parametros, CancellationToken cancellationToken)
        {
            var id = Interlocked.Increment(ref proximo);
            await processo.StandardInput.WriteLineAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = metodo, ["params"] = parametros
            }.ToJsonString().AsMemory(), cancellationToken);
            await processo.StandardInput.FlushAsync(cancellationToken);
            while (await processo.StandardOutput.ReadLineAsync(cancellationToken) is { } linha)
            {
                if (JsonNode.Parse(linha) is not JsonObject mensagem || (long?)mensagem["id"] != id) continue;
                return mensagem["result"] as JsonObject ??
                    throw new InvalidOperationException($"{metodo} falhou: {mensagem["error"]?.ToJsonString()}");
            }
            throw new InvalidOperationException("O app-server encerrou sem responder.");
        }

        public async ValueTask DisposeAsync()
        {
            try { processo.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await processo.WaitForExitAsync();
            processo.Dispose();
        }
    }

    private sealed class LiveCodexSandboxFactAttribute : FactAttribute
    {
        public LiveCodexSandboxFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("DANTE_LIVE_CODEX_SANDBOX") != "1")
                Skip = "Evidência do sandbox com o Codex CLI real (sem chamada de modelo); rode com DANTE_LIVE_CODEX_SANDBOX=1.";
        }
    }
}
