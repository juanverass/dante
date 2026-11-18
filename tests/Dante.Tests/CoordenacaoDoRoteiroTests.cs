using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dante.Application.Agentes;
using Dante.Application.Contextos;
using Dante.Application.Execucoes;
using Dante.Domain.Agentes;
using Dante.Domain.Execucoes;
using Dante.Infrastructure.Agentes;
using Dante.Infrastructure.Contextos;
using Dante.ProcessProbe;
using Dante.Worker.Coordenacao;
using Dante.Worker.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dante.Tests;

// #308 (decisões D e E): fundação do dante_coordenacao (protocolo MCP, canal por sessão autenticado e com escopo fixo),
// a sessão Codex de revisão somente leitura e o resumo das alterações da worktree.
public sealed class CoordenacaoDoRoteiroTests : IDisposable
{
    private static readonly string Hash = new('d', 64);
    private readonly RepositorioGitDeTeste temp = new();

    public void Dispose() => temp.Dispose();

    [Fact]
    public async Task ServidorMcpAnunciaSoAsFerramentasDeCoordenacaoEValidaAFormaAntesDeEncaminhar()
    {
        var encaminhadas = new List<(string Nome, string Argumentos)>();
        var entrada = string.Join('\n',
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"entregar_resultado","arguments":{"veredito":"aprovado","resumo":"ok"}}}""",
            """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"entregar_resultado","arguments":{"veredito":"aprovado","resumo":"ok","execucao":"E1"}}}""",
            """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"brain_buscar","arguments":{}}}""",
            """{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"entregar_resultado","arguments":{"resumo":"sem veredito"}}}""",
            """{"jsonrpc":"2.0","id":7,"method":"resources/list"}""") + "\n";
        var saida = new StringWriter();
        await ServidorMcpDeCoordenacao.ExecutarAsync(new StringReader(entrada), saida, (nome, argumentos, _) =>
        {
            encaminhadas.Add((nome, argumentos.GetRawText()));
            return Task.FromResult(ServidorMcpDeCoordenacao.Resultado(new { registrado = true }));
        });
        var respostas = saida.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(linha => JsonNode.Parse(linha)).ToArray();
        Assert.Equal(7, respostas.Length);
        Assert.Equal("dante_coordenacao", (string?)respostas[0]!["result"]!["serverInfo"]!["name"]);
        var ferramentas = respostas[1]!["result"]!["tools"]!.AsArray();
        Assert.Equal(["estado_da_execucao", "entregar_resultado"], ferramentas.Select(f => (string?)f!["name"]));
        Assert.Equal([true, false], ferramentas.Select(f => (bool)f!["annotations"]!["readOnlyHint"]!));
        // Só a chamada bem formada chega ao Worker; execução, ferramenta alheia, argumento ausente e método desconhecido não.
        Assert.Single(encaminhadas);
        Assert.Equal("entregar_resultado", encaminhadas[0].Nome);
        Assert.False((bool)respostas[2]!["result"]!["isError"]!);
        Assert.All(respostas[3..], r => Assert.True((bool)r!["result"]!["isError"]!));
    }

    [Fact]
    public async Task CanalSoExisteNaRevisaoConfinadaExigeOTokenDaSessaoEUsaOEscopoDoOrquestrador()
    {
        var roteiros = new RoteirosFalso();
        await using var provider = new ServiceCollection().AddSingleton<IRoteirosAppService>(roteiros).BuildServiceProvider();
        using var ferramentas = new FerramentasDeCoordenacao(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<FerramentasDeCoordenacao>.Instance);
        var escopo = new EscopoDaRevisaoDto(Guid.NewGuid(), "E7", Guid.NewGuid(), 1, Hash);
        var contexto = JobExecutionContext.Repository("@dante", "/wt/dante/E7");
        var observador = new ObservadorNulo();
        // Sessão comum, sessão sem observador e escrita confinada: nenhum servidor.
        Assert.Empty(await ferramentas.ForSessionAsync("S1", new SessionStartRequest(1, AgentKind.Codex, contexto)));
        Assert.Empty(await ferramentas.ForSessionAsync("S2", new SessionStartRequest(1, AgentKind.Codex, contexto,
            RegisteredAgent: Agente(leitura: true, escopo))));
        Assert.Empty(await ferramentas.ForSessionAsync("S3", new SessionStartRequest(1, AgentKind.Codex, contexto,
            RegisteredAgent: Agente(leitura: false, escopo) with { EscritaConfinada = true }, Observer: observador)));
        var servidor = Assert.Single(await ferramentas.ForSessionAsync("S4", new SessionStartRequest(1, AgentKind.Codex,
            contexto, RegisteredAgent: Agente(leitura: true, escopo), Observer: observador)));
        Assert.Equal(ServidorMcpDeCoordenacao.Nome, servidor.Name);
        Assert.Equal(["estado_da_execucao"], servidor.ReadOnlyTools);
        var pipe = servidor.Arguments[^1];
        var token = servidor.Environment[ServidorMcpDeCoordenacao.VariavelDoToken];
        Assert.StartsWith(ServidorMcpDeCoordenacao.PrefixoDoPipe, pipe);
        Assert.Matches("^[0-9a-f]{64}$", token);

        // Token errado: o canal fecha sem atender, e a sessão perde o canal.
        await using (var intruso = await ConectarAsync(pipe))
        {
            await Escrever(intruso, new { token = new string('0', 64) });
            Assert.Null(await LerAsync(intruso));
        }
        Assert.Empty(roteiros.Chamadas);
        // A sessão S5, com o token certo: as chamadas usam o escopo fixado, nunca um informado.
        servidor = Assert.Single(await ferramentas.ForSessionAsync("S5", new SessionStartRequest(1, AgentKind.Codex,
            contexto, RegisteredAgent: Agente(leitura: true, escopo), Observer: observador)));
        await using (var cliente = await ConectarAsync(servidor.Arguments[^1]))
        {
            await Escrever(cliente, new { token = servidor.Environment[ServidorMcpDeCoordenacao.VariavelDoToken] });
            await Escrever(cliente, new { nome = "estado_da_execucao", argumentos = new { } });
            Assert.Equal(1, (int)JsonNode.Parse(Texto(await LerAsync(cliente)))!["rodada"]!);
            await Escrever(cliente, new
            {
                nome = "entregar_resultado",
                argumentos = new { veredito = "requer_correcoes", resumo = "falta teste", itens = new[] { "teste do caso vazio" } }
            });
            Assert.True((bool)JsonNode.Parse(Texto(await LerAsync(cliente)))!["registrado"]!);
            await Escrever(cliente, new { nome = "entregar_resultado", argumentos = new { veredito = "talvez", resumo = "x" } });
            Assert.Contains("Veredito inválido", Texto(await LerAsync(cliente)));
            // Recusa do caso de uso volta como mensagem ao agente.
            roteiros.Recusa = "O veredito desta revisão já foi registrado e não muda.";
            await Escrever(cliente, new { nome = "entregar_resultado", argumentos = new { veredito = "aprovado", resumo = "ok" } });
            Assert.Contains("já foi registrado", Texto(await LerAsync(cliente)));
        }
        Assert.All(roteiros.Chamadas, c => Assert.Same(escopo, c.Escopo));
        var registro = roteiros.Chamadas.Single(c => c.Veredito is not null);
        Assert.Equal((VereditoDeRevisao.RequerCorrecoes, "falta teste", "S5"), (registro.Veredito!.Veredito, registro.Veredito.Resumo,
            registro.Sessao));
        Assert.Equal(["teste do caso vazio"], registro.Veredito.Itens);
        ferramentas.EndSession("S5");
    }

    [Fact]
    public async Task SessaoDeRevisaoRecebeSoOCoordenacaoEAEscritaENenhumaOutraSessaoORecebem()
    {
        var drivers = new FakeSessionDriverFactory();
        var escopo = new EscopoDaRevisaoDto(Guid.NewGuid(), "E7", Guid.NewGuid(), 0, Hash);
        await using var sessoes = new SessionRegistry(drivers, NullLogger<SessionRegistry>.Instance,
            toolServers: new ServidoresFixos());
        var contexto = JobExecutionContext.Repository("@dante", "/wt/dante/E7");
        var revisao = await sessoes.StartAsync(new SessionStartRequest(1, AgentKind.Codex, contexto,
            Profile: AgentPermissionProfile.Plan, RegisteredAgent: Agente(leitura: true, escopo), Observer: new ObservadorNulo()));
        Assert.True(revisao.Accepted, revisao.Error);
        var opcoes = drivers.Created[^1].StartOptions!;
        Assert.True(opcoes.LeituraConfinada);
        Assert.Equal([ServidorMcpDeCoordenacao.Nome], (opcoes.ToolServers ?? []).Select(s => s.Name));
        // Agente registrado comum (com brain concedido): nunca recebe o coordenacao.
        var comum = await sessoes.StartAsync(new SessionStartRequest(2, AgentKind.Codex, contexto,
            Profile: AgentPermissionProfile.Plan, RegisteredAgent: Agente(leitura: false, null, brain: true)));
        Assert.True(comum.Accepted, comum.Error);
        Assert.Equal(["dante_brain"], (drivers.Created[^1].StartOptions!.ToolServers ?? []).Select(s => s.Name));
        // Recusas antes de qualquer processo: sem observador, fora de plan, sem escopo, Claude.
        var criados = drivers.Created.Count;
        foreach (var pedido in new[]
                 {
                     new SessionStartRequest(3, AgentKind.Codex, contexto, Profile: AgentPermissionProfile.Plan,
                         RegisteredAgent: Agente(leitura: true, escopo)),
                     new SessionStartRequest(3, AgentKind.Codex, contexto, Profile: AgentPermissionProfile.Plan,
                         RegisteredAgent: Agente(leitura: true, null), Observer: new ObservadorNulo()),
                     new SessionStartRequest(3, AgentKind.Codex, JobExecutionContext.General("/g"), Profile: AgentPermissionProfile.Plan,
                         RegisteredAgent: Agente(leitura: true, escopo), Observer: new ObservadorNulo()),
                     new SessionStartRequest(3, AgentKind.Claude, contexto, Profile: AgentPermissionProfile.Plan,
                         RegisteredAgent: Agente(leitura: true, escopo, driver: AgentKind.Claude), Observer: new ObservadorNulo())
                 })
            Assert.False((await sessoes.StartAsync(pedido)).Accepted);
        Assert.Equal(criados, drivers.Created.Count);
    }

    // Review do PR #317: a revisão confinada lê só a worktree, o mínimo de runtime e o diretório do Codex, por um perfil
    // de permissões imposto pelo sandbox do SO; ferramentas que agem fora do sandbox ficam desligadas; a política nunca
    // escala o sandbox. Só começa quando o thread/start confirma o perfil ativo, a política, o cwd e a ausência de rede.
    [Fact]
    public async Task RevisaoConfinadaDoCodexSoComOPerfilDeLeituraConfirmado()
    {
        var servidor = new AgentToolServer(ServidorMcpDeCoordenacao.Nome, "dotnet", ["dante.dll", "--mcp-coordenacao", "--pipe", "p"],
            new Dictionary<string, string> { ["DANTE_COORDENACAO_TOKEN"] = "t" }, ["estado_da_execucao"]);
        var launcher = new ProbeLauncher();
        await using (var driver = new CodexSessionDriver(launcher, runtimeDoCodex: () => "/opt/codex/bin"))
        {
            await driver.StartAsync(new AgentSessionStartOptions(AppContext.BaseDirectory, Profile: AgentPermissionProfile.Plan,
                ToolServers: [servidor], LeituraConfinada: true, ProjetosNaoConfiaveis: ["/repos/dante"]));
            var argumentos = Assert.Single(launcher.Requests).Arguments;
            Assert.Equal(["app-server", "--listen", "stdio://", "-c", "mcp_servers={}"], argumentos.Take(5));
            Assert.Contains("mcp_servers.dante_coordenacao.command=\"dotnet\"", argumentos);
            foreach (var restricao in new[] { "features.apps=false", "features.view_image=false", "features.multi_agent=false",
                         "features.goals=false", "web_search=\"disabled\"", "default_permissions=\"dante_revisao\"" })
                Assert.Contains(restricao, argumentos);
            // Só a worktree, o mínimo de runtime e o diretório do Codex são legíveis; nenhuma escrita.
            Assert.Equal($"permissions.dante_revisao.filesystem={{\":minimal\" = \"read\", " +
                $"{JsonSerializer.Serialize(AppContext.BaseDirectory)} = \"read\", \"/opt/codex/bin\" = \"read\"}}",
                argumentos.Single(a => a.StartsWith("permissions.", StringComparison.Ordinal)));
            await Assert.ThrowsAsync<AgentModeRejectedException>(() => driver.ChangeModeAsync(AgentPermissionProfile.Manual));
        }
        // Ambiente dos comandos: política imposta pela sessão, sem herança, só a allowlist, sem shell de login.
        var politica = CodexSessionDriver.PoliticaDeAmbienteDaRevisao();
        Assert.Equal(["shell_environment_policy.inherit=\"none\"", "shell_environment_policy.ignore_default_excludes=false",
                "shell_environment_policy.exclude=[\"*\"]",
                "shell_environment_policy.include_only=[\"PATH\",\"LANG\",\"LC_ALL\",\"LC_CTYPE\",\"TERM\"]",
                "shell_environment_policy.experimental_use_profile=false",
                "shell_environment_policy.set={PATH = \"/usr/local/bin:/usr/bin:/bin\"}", "allow_login_shell=false"],
            politica.Where((_, i) => i % 2 == 1));
        var iniciado = new ProbeLauncher();
        await using (var comPolitica = new CodexSessionDriver(iniciado, runtimeDoCodex: () => "/opt/codex/bin"))
        {
            await comPolitica.StartAsync(new AgentSessionStartOptions(AppContext.BaseDirectory, Profile: AgentPermissionProfile.Plan,
                LeituraConfinada: true));
            var recebidos = Assert.Single(iniciado.Requests).Arguments;
            foreach (var item in politica) Assert.Contains(item, recebidos);
        }
        // Servidor que não reporta o perfil ativo (versão antiga) ou que mantém rede: recusado depois de iniciar.
        foreach (var cenario in new[] { "sem-perfil", "read-network" })
        {
            await using var driver = new CodexSessionDriver(new ProbeLauncher(cenario), runtimeDoCodex: () => "/opt/codex/bin");
            Assert.Contains("confinamento da revisão", (await Assert.ThrowsAsync<AgentProtocolException>(() =>
                driver.StartAsync(new AgentSessionStartOptions(AppContext.BaseDirectory, Profile: AgentPermissionProfile.Plan,
                    LeituraConfinada: true)))).Message);
        }
        // Fora de plan, geral, com raízes extras, com outro servidor, junto com escrita, no checkout principal ou sem o
        // executável do Codex localizado: sem processo.
        var semProcesso = new ProbeLauncher();
        foreach (var (opcoes, runtime) in new (AgentSessionStartOptions, string?)[]
                 {
                     (new(AppContext.BaseDirectory, Profile: AgentPermissionProfile.Auto, LeituraConfinada: true), "/opt/codex/bin"),
                     (new(AppContext.BaseDirectory, IsGeneral: true, Profile: AgentPermissionProfile.Plan, LeituraConfinada: true), "/opt/codex/bin"),
                     (new(AppContext.BaseDirectory, Profile: AgentPermissionProfile.Plan, LeituraConfinada: true,
                         AdditionalDirectories: ["/wt/outra"]), "/opt/codex/bin"),
                     (new(AppContext.BaseDirectory, Profile: AgentPermissionProfile.Plan, LeituraConfinada: true,
                         ToolServers: [servidor with { Name = "dante_brain" }]), "/opt/codex/bin"),
                     (new(AppContext.BaseDirectory, Profile: AgentPermissionProfile.Auto, LeituraConfinada: true,
                         EscritaConfinada: true), "/opt/codex/bin"),
                     (new(AppContext.BaseDirectory, Profile: AgentPermissionProfile.Plan, LeituraConfinada: true,
                         ProjetosNaoConfiaveis: [AppContext.BaseDirectory]), "/opt/codex/bin"),
                     (new(AppContext.BaseDirectory, Profile: AgentPermissionProfile.Plan, LeituraConfinada: true), null),
                     (new(AppContext.BaseDirectory, Profile: AgentPermissionProfile.Plan, LeituraConfinada: true), "relativo/bin")
                 })
        {
            await using var outro = new CodexSessionDriver(semProcesso, runtimeDoCodex: () => runtime);
            await Assert.ThrowsAsync<AgentProtocolException>(() => outro.StartAsync(opcoes));
        }
        Assert.Empty(semProcesso.Requests);
    }

    // Review do PR #317: a política de ambiente dos comandos precisa estar na configuração efetiva antes da thread.
    // Config do usuário vencendo, valor do usuário para um nome permitido, shell de login ligado ou CLI sem config/read:
    // a revisão não começa (nenhum turno, nenhuma thread).
    [Theory]
    [InlineData("ambiente-herdado")]
    [InlineData("set-extra")]
    [InlineData("login")]
    [InlineData("sem-config-read")]
    public async Task RevisaoNaoComecaSemOAmbienteIsoladoConfirmado(string cenario)
    {
        await using var driver = new CodexSessionDriver(new ProbeLauncher(cenario), runtimeDoCodex: () => "/opt/codex/bin");
        Assert.Contains("ambiente isolado", (await Assert.ThrowsAsync<AgentProtocolException>(() =>
            driver.StartAsync(new AgentSessionStartOptions(AppContext.BaseDirectory, Profile: AgentPermissionProfile.Plan,
                LeituraConfinada: true)))).Message);
    }

    [Theory]
    [InlineData("ok", true)]
    [InlineData("inherit", false)]
    [InlineData("excludes-padrao-ignorados", false)]
    [InlineData("include-amplo", false)]
    [InlineData("exclude-vazio", false)]
    [InlineData("perfil-do-shell", false)]
    [InlineData("login", false)]
    [InlineData("filters", false)]
    [InlineData("path-do-usuario", false)]
    [InlineData("term-do-usuario", false)]
    [InlineData("set-alheio", true)]
    [InlineData("sem-politica", false)]
    public void AmbienteDaRevisaoSoComAPoliticaEfetivaExata(string variacao, bool confirmado)
    {
        var politica = new JsonObject
        {
            ["inherit"] = variacao == "inherit" ? "core" : "none",
            ["ignore_default_excludes"] = variacao == "excludes-padrao-ignorados",
            ["exclude"] = variacao == "exclude-vazio" ? new JsonArray() : new JsonArray("*"),
            ["include_only"] = variacao == "include-amplo" ? new JsonArray("*")
                : new JsonArray("PATH", "LANG", "LC_ALL", "LC_CTYPE", "TERM"),
            ["experimental_use_profile"] = variacao == "perfil-do-shell",
            ["filters"] = variacao == "filters" ? new JsonArray("x") : null,
            ["set"] = new JsonObject { ["PATH"] = variacao == "path-do-usuario" ? "/tmp/meu:/usr/bin" : "/usr/local/bin:/usr/bin:/bin" }
        };
        // Um valor do usuário para um nome fora da allowlist é descartado pelo include_only; para um nome permitido, não.
        if (variacao == "term-do-usuario") ((JsonObject)politica["set"]!)["term"] = "sk-segredo";
        if (variacao == "set-alheio") ((JsonObject)politica["set"]!)["OPENAI_API_KEY"] = "sk-segredo";
        var configuracao = new JsonObject
        {
            ["shell_environment_policy"] = variacao == "sem-politica" ? null : politica,
            ["allow_login_shell"] = variacao == "login"
        };
        Assert.Equal(confirmado, CodexSessionDriver.ConfirmsShellEnvironment(configuracao));
    }

    // Review do PR #317: na revisão confinada, a aprovação MCP é decidida pelo driver, sem chegar a ninguém: só a
    // ferramenta entregar_resultado do dante_coordenacao, na thread da própria sessão, é aceita; outra ferramenta, outro
    // servidor ou outra thread são recusados. Fora da revisão, plan continua recusando e as demais sessões perguntam.
    [Theory]
    [InlineData("mcp-veredito", "accept")]
    [InlineData("mcp-outra", "decline")]
    [InlineData("mcp-outro-servidor", "decline")]
    [InlineData("mcp-outra-thread", "decline")]
    public async Task RevisaoConfinadaSoAutorizaAEntregaDoVeredito(string cenario, string acao)
    {
        await using var driver = new CodexSessionDriver(new ProbeLauncher(), runtimeDoCodex: () => "/opt/codex/bin");
        await driver.StartAsync(new AgentSessionStartOptions(AppContext.BaseDirectory, Profile: AgentPermissionProfile.Plan,
            LeituraConfinada: true));
        await using var eventos = driver.ReadEventsAsync().GetAsyncEnumerator();
        await driver.StartTurnAsync(cenario);
        var turno = await TurnoAsync(eventos);
        Assert.Empty(turno.OfType<ApprovalRequestedEvent>());
        Assert.Equal($"mcp:{acao}", turno.OfType<MessageCompletedEvent>().Single().Text);
    }

    [Fact]
    public async Task ForaDaRevisaoAEntregaDoVereditoNaoTemAutorizacaoEspecial()
    {
        await using (var plano = new CodexSessionDriver(new ProbeLauncher()))
        {
            await plano.StartAsync(new AgentSessionStartOptions(AppContext.BaseDirectory, Profile: AgentPermissionProfile.Plan));
            await using var eventos = plano.ReadEventsAsync().GetAsyncEnumerator();
            await plano.StartTurnAsync("mcp-veredito");
            Assert.Equal("mcp:decline", (await TurnoAsync(eventos)).OfType<MessageCompletedEvent>().Single().Text);
        }
        await using var comum = new CodexSessionDriver(new ProbeLauncher());
        await comum.StartAsync(new AgentSessionStartOptions(AppContext.BaseDirectory));
        await using var deComum = comum.ReadEventsAsync().GetAsyncEnumerator();
        await comum.StartTurnAsync("mcp-veredito");
        // Uma sessão comum pergunta à pessoa, como qualquer ferramenta MCP que escreve.
        var pedido = await ProximoAsync<ApprovalRequestedEvent>(deComum);
        await comum.RespondAsync(pedido.UpstreamRequestId, new AgentApprovalResponse(AgentApprovalDecision.Deny));
    }

    private static async Task<List<AgentEvent>> TurnoAsync(IAsyncEnumerator<AgentEvent> eventos)
    {
        var lidos = new List<AgentEvent>();
        using var prazo = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (await eventos.MoveNextAsync().AsTask().WaitAsync(prazo.Token))
        {
            lidos.Add(eventos.Current);
            if (eventos.Current is TurnCompletedEvent) return lidos;
        }
        return lidos;
    }

    private static async Task<T> ProximoAsync<T>(IAsyncEnumerator<AgentEvent> eventos) where T : AgentEvent
    {
        using var prazo = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (await eventos.MoveNextAsync().AsTask().WaitAsync(prazo.Token))
            if (eventos.Current is T achado) return achado;
        throw new InvalidOperationException("Evento não recebido.");
    }

    [Theory]
    [InlineData("ok", true)]
    [InlineData("approval", false)]
    [InlineData("approval-texto", false)]
    [InlineData("cwd", false)]
    [InlineData("network", false)]
    [InlineData("sem-perfil", false)]
    [InlineData("outro-perfil", false)]
    [InlineData("write", false)]
    [InlineData("sem-rede-informada", true)]
    public void ConfinamentoDaRevisaoSoComAPoliticaEfetivaExata(string variacao, bool confirmado)
    {
        var sandbox = new JsonObject { ["type"] = variacao == "write" ? "workspaceWrite" : "readOnly" };
        if (variacao != "sem-rede-informada") sandbox["networkAccess"] = variacao == "network";
        var politica = CodexSessionDriver.PoliticaDaRevisao();
        if (variacao == "approval") politica["granular"]!["sandbox_approval"] = true;
        var thread = new JsonObject
        {
            ["approvalPolicy"] = variacao == "approval-texto" ? "never" : politica,
            ["cwd"] = variacao == "cwd" ? "/repos/dante" : "/wt/dante/E7",
            ["sandbox"] = sandbox,
            ["activePermissionProfile"] = variacao == "sem-perfil" ? null
                : new JsonObject { ["id"] = variacao == "outro-perfil" ? ":read-only" : "dante_revisao" }
        };
        Assert.Equal(confirmado, CodexSessionDriver.ConfirmsReadConfinement(thread, "/wt/dante/E7"));
    }

    [Fact]
    public async Task ClaudeRecusaARevisaoConfinadaAntesDeQualquerProcesso()
    {
        var launcher = new ProbeLauncher();
        await using var driver = new ClaudeSessionDriver(launcher);
        await Assert.ThrowsAsync<AgentProtocolException>(() => driver.StartAsync(new AgentSessionStartOptions(
            AppContext.BaseDirectory, Profile: AgentPermissionProfile.Plan, LeituraConfinada: true)));
        Assert.Empty(launcher.Requests);
    }

    // Resumo da worktree com Git real: o hash cobre o diff inteiro e os arquivos novos, muda com qualquer alteração,
    // não depende do corte de exibição, e o checkout principal não é tocado.
    [Fact]
    public async Task ResumoDasAlteracoesTemHashCanonicoDoDiffEDosArquivosNovos()
    {
        var repo = temp.Criar("dante");
        var impressao = RepositorioGitDeTeste.Impressao(repo.Caminho);
        var gerenciador = new GerenciadorDeWorktreesGit(new OpcoesDeWorktrees { Raiz = temp.RaizDasWorktrees },
            new CatalogoFixo(repo));
        var w = await gerenciador.PlanejarAsync(repo, 7, "dev");
        await gerenciador.CriarOuAdotarAsync(w);
        var vazia = await gerenciador.ResumirAlteracoesAsync(w, 1000);
        Assert.True(vazia.Vazias);
        File.AppendAllText(Path.Combine(w.Caminho, "README.md"), "linha nova\n");
        File.WriteAllText(Path.Combine(w.Caminho, "novo.txt"), "conteúdo\n");
        var resumo = await gerenciador.ResumirAlteracoesAsync(w, 1000);
        Assert.Contains("+linha nova", resumo.Diff);
        Assert.Equal(["novo.txt"], resumo.NaoRastreados);
        Assert.False(resumo.Cortado);
        Assert.NotEqual(vazia.Hash, resumo.Hash);
        // Mesmo conteúdo, mesmo hash, com ou sem corte de exibição.
        var cortado = await gerenciador.ResumirAlteracoesAsync(w, 10);
        Assert.Equal((resumo.Hash, true, 10), (cortado.Hash, cortado.Cortado, cortado.Diff.Length));
        // Só o conteúdo do arquivo novo muda: outro hash.
        File.WriteAllText(Path.Combine(w.Caminho, "novo.txt"), "outro\n");
        Assert.NotEqual(resumo.Hash, (await gerenciador.ResumirAlteracoesAsync(w, 1000)).Hash);
        Assert.Equal(impressao, RepositorioGitDeTeste.Impressao(repo.Caminho));
        // Conjunto fechado: só estas leituras novas.
        Assert.True(GerenciadorDeWorktreesGit.Permitido(["diff", "--no-ext-diff", "--no-textconv", "--no-color", "--binary", "abc"]));
        Assert.True(GerenciadorDeWorktreesGit.Permitido(["ls-files", "--others", "--exclude-standard", "-z"]));
        Assert.False(GerenciadorDeWorktreesGit.Permitido(["diff", "--ext-diff", "abc"]));
        Assert.False(GerenciadorDeWorktreesGit.Permitido(["add", "-A"]));
        Assert.False(GerenciadorDeWorktreesGit.Permitido(["commit", "-m", "x"]));
    }

    private static AgenteDaSessao Agente(bool leitura, EscopoDaRevisaoDto? escopo, AgentKind driver = AgentKind.Codex,
        bool brain = false) =>
        new("codex-rev", new VersaoDoAgenteDto
        {
            Id = Guid.NewGuid(), Numero = 1,
            Configuracao = new ConfiguracaoDoAgente(driver, AgentPermissionProfile.Plan, AgentPermissionProfile.Plan, false,
                capacidades: [CapacidadeDoAgente.Revisar], repositorios: ["*"],
                ferramentas: brain ? [FerramentaDoDante.Brain] : []),
            HashDaConfiguracao = Hash
        })
        {
            TetoDaExecucao = AgentPermissionProfile.Plan, LeituraConfinada = leitura, EscopoDaRevisao = escopo
        };

    // Texto da resposta MCP (content[0].text).
    private static string Texto(string? resposta) => (string)JsonNode.Parse(resposta!)!["content"]![0]!["text"]!;

    private static async Task<NamedPipeClientStream> ConectarAsync(string nome)
    {
        var cliente = new NamedPipeClientStream(".", nome, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await cliente.ConnectAsync(10_000);
        return cliente;
    }

    private static async Task Escrever(Stream pipe, object mensagem)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(mensagem) + "\n");
        await pipe.WriteAsync(bytes);
        await pipe.FlushAsync();
    }

    private static async Task<string?> LerAsync(Stream pipe)
    {
        var linha = new StringBuilder();
        var buffer = new byte[1];
        using var prazo = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (await pipe.ReadAsync(buffer, prazo.Token) == 1)
        {
            if (buffer[0] == '\n') return linha.ToString();
            linha.Append((char)buffer[0]);
        }
        return linha.Length == 0 ? null : linha.ToString();
    }

    // O mesmo lançador do CodexSessionDriverTests: o processo falso do Codex (Dante.ProcessProbe fake-codex).
    private sealed class ProbeLauncher(params string[] probeArguments) : IInteractiveAgentProcessLauncher
    {
        private static readonly string ProbeAssembly = typeof(ProbeMarker).Assembly.Location;
        private static readonly string RuntimeConfig = Path.Combine(AppContext.BaseDirectory, "Dante.Tests.runtimeconfig.json");
        private readonly InteractiveAgentProcessLauncher inner = new(new DotnetResolver(),
            new OpcoesContencaoAgentes { Modo = ModoContencaoAgentes.Compatibilidade });

        public List<AgentProcessRequest> Requests { get; } = [];

        public async Task<InteractiveAgentProcess> StartAsync(AgentProcessRequest request,
            Func<string, string>? redactOutput = null, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return await inner.StartAsync(request with
            {
                Arguments = ["exec", "--runtimeconfig", RuntimeConfig, ProbeAssembly, "fake-codex", .. probeArguments,
                    .. request.Arguments]
            }, redactOutput, cancellationToken);
        }
    }

    private sealed class DotnetResolver : IAgentExecutableResolver
    {
        public string? Resolve(AgentKind agent) =>
            (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"))
            .FirstOrDefault(File.Exists) ?? throw new FileNotFoundException("dotnet host must be on PATH to run process tests.");
    }

    private sealed class ObservadorNulo : IAgentSessionEventSink
    {
        public Task PublishAsync(AgentSessionSnapshot session, AgentEvent agentEvent, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    // Brain e coordenacao oferecidos a toda sessão: quem filtra é o SessionRegistry.
    private sealed class ServidoresFixos : IAgentToolServers
    {
        public Task<IReadOnlyList<AgentToolServer>> ForAsync(long ownerUserId, AgentKind agent, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AgentToolServer>>([]);

        public Task<IReadOnlyList<AgentToolServer>> ForSessionAsync(string sessionId, SessionStartRequest request,
            CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AgentToolServer>>(
        [
            new("dante_brain", "dotnet", ["x"], new Dictionary<string, string>(), []),
            new(ServidorMcpDeCoordenacao.Nome, "dotnet", ["y"], new Dictionary<string, string>(), [])
        ]);

        public void BeginTurn(string sessionId, AgentInput input) { }
        public void EndSession(string sessionId) { }
    }
}

    // Caso de uso falso: registra o que recebeu; nada mais é chamado pelo canal.
internal sealed class RoteirosFalso : IRoteirosAppService
{
    public List<(EscopoDaRevisaoDto Escopo, VereditoInformadoDto? Veredito, string? Sessao)> Chamadas { get; } = [];
    public string? Recusa { get; set; }

    public Task<RegistroDeVereditoDto> RegistrarVereditoAsync(EscopoDaRevisaoDto escopo, VereditoInformadoDto veredito,
        string? sessao, CancellationToken cancellationToken = default)
    {
        if (Recusa is not null) throw new InvalidOperationException(Recusa);
        Chamadas.Add((escopo, veredito, sessao));
        return Task.FromResult(new RegistroDeVereditoDto(veredito.Veredito, false));
    }

    public Task<EstadoDaRevisaoDto> ObterEstadoDaRevisaoAsync(EscopoDaRevisaoDto escopo, CancellationToken cancellationToken = default)
    {
        Chamadas.Add((escopo, null, null));
        return Task.FromResult(new EstadoDaRevisaoDto(escopo.Codigo, "eng", escopo.Rodada, 2, escopo.HashDoArtefato, [], null));
    }

    public Task<RoteiroCriadoDto> EnfileirarAsync(NovoRoteiroDto novo, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<RoteiroDaExecucaoDto?> ObterAsync(Guid idExecucao, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<RoteiroDaExecucaoDto?> ObterDoProprietarioAsync(ProprietarioDaExecucao proprietario, string codigo, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<string?> ConferirIntegridadeAsync(Guid idExecucao, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<AvancoDoRoteiroDto> ConcluirPassoAsync(PosseDoTrabalho posse, Guid idEtapa, ConclusaoDoPassoDto conclusao, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task SolicitarObjetivoAsync(PosseDoTrabalho posse, Guid idEtapa, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<ResultadoDaDecisaoDto> DecidirAsync(ProprietarioDaExecucao proprietario, string codigo, DecisaoHumanaDto decisao, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<VersaoDoAgenteDto> ObterVersaoDoParticipanteAsync(Guid idExecucao, string papel, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<VereditoRegistrado?> ObterVereditoAsync(Guid idEtapa, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<bool> SubstituirObjetivoAsync(ProprietarioDaExecucao proprietario, string codigo, string objetivo, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<ResultadoDaDecisaoDto> RevisarNovamenteAsync(ProprietarioDaExecucao proprietario, string codigo, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
