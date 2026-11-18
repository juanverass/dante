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

    // #308, review do PR #317: a revisão de um roteiro com o Codex real (0.166), o driver real e um turno real, sem
    // chamada de modelo paga: o "modelo" é um provedor Responses local roteirizado, com CODEX_HOME temporário (a
    // configuração do usuário não é lida nem alterada). O modelo manda o shell ler arquivos fora da worktree (outro
    // diretório com .env, o checkout principal, a home real, /srv), pede execução escalonada, tenta escrever e entrega o
    // veredito pelo dante_coordenacao real (o binário do Worker). Só a worktree e o veredito podem passar.
    [LiveCodexSandboxFact]
    public async Task RevisaoConfinadaDoCodexRealSoLeAWorktreeEEntregaOVereditoNumTurno()
    {
        using var temp = new RepositorioGitDeTeste(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "dante-live-revisao-" + Guid.NewGuid().ToString("N")));
        var configDoCodex = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "config.toml");
        var configAntes = Hash(configDoCodex);
        var principal = temp.Criar("principal");
        File.WriteAllText(Path.Combine(principal.Caminho, ".env"), "SEGREDO_DO_CHECKOUT=checkout-308\n");
        var impressao = RepositorioGitDeTeste.Impressao(principal.Caminho);
        var fora = Path.Combine(temp.Raiz, "fora");
        Directory.CreateDirectory(fora);
        File.WriteAllText(Path.Combine(fora, ".env"), "SEGREDO_DE_FORA=fora-308\n");
        var gerenciador = new GerenciadorDeWorktreesGit(new OpcoesDeWorktrees { Raiz = temp.RaizDasWorktrees }, new CatalogoFixo(principal));
        var w = await gerenciador.PlanejarAsync(principal, 1, "dev");
        await gerenciador.CriarOuAdotarAsync(w);
        File.WriteAllText(Path.Combine(w.Caminho, "alterado.txt"), "do implementador\n");
        var authReal = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "auth.json");

        await using var modelo = await ModeloDeTeste.IniciarAsync(
        [
            Shell($"cat '{Path.Combine(fora, ".env")}'; cat '{Path.Combine(principal.Caminho, ".env")}'; cat '{authReal}'; ls /srv"),
            Shell($"cat '{Path.Combine(fora, ".env")}'", escalonado: true),
            Shell("echo x > invasao.txt; cat alterado.txt"),
            new JsonObject
            {
                ["name"] = "entregar_resultado", ["namespace"] = "mcp__dante_coordenacao",
                ["args"] = new JsonObject { ["veredito"] = "requer_correcoes", ["resumo"] = "falta teste", ["itens"] = new JsonArray("adicione o teste") }
            }
        ]);
        var codexHome = modelo.CodexHome(Path.Combine(temp.Raiz, "codex-home"));

        var roteiros = new RoteirosFalso();
        await using var provider = new ServiceCollection().AddSingleton<IRoteirosAppService>(roteiros).BuildServiceProvider();
        var worker = typeof(Dante.Worker.Coordenacao.ServidorMcpDeCoordenacao).Assembly.Location;
        using var coordenacao = new Dante.Worker.Coordenacao.FerramentasDeCoordenacao(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Dante.Worker.Coordenacao.FerramentasDeCoordenacao>.Instance,
            (new DotnetHost().Caminho, ["exec", "--runtimeconfig", Path.Combine(AppContext.BaseDirectory, "Dante.Tests.runtimeconfig.json"), worker]));
        var escopo = new EscopoDaRevisaoDto(Guid.NewGuid(), "E1", Guid.NewGuid(), 0, new string('a', 64));
        var servidor = Assert.Single(await coordenacao.ForSessionAsync("S-evidencia", new SessionStartRequest(1, AgentKind.Codex,
            Dante.Application.Contextos.JobExecutionContext.Repository("@principal", w.Caminho),
            RegisteredAgent: new AgenteDaSessao("codex-rev", new Dante.Application.Agentes.VersaoDoAgenteDto
            {
                Id = Guid.NewGuid(), Numero = 1, HashDaConfiguracao = new string('b', 64),
                Configuracao = new Dante.Domain.Agentes.ConfiguracaoDoAgente(AgentKind.Codex, AgentPermissionProfile.Plan,
                    AgentPermissionProfile.Plan, false, capacidades: [Dante.Domain.Agentes.CapacidadeDoAgente.Revisar])
            }) { LeituraConfinada = true, EscopoDaRevisao = escopo }, Observer: new SemEventos())));

        var eventos = await TurnoDaRevisaoAsync(w.Caminho, principal.Caminho, servidor, codexHome);
        foreach (var linha in eventos) output.WriteLine(linha);
        var saidas = modelo.Saidas;
        foreach (var saida in saidas) output.WriteLine("saída -> " + saida);
        // 1. Nada fora da worktree existe no sandbox.
        Assert.Contains("No such file", saidas[0]);
        Assert.DoesNotContain("fora-308", saidas[0]);
        Assert.DoesNotContain("checkout-308", saidas[0]);
        Assert.DoesNotContain("refresh_token", saidas[0]);
        Assert.Contains("/srv", saidas[0]);
        Assert.Equal(4, System.Text.RegularExpressions.Regex.Matches(saidas[0], "No such file or directory").Count);
        // 2. Execução escalonada recusada pela própria política.
        Assert.Contains("cannot ask for escalated permissions", saidas[1]);
        Assert.DoesNotContain("fora-308", saidas[1]);
        // 3. Escrever é negado; ler a worktree passa.
        Assert.Contains("Read-only file system", saidas[2]);
        Assert.Contains("do implementador", saidas[2]);
        // 4. O veredito chegou ao caso de uso, no escopo fixado, sem pedido de aprovação a ninguém.
        Assert.Contains("registrado", saidas[3]);
        var registro = Assert.Single(roteiros.Chamadas, c => c.Veredito is not null);
        Assert.Same(escopo, registro.Escopo);
        Assert.Equal(VereditoDeRevisao.RequerCorrecoes, registro.Veredito!.Veredito);
        Assert.DoesNotContain(eventos, e => e.StartsWith("aprovação", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(w.Caminho, "invasao.txt")));
        Assert.Equal(impressao, RepositorioGitDeTeste.Impressao(principal.Caminho));
        Assert.Equal(configAntes, Hash(configDoCodex));
    }

    // A autorização restrita do driver com a elicitação real do app-server: um servidor de teste chamado
    // dante_coordenacao com ferramentas que exigem aprovação. O driver aceita só entregar_resultado e recusa a outra;
    // nenhum pedido chega a uma pessoa.
    [LiveCodexSandboxFact]
    public async Task AutorizacaoDaRevisaoSoAceitaAEntregaDoVereditoNaElicitacaoReal()
    {
        using var temp = new RepositorioGitDeTeste(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "dante-live-elicitacao-" + Guid.NewGuid().ToString("N")));
        var principal = temp.Criar("principal");
        var gerenciador = new GerenciadorDeWorktreesGit(new OpcoesDeWorktrees { Raiz = temp.RaizDasWorktrees }, new CatalogoFixo(principal));
        var w = await gerenciador.PlanejarAsync(principal, 1, "dev");
        await gerenciador.CriarOuAdotarAsync(w);
        var executadas = Path.Combine(temp.Raiz, "executadas.txt");
        await using var modelo = await ModeloDeTeste.IniciarAsync(
        [
            new JsonObject { ["name"] = "entregar_resultado", ["namespace"] = "mcp__dante_coordenacao", ["args"] = new JsonObject() },
            new JsonObject { ["name"] = "outra_ferramenta", ["namespace"] = "mcp__dante_coordenacao", ["args"] = new JsonObject() }
        ]);
        var servidor = new AgentToolServer(Dante.Worker.Coordenacao.ServidorMcpDeCoordenacao.Nome, new DotnetHost().Caminho,
            ["exec", "--runtimeconfig", Path.Combine(AppContext.BaseDirectory, "Dante.Tests.runtimeconfig.json"),
                typeof(Dante.ProcessProbe.ProbeMarker).Assembly.Location, "fake-mcp-aprovacao", executadas],
            new Dictionary<string, string>(), []);
        var eventos = await TurnoDaRevisaoAsync(w.Caminho, principal.Caminho, servidor, modelo.CodexHome(Path.Combine(temp.Raiz, "codex-home")));
        foreach (var linha in eventos.Concat(modelo.Saidas)) output.WriteLine(linha);
        Assert.Equal(["entregar_resultado"], File.ReadAllLines(executadas));
        Assert.Contains("executada entregar_resultado", modelo.Saidas[0]);
        Assert.Contains("rejected", modelo.Saidas[1]);
        Assert.DoesNotContain(eventos, e => e.StartsWith("aprovação", StringComparison.Ordinal));
    }

    // Um turno da revisão confinada pelo driver real: devolve os eventos relevantes, em texto.
    private static async Task<List<string>> TurnoDaRevisaoAsync(string worktree, string checkout, AgentToolServer servidor,
        string codexHome)
    {
        using var prazo = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var driver = new CodexSessionDriver(new InteractiveAgentProcessLauncher(new AgentExecutableResolver(),
            new OpcoesContencaoAgentes { Modo = ModoContencaoAgentes.Compatibilidade }));
        await driver.StartAsync(new AgentSessionStartOptions(worktree, false,
            new Dictionary<string, string> { ["CODEX_HOME"] = codexHome }, AgentPermissionProfile.Plan,
            ToolServers: [servidor], LeituraConfinada: true, ProjetosNaoConfiaveis: [checkout]), prazo.Token);
        await using var eventos = driver.ReadEventsAsync(prazo.Token).GetAsyncEnumerator(prazo.Token);
        await driver.StartTurnAsync("revise", prazo.Token);
        var lidos = new List<string>();
        while (await eventos.MoveNextAsync())
        {
            switch (eventos.Current)
            {
                case ApprovalRequestedEvent pedido:
                    lidos.Add("aprovação " + pedido.Action);
                    break;
                case TurnCompletedEvent fim:
                    lidos.Add("fim " + fim.Outcome);
                    return lidos;
            }
        }
        return lidos;
    }

    private static JsonObject Shell(string comando, bool escalonado = false)
    {
        var argumentos = new JsonObject { ["cmd"] = comando, ["login"] = false };
        if (escalonado)
        {
            argumentos["sandbox_permissions"] = "require_escalated";
            argumentos["justification"] = "preciso ler";
        }
        return new JsonObject { ["name"] = "exec_command", ["args"] = argumentos };
    }

    // Provedor Responses local roteirizado: a cada pedido, a próxima chamada de ferramenta; depois, uma mensagem final.
    // Guarda a saída de cada ferramenta que o Codex devolve no pedido seguinte.
    private sealed class ModeloDeTeste : IAsyncDisposable
    {
        private readonly System.Net.HttpListener ouvinte = new();
        private readonly IReadOnlyList<JsonObject> passos;
        private readonly List<string> saidas = [];
        private readonly CancellationTokenSource fim = new();
        private int pedidos;
        private Task? laco;

        private ModeloDeTeste(IReadOnlyList<JsonObject> passos, int porta)
        {
            this.passos = passos;
            Porta = porta;
            ouvinte.Prefixes.Add($"http://127.0.0.1:{porta}/");
        }

        public int Porta { get; }

        public IReadOnlyList<string> Saidas { get { lock (saidas) return saidas.ToArray(); } }

        public static Task<ModeloDeTeste> IniciarAsync(IReadOnlyList<JsonObject> passos)
        {
            var livre = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            livre.Start();
            var porta = ((System.Net.IPEndPoint)livre.LocalEndpoint).Port;
            livre.Stop();
            var modelo = new ModeloDeTeste(passos, porta);
            modelo.ouvinte.Start();
            modelo.laco = modelo.AtenderAsync();
            return Task.FromResult(modelo);
        }

        public string CodexHome(string diretorio)
        {
            Directory.CreateDirectory(diretorio);
            File.WriteAllText(Path.Combine(diretorio, "config.toml"),
                "model = \"mock-model\"\nmodel_provider = \"mock\"\n[model_providers.mock]\nname = \"mock\"\n" +
                $"base_url = \"http://127.0.0.1:{Porta}/v1\"\nwire_api = \"responses\"\nrequires_openai_auth = false\n");
            return diretorio;
        }

        private async Task AtenderAsync()
        {
            while (!fim.IsCancellationRequested)
            {
                System.Net.HttpListenerContext contexto;
                try { contexto = await ouvinte.GetContextAsync(); }
                catch (Exception) { return; }
                if (contexto.Request.HttpMethod != "POST")
                {
                    contexto.Response.StatusCode = 404;
                    contexto.Response.Close();
                    continue;
                }
                using var leitor = new StreamReader(contexto.Request.InputStream);
                var corpo = JsonNode.Parse(await leitor.ReadToEndAsync())!.AsObject();
                var ultima = (corpo["input"] as JsonArray ?? []).OfType<JsonObject>()
                    .LastOrDefault(item => ((string?)item["type"])?.EndsWith("_output", StringComparison.Ordinal) == true);
                var indice = Interlocked.Increment(ref pedidos) - 1;
                if (indice > 0 && ultima is not null) lock (saidas) saidas.Add(ultima["output"]?.ToJsonString() ?? "");
                JsonObject item = indice < passos.Count
                    ? new JsonObject
                    {
                        ["type"] = "function_call", ["id"] = $"fc{indice}", ["call_id"] = $"call{indice}",
                        ["name"] = (string?)passos[indice]["name"], ["arguments"] = passos[indice]["args"]!.ToJsonString()
                    }
                    : new JsonObject
                    {
                        ["type"] = "message", ["id"] = "m", ["role"] = "assistant",
                        ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = "fim" })
                    };
                if (indice < passos.Count && passos[indice]["namespace"] is { } ns) item["namespace"] = ns.DeepClone();
                contexto.Response.ContentType = "text/event-stream";
                await using var escritor = new StreamWriter(contexto.Response.OutputStream);
                async Task Evento(string tipo, JsonObject dados)
                {
                    dados["type"] = tipo;
                    await escritor.WriteAsync($"event: {tipo}\ndata: {dados.ToJsonString()}\n\n");
                    await escritor.FlushAsync();
                }
                await Evento("response.created", new JsonObject { ["response"] = new JsonObject { ["id"] = $"r{indice}" } });
                await Evento("response.output_item.done", new JsonObject { ["item"] = item, ["output_index"] = 0 });
                await Evento("response.completed", new JsonObject
                {
                    ["response"] = new JsonObject
                    {
                        ["id"] = $"r{indice}",
                        ["usage"] = new JsonObject
                        {
                            ["input_tokens"] = 1, ["output_tokens"] = 1, ["total_tokens"] = 2,
                            ["input_tokens_details"] = new JsonObject { ["cached_tokens"] = 0 },
                            ["output_tokens_details"] = new JsonObject { ["reasoning_tokens"] = 0 }
                        }
                    }
                });
                contexto.Response.Close();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await fim.CancelAsync();
            ouvinte.Stop();
            ouvinte.Close();
            if (laco is not null) await laco;
        }
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
