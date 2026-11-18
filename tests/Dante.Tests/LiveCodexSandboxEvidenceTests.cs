using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Dante.Application.Execucoes;
using Dante.Domain.Execucoes;
using Microsoft.Extensions.DependencyInjection;
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

    // #308: a revisão de um roteiro com o Codex real e o dante_coordenacao real (o binário do Worker), sem modelo. O
    // driver real só abre a sessão se o thread/start confirmar o sandbox somente leitura sem rede; sob a mesma política,
    // ler a worktree passa e escrever nela, no checkout, no diretório Git comum, em /tmp e fora é negado; e o servidor
    // MCP, declarado sozinho depois de esvaziar os do usuário, entrega o veredito pelo pipe da sessão com o token, no
    // escopo fixado pelo orquestrador.
    [LiveCodexSandboxFact]
    public async Task RevisaoConfinadaDoCodexSoLeEEntregaOVereditoPeloCoordenacao()
    {
        using var temp = new RepositorioGitDeTeste(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "dante-live-revisao-" + Guid.NewGuid().ToString("N")));
        var configDoCodex = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "config.toml");
        var configAntes = Hash(configDoCodex);
        var principal = temp.Criar("principal");
        var impressao = RepositorioGitDeTeste.Impressao(principal.Caminho);
        var gerenciador = new GerenciadorDeWorktreesGit(new OpcoesDeWorktrees { Raiz = temp.RaizDasWorktrees },
            new CatalogoFixo(principal));
        var w = await gerenciador.PlanejarAsync(principal, 1, "dev");
        await gerenciador.CriarOuAdotarAsync(w);
        File.WriteAllText(Path.Combine(w.Caminho, "alterado.txt"), "do implementador\n");
        var noTmp = Path.Combine(Path.GetTempPath(), "dante-live-revisao-" + Guid.NewGuid().ToString("N"));
        using var prazo = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        // O servidor do dante_coordenacao é o Worker compilado, iniciado pelo próprio Codex (dotnet exec).
        var roteiros = new RoteirosFalso();
        await using var provider = new Microsoft.Extensions.DependencyInjection.ServiceCollection()
            .AddSingleton<IRoteirosAppService>(roteiros).BuildServiceProvider();
        var worker = typeof(Dante.Worker.Coordenacao.ServidorMcpDeCoordenacao).Assembly.Location;
        using var coordenacao = new Dante.Worker.Coordenacao.FerramentasDeCoordenacao(
            provider.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Dante.Worker.Coordenacao.FerramentasDeCoordenacao>.Instance,
            (new DotnetHost().Caminho, ["exec", "--runtimeconfig", Path.Combine(AppContext.BaseDirectory, "Dante.Tests.runtimeconfig.json"), worker]));
        var escopo = new EscopoDaRevisaoDto(Guid.NewGuid(), "E1", Guid.NewGuid(), 0, new string('a', 64));
        AgentToolServer Servidor(string sessao) => Assert.Single(coordenacao.ForSessionAsync(sessao, new SessionStartRequest(1,
            AgentKind.Codex, Dante.Application.Contextos.JobExecutionContext.Repository("@principal", w.Caminho),
            RegisteredAgent: new AgenteDaSessao("codex-rev", new Dante.Application.Agentes.VersaoDoAgenteDto
            {
                Id = Guid.NewGuid(), Numero = 1, HashDaConfiguracao = new string('b', 64),
                Configuracao = new Dante.Domain.Agentes.ConfiguracaoDoAgente(AgentKind.Codex, AgentPermissionProfile.Plan,
                    AgentPermissionProfile.Plan, false, capacidades: [Dante.Domain.Agentes.CapacidadeDoAgente.Revisar])
            }) { LeituraConfinada = true, EscopoDaRevisao = escopo }, Observer: new SemEventos())).Result);

        // 1. O driver real abre a revisão confinada só com a política confirmada pelo Codex real.
        await using (var driver = new CodexSessionDriver(new InteractiveAgentProcessLauncher(new AgentExecutableResolver(),
                         new OpcoesContencaoAgentes { Modo = ModoContencaoAgentes.Compatibilidade })))
        {
            await driver.StartAsync(new AgentSessionStartOptions(w.Caminho, Profile: AgentPermissionProfile.Plan,
                ToolServers: [Servidor("S-driver")], LeituraConfinada: true, ProjetosNaoConfiaveis: [principal.Caminho]),
                prazo.Token);
        }
        coordenacao.EndSession("S-driver");

        // 2. Comandos reais sob a política somente leitura da revisão.
        var servidorMcp = Servidor("S-evidencia");
        await using var servidor = await AppServer.IniciarAsync(w.Caminho, prazo.Token, servidorMcp);
        var politica = CodexSessionDriver.ConfinedReadSandboxPolicy();
        async Task<int> Rodar(params string[] comando)
        {
            var resultado = await servidor.ExecutarAsync(comando, w.Caminho, politica, prazo.Token);
            output.WriteLine($"{string.Join(' ', comando)} -> {resultado}");
            return (int)resultado["exitCode"]!;
        }
        Assert.Equal(0, await Rodar("cat", "alterado.txt"));
        Assert.Equal(0, await Rodar("git", "diff", "--stat"));
        Assert.NotEqual(0, await Rodar("sh", "-c", "echo x > dentro.txt"));
        Assert.NotEqual(0, await Rodar("sh", "-c", "echo x >> alterado.txt"));
        Assert.NotEqual(0, await Rodar("sh", "-c", $"echo x > '{Path.Combine(principal.Caminho, "invasao.txt")}'"));
        Assert.NotEqual(0, await Rodar("sh", "-c", $"echo x > '{Path.Combine(principal.Caminho, ".git", "invasao")}'"));
        Assert.NotEqual(0, await Rodar("git", "-c", "user.name=t", "-c", "user.email=t@t", "commit", "--allow-empty", "-qm", "x"));
        Assert.NotEqual(0, await Rodar("sh", "-c", $"mkdir '{noTmp}'"));

        // 3. O veredito pelo dante_coordenacao real: thread somente leitura, ferramenta chamada sem modelo.
        var thread = await servidor.IniciarThreadDeRevisaoAsync(w.Caminho, prazo.Token);
        Assert.Equal("readOnly", (string?)thread["sandbox"]!["type"]);
        Assert.True(CodexSessionDriver.ConfirmsReadConfinement(thread, w.Caminho));
        var chamada = await servidor.ChamarFerramentaAsync((string)thread["thread"]!["id"]!, "entregar_resultado", new JsonObject
        {
            ["veredito"] = "requer_correcoes", ["resumo"] = "falta teste", ["itens"] = new JsonArray("adicione o teste")
        }, prazo.Token);
        output.WriteLine($"entregar_resultado -> {chamada}");
        Assert.False((bool?)chamada["isError"] ?? false);
        var registro = Assert.Single(roteiros.Chamadas, c => c.Veredito is not null);
        Assert.Same(escopo, registro.Escopo);
        Assert.Equal((VereditoDeRevisao.RequerCorrecoes, "S-evidencia"), (registro.Veredito!.Veredito, registro.Sessao));

        Assert.Equal("do implementador\n", File.ReadAllText(Path.Combine(w.Caminho, "alterado.txt")));
        Assert.False(File.Exists(Path.Combine(w.Caminho, "dentro.txt")));
        Assert.False(Directory.Exists(noTmp));
        Assert.Equal(impressao, RepositorioGitDeTeste.Impressao(principal.Caminho));
        Assert.Equal(configAntes, Hash(configDoCodex));
    }

    private sealed class SemEventos : IAgentSessionEventSink
    {
        public Task PublishAsync(AgentSessionSnapshot session, AgentEvent agentEvent, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class DotnetHost
    {
        public string Caminho { get; } = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(diretorio => Path.Combine(diretorio, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"))
            .FirstOrDefault(File.Exists) ?? "dotnet";
    }

    private static string? Hash(string arquivo) =>
        File.Exists(arquivo) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(arquivo))) : null;

    // Cliente JSON-RPC mínimo do app-server: initialize e command/exec.
    private sealed class AppServer : IAsyncDisposable
    {
        private readonly Process processo;
        private long proximo;

        private AppServer(Process processo) => this.processo = processo;

        // servidorMcp: o único servidor declarado depois de esvaziar os do usuário, como o driver faz na revisão (#308).
        public static async Task<AppServer> IniciarAsync(string cwd, CancellationToken cancellationToken,
            AgentToolServer? servidorMcp = null)
        {
            var executavel = new AgentExecutableResolver().Resolve(AgentKind.Codex) ?? "codex";
            var inicio = new ProcessStartInfo(executavel)
            {
                WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var argumento in new[] { "app-server", "--listen", "stdio://", "-c", "mcp_servers={}" })
                inicio.ArgumentList.Add(argumento);
            if (servidorMcp is not null)
            {
                var prefixo = $"mcp_servers.{servidorMcp.Name}";
                foreach (var argumento in new[]
                         {
                             "-c", $"{prefixo}.command={System.Text.Json.JsonSerializer.Serialize(servidorMcp.Command)}",
                             "-c", $"{prefixo}.args={System.Text.Json.JsonSerializer.Serialize(servidorMcp.Arguments)}",
                             "-c", $"{prefixo}.env={{{string.Join(", ", servidorMcp.Environment.Select(par => $"{par.Key} = {System.Text.Json.JsonSerializer.Serialize(par.Value)}"))}}}",
                             "-c", $"{prefixo}.startup_timeout_sec=30"
                         })
                    inicio.ArgumentList.Add(argumento);
            }
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

        // thread/start como o driver faz na revisão: never, read-only e o checkout não confiável.
        public Task<JsonObject> IniciarThreadDeRevisaoAsync(string cwd, CancellationToken cancellationToken) =>
            PedirAsync("thread/start", new JsonObject
            {
                ["cwd"] = cwd, ["approvalPolicy"] = "never", ["sandbox"] = "read-only", ["ephemeral"] = true
            }, cancellationToken);

        public Task<JsonObject> ChamarFerramentaAsync(string thread, string ferramenta, JsonObject argumentos,
            CancellationToken cancellationToken) => PedirAsync("mcpServer/tool/call", new JsonObject
        {
            ["threadId"] = thread, ["server"] = "dante_coordenacao", ["tool"] = ferramenta, ["arguments"] = argumentos
        }, cancellationToken);

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
