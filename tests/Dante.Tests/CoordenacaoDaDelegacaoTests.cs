using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dante.Application.Agentes;
using Dante.Application.Contextos;
using Dante.Application.Execucoes;
using Dante.Domain.Agentes;
using Dante.Domain.Execucoes;
using Dante.Infrastructure.Agentes;
using Dante.ProcessProbe;
using Dante.Worker.Coordenacao;
using Dante.Worker.Execucoes;
using Dante.Worker.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dante.Tests;

// #309 (AD-70), PR 3: as delegações pelo dante_coordenacao existente (mesmo servidor, pipe, token e limite da #308).
// O catálogo é por perfil (origem delega e consulta; subordinada entrega o resultado), o perfil é do canal fixado pelo
// orquestrador, a identidade de quem pede vem da posse e da sessão vinculada, nunca dos argumentos, e o resultado só vale
// pela ferramenta. Sem banco: os casos de uso são falsos e registram o que receberam (o fluxo real está em
// DelegacaoPelaFilaEndToEndTests e DelegacoesExecucaoPostgreSqlTests).
public sealed class CoordenacaoDaDelegacaoTests : IDisposable
{
    private static readonly string Hash = new('d', 64);
    private const string Resultado = """{"resposta":"Sim.","confianca":"alta","evidencias":[{"tipo":"arquivo","referencia":"README.md:1"}]}""";
    private readonly string repositorio = Path.Combine(Path.GetTempPath(), "dante-coord-" + Guid.NewGuid().ToString("N"));

    public CoordenacaoDaDelegacaoTests()
    {
        Directory.CreateDirectory(repositorio);
        File.WriteAllText(Path.Combine(repositorio, "README.md"), "# dante\n");
    }

    public void Dispose() => Directory.Delete(repositorio, true);

    [Fact]
    public async Task CadaPerfilAnunciaSoAsSuasFerramentasEAFormaEValidadaPeloPerfil()
    {
        Assert.Equal(["consultar_agente", "estado_da_execucao"], Nomes(PerfilDeCoordenacao.Origem));
        Assert.Equal(["entregar_resultado"], Nomes(PerfilDeCoordenacao.Delegacao));
        Assert.Equal(["estado_da_execucao", "entregar_resultado"], Nomes(PerfilDeCoordenacao.Revisao));
        // Só leitura roda sem aprovação; as de mundo fechado da #309 são pré-autorizadas; a revisão não muda (#308).
        Assert.Equal(["estado_da_execucao"], ServidorMcpDeCoordenacao.DeLeitura(PerfilDeCoordenacao.Origem));
        Assert.Equal(["consultar_agente"], ServidorMcpDeCoordenacao.PreAutorizadas(PerfilDeCoordenacao.Origem));
        Assert.Equal(["entregar_resultado"], ServidorMcpDeCoordenacao.PreAutorizadas(PerfilDeCoordenacao.Delegacao));
        Assert.Empty(ServidorMcpDeCoordenacao.PreAutorizadas(PerfilDeCoordenacao.Revisao));
        // Nenhum campo de identidade, dono, repositório, permissão ou escrita no schema da origem.
        var solicitar = ServidorMcpDeCoordenacao.Lista(PerfilDeCoordenacao.Origem)["tools"]![0]!["inputSchema"]!;
        Assert.Equal(["destinatario", "contrato", "objetivo", "entrada", "criterios"],
            solicitar["properties"]!.AsObject().Select(p => p.Key));
        Assert.False((bool)solicitar["additionalProperties"]!);
        Assert.Equal(["consulta.v1"], solicitar["properties"]!["contrato"]!["enum"]!.AsArray().Select(v => (string?)v));

        // O processo MCP valida pelo perfil: a ferramenta de outro perfil, um campo de identidade e a falta de obrigatório
        // nunca chegam ao Worker.
        var encaminhadas = new List<string>();
        var entrada = string.Join('\n',
            """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"consultar_agente","arguments":{"destinatario":"revisor","contrato":"consulta.v1","objetivo":"o","entrada":{"pergunta":"?"}}}}""",
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"entregar_resultado","arguments":{"veredito":"aprovado","resumo":"x"}}}""",
            """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"consultar_agente","arguments":{"destinatario":"revisor","contrato":"consulta.v1","objetivo":"o","entrada":{},"execucao":"E1"}}}""",
            """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"consultar_agente","arguments":{"destinatario":"revisor"}}}""") + "\n";
        var saida = new StringWriter();
        await ServidorMcpDeCoordenacao.ExecutarAsync(new StringReader(entrada), saida, (nome, _, _) =>
        {
            encaminhadas.Add(nome);
            return Task.FromResult(ServidorMcpDeCoordenacao.Resultado(new { nova = true }));
        }, PerfilDeCoordenacao.Origem);
        var respostas = saida.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!).ToArray();
        Assert.Equal(["consultar_agente", "estado_da_execucao"],
            respostas[0]["result"]!["tools"]!.AsArray().Select(f => (string?)f!["name"]));
        Assert.Equal(["consultar_agente"], encaminhadas);
        Assert.All(respostas[2..], r => Assert.True((bool)r["result"]!["isError"]!));
    }

    [Fact]
    public async Task SoAOrigemComConcessaoEASubordinadaEmPlanRecebemOServidorNoPerfilCerto()
    {
        var (provider, _, _) = Servicos();
        await using var _p = provider;
        using var ferramentas = new FerramentasDeCoordenacao(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<FerramentasDeCoordenacao>.Instance);
        var drivers = new FakeSessionDriverFactory();
        await using var sessoes = new SessionRegistry(drivers, NullLogger<SessionRegistry>.Instance,
            toolServers: new CompositorDeFerramentas([ferramentas]));
        var controle = Controle(provider);
        var contexto = JobExecutionContext.Repository("@dante", repositorio);

        async Task<IReadOnlyList<AgentToolServer>> Servidores(AgenteDaSessao agente, AgentPermissionProfile perfil = AgentPermissionProfile.Plan)
        {
            var iniciada = await sessoes.StartAsync(new SessionStartRequest(1, AgentKind.Codex, contexto, Profile: perfil,
                RegisteredAgent: agente, Observer: new Observador()));
            Assert.True(iniciada.Accepted, iniciada.Error);
            return drivers.Created[^1].StartOptions!.ToolServers ?? [];
        }

        // Origem com coordenacao e alguém a consultar: perfil origem, o pipe por último, solicitar pré-autorizada.
        var origem = Assert.Single(await Servidores(Agente(coordenacao: true, consulta: ["revisor"]) with
        {
            Coordenacao = new CoordenacaoDaOrigem(controle)
        }));
        Assert.Equal(ServidorMcpDeCoordenacao.Nome, origem.Name);
        Assert.Equal(["--mcp-coordenacao", "--perfil", "origem", "--pipe"], origem.Arguments.SkipLast(1).TakeLast(4));
        Assert.StartsWith(ServidorMcpDeCoordenacao.PrefixoDoPipe, origem.Arguments[^1]);
        Assert.Equal(["estado_da_execucao"], origem.ReadOnlyTools);
        Assert.Equal(["consultar_agente"], origem.PreAuthorizedTools);
        // Subordinada: perfil delegacao (a concessão coordenacao é de quem delega, não de quem responde).
        var entrega = new EntregaPelaFerramenta(provider.GetRequiredService<IServiceScopeFactory>(), controle, Guid.NewGuid(),
            "consulta.v1", contexto);
        var subordinada = Assert.Single(await Servidores(Agente(coordenacao: false, consulta: []) with
        {
            Coordenacao = new CoordenacaoDaDelegacao(entrega)
        }));
        Assert.Equal(["--perfil", "delegacao"], subordinada.Arguments.SkipLast(2).TakeLast(2));
        Assert.Equal(["entregar_resultado"], subordinada.PreAuthorizedTools);
        // Nada: escopo sem a concessão, sem ninguém a consultar, fora de plan, sem escopo, e a sessão comum com a concessão.
        Assert.Empty(await Servidores(Agente(coordenacao: false, consulta: []) with { Coordenacao = new CoordenacaoDaOrigem(controle) }));
        Assert.Empty(await Servidores(Agente(coordenacao: true, consulta: []) with { Coordenacao = new CoordenacaoDaOrigem(controle) }));
        Assert.Empty(await Servidores(Agente(coordenacao: true, consulta: ["revisor"], maximo: AgentPermissionProfile.Manual) with
        {
            Coordenacao = new CoordenacaoDaOrigem(controle), TetoDaExecucao = null
        }, AgentPermissionProfile.Manual));
        Assert.Empty(await Servidores(Agente(coordenacao: true, consulta: ["revisor"])));
        // Sem observador (sessão que ninguém conduz), nenhum canal.
        Assert.Empty(await ferramentas.ForSessionAsync("S9", new SessionStartRequest(1, AgentKind.Codex, contexto,
            Profile: AgentPermissionProfile.Plan, RegisteredAgent: Agente(coordenacao: true, consulta: ["revisor"]) with
            {
                Coordenacao = new CoordenacaoDaOrigem(controle)
            })));
    }

    [Fact]
    public async Task OrigemDelegaSobAPosseEPelaSessaoVinculadaComOEscopoDoOrquestrador()
    {
        var (provider, delegacoes, _) = Servicos();
        await using var _p = provider;
        using var ferramentas = new FerramentasDeCoordenacao(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<FerramentasDeCoordenacao>.Instance);
        var controle = Controle(provider);
        var servidor = Assert.Single(await ferramentas.ForSessionAsync("S1", new SessionStartRequest(1, AgentKind.Codex,
            JobExecutionContext.Repository("@dante", repositorio), Profile: AgentPermissionProfile.Plan,
            RegisteredAgent: Agente(coordenacao: true, consulta: ["revisor"]) with { Coordenacao = new CoordenacaoDaOrigem(controle) },
            Observer: new Observador())));
        await using var cliente = await ConectarAsync(servidor);
        object Pedido(object? extra = null) => new
        {
            nome = "consultar_agente",
            argumentos = extra ?? new { destinatario = "revisor", contrato = "consulta.v1", objetivo = "Conferir o índice",
                entrada = new { pergunta = "ix_estado cobre?" }, criterios = "Citar o arquivo." }
        };

        // Antes do vínculo da sessão com a etapa, nada é pedido.
        Assert.Contains("não está autorizada", await ChamarAsync(cliente, Pedido()));
        Assert.DoesNotContain(delegacoes.Chamadas, c => c.Metodo == nameof(IDelegacoesAppService.SolicitarSobPosseAsync));
        await controle.VincularSessaoAsync(42, CancellationToken.None);

        // Identidade nunca vem do agente; ferramenta de outro perfil não existe aqui.
        Assert.Contains("Argumento não permitido", await ChamarAsync(cliente, Pedido(new
        {
            destinatario = "revisor", contrato = "consulta.v1", objetivo = "o", entrada = new { pergunta = "?" }, etapa = "x"
        })));
        Assert.Contains("Ferramenta desconhecida", await ChamarAsync(cliente,
            new { nome = "entregar_resultado", argumentos = new { resultado = new { } } }));
        Assert.Contains("entrada é um objeto", await ChamarAsync(cliente, Pedido(new
        {
            destinatario = "revisor", contrato = "consulta.v1", objetivo = "o", entrada = "texto"
        })));

        var resposta = JsonNode.Parse(await ChamarAsync(cliente, Pedido()))!;
        Assert.Equal(("E9", true), ((string?)resposta["delegacao"], (bool)resposta["nova"]!));
        var chamada = delegacoes.Chamadas.Single(c => c.Metodo == nameof(IDelegacoesAppService.SolicitarSobPosseAsync));
        Assert.Equal(Trabalho.Posse, chamada.Argumentos[0]);
        Assert.Equal(42L, chamada.Argumentos[1]);
        var pedido = (SolicitacaoDeDelegacaoDto)chamada.Argumentos[2]!;
        Assert.Equal((Alvo.IdExecucao, Alvo.IdEtapa, "telegram", 1001L, false),
            (pedido.IdExecucaoOrigem, pedido.IdEtapaOrigem, pedido.Proprietario.Canal, pedido.Proprietario.IdDoUsuario, pedido.Escrita));
        Assert.Equal(("revisor", "consulta.v1", "Conferir o índice", "Citar o arquivo."),
            (pedido.Destinatario, pedido.Contrato, pedido.Objetivo, pedido.Criterios));
        Assert.Equal("""{"pergunta":"ix_estado cobre?"}""", pedido.Entrada);

        // Recusa do domínio volta como mensagem ao agente.
        delegacoes.Recusa = new DelegacaoRecusadaException(MotivoDaRecusaDeDelegacao.DestinatarioNaoAutorizado,
            "O agente estranho não está entre os que você pode consultar.");
        Assert.Contains("não está entre os que você pode consultar", await ChamarAsync(cliente, Pedido()));

        // Consulta: pelo dono e pela execução do escopo, com progresso e o resultado estruturado.
        var consulta = JsonNode.Parse(await ChamarAsync(cliente, new { nome = "estado_da_execucao", argumentos = new { } }))!;
        var item = consulta["delegacoes"]![0]!;
        Assert.Equal(("E9", "Respondida", "TurnoEnviado", "alta"),
            ((string?)item["delegacao"], (string?)item["estado"], (string?)item["progresso"], (string?)item["resultado"]!["confianca"]));
        var acompanhar = delegacoes.Chamadas.Single(c => c.Metodo == nameof(IDelegacoesAppService.AcompanharDaOrigemAsync));
        Assert.Equal((new ProprietarioDaExecucao("telegram", 1001), Alvo.IdExecucao), (acompanhar.Argumentos[0], acompanhar.Argumentos[1]));

        // Etapa concluída: a posse acabou e nada mais é pedido por esta sessão.
        await controle.ConcluirAsync(new ResultadoDaEtapaDto(EstadoDaEtapa.Concluida), CancellationToken.None);
        Assert.Contains("não está autorizada", await ChamarAsync(cliente, Pedido()));
        Assert.Equal(2, delegacoes.Chamadas.Count(c => c.Metodo == nameof(IDelegacoesAppService.SolicitarSobPosseAsync)));
    }

    [Fact]
    public async Task SubordinadaEntregaOResultadoPelaFerramentaUmaVezEOExecutorNaoSobrescreve()
    {
        var (provider, delegacoes, _) = Servicos();
        await using var _p = provider;
        var escopos = provider.GetRequiredService<IServiceScopeFactory>();
        using var ferramentas = new FerramentasDeCoordenacao(escopos, NullLogger<FerramentasDeCoordenacao>.Instance);
        var controle = Controle(provider);
        var entrega = new EntregaPelaFerramenta(escopos, controle, Alvo.IdExecucao, "consulta.v1",
            JobExecutionContext.Repository("@dante", repositorio));
        var servidor = Assert.Single(await ferramentas.ForSessionAsync("S2", new SessionStartRequest(1, AgentKind.Codex,
            JobExecutionContext.Repository("@dante", repositorio), Profile: AgentPermissionProfile.Plan,
            RegisteredAgent: Agente(coordenacao: false, consulta: []) with { Coordenacao = new CoordenacaoDaDelegacao(entrega) },
            Observer: new Observador())));
        await using var cliente = await ConectarAsync(servidor);
        object Entregar(string json) => new { nome = "entregar_resultado", argumentos = new { resultado = JsonNode.Parse(json) } };

        // Inválido pelo servidor (contrato) ou evidência que não existe no repositório: nada gravado, o agente corrige.
        delegacoes.Invalido = "saída: campo extra não é permitido.";
        Assert.Contains("Resultado não aceito: saída: campo extra", await ChamarAsync(cliente, Entregar(Resultado)));
        delegacoes.Invalido = null;
        Assert.Contains("a evidência src/x.cs não existe", await ChamarAsync(cliente,
            Entregar(Resultado.Replace("README.md:1", "src/x.cs:3"))));
        // Antes do vínculo, nenhuma sessão é a autorizada.
        Assert.Contains("não está vinculada", await ChamarAsync(cliente, Entregar(Resultado)));
        Assert.DoesNotContain(delegacoes.Chamadas, c => c.Metodo == nameof(IDelegacoesAppService.ConcluirAsync));

        await controle.VincularSessaoAsync(42, CancellationToken.None);
        Assert.True((bool)JsonNode.Parse(await ChamarAsync(cliente, Entregar(Resultado)))!["registrado"]!);
        var concluir = delegacoes.Chamadas.Single(c => c.Metodo == nameof(IDelegacoesAppService.ConcluirAsync));
        Assert.Equal(Trabalho.Posse, concluir.Argumentos[0]);
        Assert.Equal(new EntregaDaDelegacaoDto(Resultado, 42), concluir.Argumentos[2]);
        Assert.Contains(delegacoes.Chamadas, c => c.Metodo == nameof(IDelegacoesAppService.RegistrarProgressoAsync) &&
            (MarcoDaDelegacao)c.Argumentos[2]! == MarcoDaDelegacao.ResultadoRecebido);
        Assert.True(controle.Concluida);

        // O mesmo conteúdo (outra ordem de chaves) é idempotente; outro conteúdo é recusado; nada é gravado de novo.
        var reordenado = """{"confianca":"alta","evidencias":[{"tipo":"arquivo","referencia":"README.md:1"}],"resposta":"Sim."}""";
        var repetida = JsonNode.Parse(await ChamarAsync(cliente, Entregar(reordenado)))!;
        Assert.Equal((true, true), ((bool)repetida["registrado"]!, (bool)repetida["ja_registrado"]!));
        Assert.Contains("já terminou", await ChamarAsync(cliente, Entregar(Resultado.Replace("Sim.", "Não."))));
        Assert.Single(delegacoes.Chamadas, c => c.Metodo == nameof(IDelegacoesAppService.ConcluirAsync));
        // O executor (prazo, falha) não sobrescreve o que a ferramenta gravou.
        Assert.False(await entrega.EncerrarAsync("Duração máxima excedida.", expirada: true));
        Assert.DoesNotContain(delegacoes.Chamadas, c => c.Metodo is nameof(IDelegacoesAppService.ExpirarAsync)
            or nameof(IDelegacoesAppService.FalharAsync));
    }

    [Fact]
    public async Task EncerramentoPeloExecutorAntesDaEntregaFazAEntregaSerRecusada()
    {
        var (provider, delegacoes, _) = Servicos();
        await using var _p = provider;
        var controle = Controle(provider);
        await controle.VincularSessaoAsync(42, CancellationToken.None);
        var entrega = new EntregaPelaFerramenta(provider.GetRequiredService<IServiceScopeFactory>(), controle, Alvo.IdExecucao,
            "consulta.v1", JobExecutionContext.Repository("@dante", repositorio));
        Assert.True(await entrega.EncerrarAsync("Duração máxima excedida.", expirada: true));
        var erro = await Assert.ThrowsAsync<InvalidOperationException>(() => entrega.EntregarAsync(Resultado, CancellationToken.None));
        Assert.Contains("já terminou", erro.Message);
        Assert.Single(delegacoes.Chamadas, c => c.Metodo == nameof(IDelegacoesAppService.ExpirarAsync));
        Assert.DoesNotContain(delegacoes.Chamadas, c => c.Metodo == nameof(IDelegacoesAppService.ConcluirAsync));
        Assert.Null(entrega.Desfecho);
    }

    // Codex em plan: só a ferramenta de coordenação pré-autorizada desta sessão, na thread dela, é aceita sem chegar a
    // ninguém; outra ferramenta, outro servidor e outra thread continuam recusados, e sem pré-autorização nada muda.
    [Theory]
    [InlineData("mcp-veredito", "accept")]
    [InlineData("mcp-outra", "decline")]
    [InlineData("mcp-outro-servidor", "decline")]
    [InlineData("mcp-outra-thread", "decline")]
    [InlineData("mcp", "decline")]
    public async Task CodexEmPlanAceitaSoAFerramentaPreAutorizadaDaSessao(string cenario, string acao)
    {
        var servidor = new AgentToolServer(ServidorMcpDeCoordenacao.Nome, "dotnet",
            ["dante.dll", "--mcp-coordenacao", "--perfil", "delegacao", "--pipe", "p"],
            new Dictionary<string, string> { ["DANTE_COORDENACAO_TOKEN"] = "t" }, [], ["entregar_resultado"]);
        await using var driver = new CodexSessionDriver(new ProbeLauncher());
        await driver.StartAsync(new AgentSessionStartOptions(AppContext.BaseDirectory, Profile: AgentPermissionProfile.Plan,
            ToolServers: [servidor]));
        await using var eventos = driver.ReadEventsAsync().GetAsyncEnumerator();
        await driver.StartTurnAsync(cenario);
        var turno = new List<AgentEvent>();
        using var prazo = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            Assert.True(await eventos.MoveNextAsync().AsTask().WaitAsync(prazo.Token));
            turno.Add(eventos.Current);
            if (eventos.Current is TurnCompletedEvent) break;
        }
        Assert.Empty(turno.OfType<ApprovalRequestedEvent>());
        Assert.Equal($"mcp:{acao}", turno.OfType<MessageCompletedEvent>().Single().Text);
    }

    [Fact]
    public void ClaudePreAutorizaSoAsFerramentasDeclaradasDoServidor()
    {
        var argumentos = ClaudeSessionDriver.BuildArguments("s", new AgentSessionStartOptions(AppContext.BaseDirectory,
            Profile: AgentPermissionProfile.Plan, ToolServers:
            [
                new AgentToolServer(ServidorMcpDeCoordenacao.Nome, "/usr/bin/dante",
                    ["--mcp-coordenacao", "--perfil", "origem", "--pipe", "p"], new Dictionary<string, string>(),
                    ["estado_da_execucao"], ["consultar_agente"])
            ]));
        Assert.Equal("mcp__dante_coordenacao__estado_da_execucao,mcp__dante_coordenacao__consultar_agente",
            argumentos[argumentos.IndexOf("--allowedTools") + 1]);
    }

    [Fact]
    public void SessaoDaTarefaDeLeituraSoRecebeOEscopoDaOrigemComAConcessao()
    {
        var (provider, _, _) = Servicos();
        using var _p = provider;
        var controle = Controle(provider);
        PlanoDaTarefa Plano(bool coordenacao, string[] consulta) => MontagemDaTarefa.Montar("pesquisador",
            Agente(coordenacao, consulta, AgentPermissionProfile.Manual).Versao, JobExecutionContext.Repository("@dante", repositorio),
            "Investigar o índice");

        var comConcessao = SolicitacaoDaTarefa.Para(Plano(true, ["revisor", "outro"]), 1001, controle: controle).RegisteredAgent!;
        Assert.Same(controle, Assert.IsType<CoordenacaoDaOrigem>(comConcessao.Coordenacao).Controle);
        Assert.Contains("consultar_agente) a: outro, revisor.", comConcessao.InstrucoesDaExecucao);
        Assert.Null(SolicitacaoDaTarefa.Para(Plano(false, []), 1001, controle: controle).RegisteredAgent!.Coordenacao);
        Assert.Null(SolicitacaoDaTarefa.Para(Plano(true, ["revisor"]), 1001).RegisteredAgent!.Coordenacao);
    }

    // ---- apoio ----

    private static readonly AlvoDoTrabalhoDto Alvo = new(Guid.NewGuid(), 7, "E7", TipoDeExecucao.Tarefa,
        EstadoDaExecucao.EmExecucao, "telegram", 1001, Guid.NewGuid(), 1, EstadoDaEtapa.EmExecucao, false,
        PoliticaDeRetomada.Automatica, 1);

    private static readonly TrabalhoReivindicado Trabalho = new(Guid.NewGuid(), TipoDeTrabalho.AvancarExecucao, Alvo.IdExecucao,
        Alvo.IdEtapa, null, "tarefa:x", 1, 3, 4, DateTimeOffset.UtcNow.AddMinutes(5), "teste", Guid.NewGuid());

    private static ExecucaoSobPosse Controle(IServiceProvider provider) =>
        new(provider.GetRequiredService<IServiceScopeFactory>(), Trabalho, Alvo, CancellationToken.None, () => Task.CompletedTask);

    private static (ServiceProvider Provider, DelegacoesFalsas Delegacoes, Falso<IFilaDeExecucoesAppService> Fila) Servicos()
    {
        var delegacoes = new DelegacoesFalsas();
        var fila = Falso<IFilaDeExecucoesAppService>.Criar((metodo, _) => metodo.Name == nameof(IFilaDeExecucoesAppService.ConcluirEtapaAsync)
            ? Task.FromResult(EstadoDaEtapa.Concluida) : Task.CompletedTask);
        var provider = new ServiceCollection()
            .AddSingleton(delegacoes.Servico)
            .AddSingleton(fila.Servico)
            // Toda chamada passa pelo gateway da Application (#311, PR 2); sem ele, o canal recusa (fail-closed).
            .AddSingleton<IGatewayDeFerramentas, GatewayDeFerramentas>()
            .BuildServiceProvider();
        return (provider, delegacoes, fila);
    }

    private static AgenteDaSessao Agente(bool coordenacao, string[] consulta,
        AgentPermissionProfile maximo = AgentPermissionProfile.Plan) =>
        new("pesquisador", new VersaoDoAgenteDto
        {
            Id = Guid.NewGuid(), Numero = 1,
            Configuracao = new ConfiguracaoDoAgente(AgentKind.Codex, AgentPermissionProfile.Plan, maximo,
                maximo != AgentPermissionProfile.Plan,
                capacidades: [CapacidadeDoAgente.Pesquisar], repositorios: ["*"],
                ferramentas: coordenacao ? [FerramentaDoDante.Coordenacao] : [], podeConsultar: consulta),
            HashDaConfiguracao = Hash
        })
        {
            TetoDaExecucao = AgentPermissionProfile.Plan
        };

    private static string[] Nomes(PerfilDeCoordenacao perfil) =>
        ServidorMcpDeCoordenacao.Lista(perfil)["tools"]!.AsArray().Select(f => (string)f!["name"]!).ToArray();

    // O que o processo MCP faria: conecta uma vez no pipe da sessão, apresenta o token e chama pela mesma conexão.
    private static async Task<NamedPipeClientStream> ConectarAsync(AgentToolServer servidor)
    {
        var cliente = new NamedPipeClientStream(".", servidor.Arguments[^1], PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await cliente.ConnectAsync(10_000);
        await EscreverAsync(cliente, new { token = servidor.Environment[ServidorMcpDeCoordenacao.VariavelDoToken] });
        return cliente;
    }

    private static async Task<string> ChamarAsync(Stream pipe, object chamada)
    {
        await EscreverAsync(pipe, chamada);
        var bytes = new List<byte>();
        var buffer = new byte[1];
        using var prazo = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (await pipe.ReadAsync(buffer, prazo.Token) == 1 && buffer[0] != '\n') bytes.Add(buffer[0]);
        return (string)JsonNode.Parse(Encoding.UTF8.GetString(bytes.ToArray()))!["content"]![0]!["text"]!;
    }

    private static async Task EscreverAsync(Stream pipe, object mensagem)
    {
        await pipe.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(mensagem) + "\n"));
        await pipe.FlushAsync();
    }

    private sealed class ProbeLauncher : IInteractiveAgentProcessLauncher
    {
        private static readonly string ProbeAssembly = typeof(ProbeMarker).Assembly.Location;
        private static readonly string RuntimeConfig = Path.Combine(AppContext.BaseDirectory, "Dante.Tests.runtimeconfig.json");
        private readonly InteractiveAgentProcessLauncher inner = new(new DotnetResolver(),
            new Dante.Infrastructure.Agentes.OpcoesContencaoAgentes { Modo = Dante.Infrastructure.Agentes.ModoContencaoAgentes.Compatibilidade });

        public Task<InteractiveAgentProcess> StartAsync(AgentProcessRequest request, Func<string, string>? redactOutput = null,
            CancellationToken cancellationToken = default) =>
            inner.StartAsync(request with
            {
                Arguments = ["exec", "--runtimeconfig", RuntimeConfig, ProbeAssembly, "fake-codex", .. request.Arguments]
            }, redactOutput, cancellationToken);
    }

    private sealed class DotnetResolver : IAgentExecutableResolver
    {
        public string? Resolve(AgentKind agent) =>
            (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"))
            .FirstOrDefault(File.Exists) ?? throw new FileNotFoundException("dotnet host must be on PATH to run process tests.");
    }

    private sealed class Observador : IAgentSessionEventSink
    {
        public Task PublishAsync(AgentSessionSnapshot session, AgentEvent agentEvent, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    // Casos de uso falsos: registram cada chamada (e se a etapa já estava concluída) e respondem o mínimo.
    private sealed class DelegacoesFalsas
    {
        private readonly Falso<IDelegacoesAppService> falso;

        public DelegacoesFalsas() => falso = Falso<IDelegacoesAppService>.Criar(Responder);

        public IDelegacoesAppService Servico => falso.Servico;
        public string? Invalido { get; set; }
        public Exception? Recusa { get; set; }
        public List<(string Metodo, object?[] Argumentos)> Chamadas => falso.Chamadas;

        private static readonly DelegacaoDto Dto = new(Guid.NewGuid(), Alvo.IdExecucao, Alvo.IdExecucao, Alvo.IdEtapa,
            Guid.NewGuid(), "E9", "revisor", Guid.NewGuid(), "consulta.v1", EstadoDaDelegacao.Aceita, null, null, null, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null);

        private object? Responder(MethodInfo metodo, object?[] argumentos) => metodo.Name switch
        {
            nameof(IDelegacoesAppService.SolicitarSobPosseAsync) when Recusa is not null => Task.FromException<DelegacaoSolicitadaDto>(Recusa),
            nameof(IDelegacoesAppService.SolicitarSobPosseAsync) => Task.FromResult(new DelegacaoSolicitadaDto(Dto, true)),
            nameof(IDelegacoesAppService.AcompanharDaOrigemAsync) => Task.FromResult<IReadOnlyList<AcompanhamentoDaDelegacaoDto>>(
                [new(Dto with { Estado = EstadoDaDelegacao.Respondida, Resultado = Resultado }, MarcoDaDelegacao.TurnoEnviado)]),
            nameof(IDelegacoesAppService.ValidarResultadoAsync) => Task.FromResult(Invalido),
            nameof(IDelegacoesAppService.ConcluirAsync) => Task.FromResult(new DesfechoDaDelegacaoDto(EstadoDaDelegacao.Respondida, null, true)),
            nameof(IDelegacoesAppService.ExpirarAsync) or nameof(IDelegacoesAppService.FalharAsync) =>
                Task.FromResult(new DesfechoDaDelegacaoDto(EstadoDaDelegacao.Expirada, (string)argumentos[2]!, true)),
            nameof(IDelegacoesAppService.RegistrarProgressoAsync) => Task.CompletedTask,
            _ => throw new NotSupportedException(metodo.Name)
        };
    }
}

// Implementação falsa de uma interface por DispatchProxy: registra as chamadas e delega a resposta.
public class Falso<T> : DispatchProxy where T : class
{
    private Func<MethodInfo, object?[], object?> responder = (metodo, _) => throw new NotSupportedException(metodo.Name);

    public T Servico { get; private set; } = null!;
    public List<(string Metodo, object?[] Argumentos)> Chamadas { get; } = [];

    public static Falso<T> Criar(Func<MethodInfo, object?[], object?> responder)
    {
        var servico = Create<T, Falso<T>>();
        var falso = (Falso<T>)(object)servico;
        (falso.responder, falso.Servico) = (responder, servico);
        return falso;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        Chamadas.Add((targetMethod!.Name, args ?? []));
        return responder(targetMethod, args ?? []);
    }
}
