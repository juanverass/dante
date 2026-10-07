using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dante.Application;
using Dante.Application.CapturaDeConhecimento;
using Dante.Application.SegurancaDoBrain;
using Dante.Domain.CapturaDeConhecimento;
using Dante.Domain.Conhecimentos;
using Dante.Domain.Projetos;
using Dante.Infrastructure;
using Dante.Worker.Brain;
using Dante.Worker.Jobs;
using Dante.Worker.Sessions;
using Dante.Worker.Telegram;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dante.Tests;

public sealed class BrainMcpTests
{
    [Fact]
    public async Task StdioAnunciaFerramentasEClassificaMutacoesERecusaIdentidadeLivre()
    {
        var chamadas = 0;
        var entrada = string.Join('\n',
            """{"jsonrpc":"2.0","id":1,"method":"initialize"}""",
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"brain_obter_escopo","arguments":{"tenant":"outro"}}}""",
            """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"brain_obter_escopo","arguments":{}}}""");
        using var saida = new StringWriter();
        await ServidorMcpDoBrain.ExecutarAsync(new StringReader(entrada), saida, (_, _, _) =>
        { chamadas++; return Task.FromResult(ServidorMcpDoBrain.Resultado(new { espaco = "Desenvolvimento" })); });
        var respostas = saida.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => JsonNode.Parse(x)!).ToArray();
        Assert.Equal(4, respostas.Length); Assert.Equal("dante_brain", respostas[0]["result"]!["serverInfo"]!["name"]!.GetValue<string>());
        Assert.Contains("capacidade interna", respostas[0]["result"]!["instructions"]!.GetValue<string>());
        Assert.Contains("Nunca procure o Brain em plugins", respostas[0]["result"]!["instructions"]!.GetValue<string>());
        var tools = respostas[1]["result"]!["tools"]!.AsArray(); Assert.Equal(14, tools.Count);
        Assert.All(tools, t => Assert.False(t!["inputSchema"]!["additionalProperties"]!.GetValue<bool>()));
        Assert.True(tools.Single(t => t!["name"]!.GetValue<string>() == "brain_buscar_conhecimento")!["annotations"]!["readOnlyHint"]!.GetValue<bool>());
        Assert.True(tools.Single(t => t!["name"]!.GetValue<string>() == "brain_cancelar_candidato")!["annotations"]!["destructiveHint"]!.GetValue<bool>());
        Assert.True(respostas[2]["result"]!["isError"]!.GetValue<bool>()); Assert.Equal(1, chamadas);
        await Assert.ThrowsAsync<ArgumentException>(() => ServidorMcpDoBrain.LerLinhaAsync(new StringReader(new string('x', 33_000)), 32_000, default));
    }
    [Fact]
    public async Task ConfirmacaoSoVemDaConversaCorretaERevogacaoImpedeReuso()
    {
        var registro = new RegistroDeOperacoesBrain(); var m = Mensagem("pedido"); var chamadas = 0;
        registro.RegistrarSessao("S1", m);
        registro.Propor(m, "S1", (_, _) => { chamadas++; return Task.FromResult("feito"); });
        Assert.Null(await registro.AtenderAsync(m with { From = new(7) }, "confirmar", default));
        Assert.Null(await registro.AtenderAsync(m with { Chat = new(99) }, "confirmar", default));
        Assert.Null(await registro.AtenderAsync(m with { MessageThreadId = 8 }, "confirmar", default));
        Assert.Equal("feito", await registro.AtenderAsync(m, "confirmar", default));
        Assert.Null(await registro.AtenderAsync(m, "confirmar", default)); Assert.Equal(1, chamadas);
        registro.Propor(m, "S1", (_, _) => Task.FromResult("indevido")); registro.Revogar("S1");
        Assert.Null(await registro.AtenderAsync(m, "confirmar", default));
    }
    [Fact]
    public async Task SemBrainOuSemConversaNaoHaServidorENaoImpedeSessao()
    {
        using var services = new ServiceCollection().BuildServiceProvider(); var registro = new RegistroDeOperacoesBrain();
        var brain = new TelegramBrain(services.GetRequiredService<IServiceScopeFactory>(), new ConfigurationBuilder().Build(), registro);
        var ops = new OperacoesMcpDoBrain(services.GetRequiredService<IServiceScopeFactory>(), registro, brain);
        using var ferramentas = new FerramentasDoBrainParaAgentes(brain, ops, registro, NullLogger<FerramentasDoBrainParaAgentes>.Instance);
        var drivers = new FakeSessionDriverFactory(); await using var sessoes = new SessionRegistry(drivers, NullLogger<SessionRegistry>.Instance, toolServers: ferramentas);
        var resultado = await sessoes.StartAsync(new(42, AgentKind.Codex, JobExecutionContext.General(AppContext.BaseDirectory), BrainConversation: Mensagem("pedido").ParaFerramentas()));
        Assert.True(resultado.Accepted); Assert.Empty(drivers.Created.Single().StartOptions!.ToolServers!);
        Assert.Empty(await ferramentas.ForAsync(42, AgentKind.Claude));
    }
    [Fact]
    public void TitulosETagsSobrevivemAPromocaoEInferenciaNaoViraFato()
    {
        var prova = new ProvenienciaDoConhecimento(Guid.NewGuid(), "MCP/agente:codex", "mensagem:1", trechoDaFonte: "conteúdo");
        var c = new CandidatoDeConhecimento(Guid.NewGuid(), null, TipoDeConhecimento.Nota, "conteúdo", Sensibilidade.Pessoal,
            NaturezaDoConteudo.ConclusaoDoAgente, ModoDeCaptura.Explicita, "sugestão", prova, DateTimeOffset.UtcNow, titulo: "Título original", tags: ["brain"]);
        var item = c.Promover(c.Revisao, prova, DateTimeOffset.UtcNow);
        Assert.Equal(StatusDoConhecimento.Inferido, item.Status); Assert.Equal(TipoDeConhecimento.Inferencia, item.Tipo);
        Assert.Equal("Título original", JsonNode.Parse(item.DadosEstruturados!)!["titulo"]!.GetValue<string>()); Assert.Equal(["brain"], item.Tags);
        Assert.Equal("Título original", c.Historico[^1].Titulo);
    }
    [Fact]
    public async Task SessaoAusenteOuRevogadaNaoAceitaTurnoNemChamadasConcorrentes()
    {
        var registro = new RegistroDeOperacoesBrain(); var m = Mensagem("A");
        Assert.False(registro.ConversaPermitida("S1", m));
        registro.RegistrarSessao("S1", m); Assert.True(registro.ConversaPermitida("S1", m));
        await Task.WhenAll(Task.Run(() => registro.Revogar("S1")), Task.Run(() => registro.IniciarTurno("S1", Mensagem("B", 124))));
        Assert.False(registro.ConversaPermitida("S1", m));
        Assert.Throws<UnauthorizedAccessException>(() => registro.Atual("S1"));
        registro.IniciarTurno("S1", m); Assert.False(registro.ConversaPermitida("S1", m));
        Assert.Throws<UnauthorizedAccessException>(() => registro.Propor(m, "S1", (_, _) => Task.FromResult("indevido")));
    }
    [PostgreSqlFact]
    public async Task MensagemEnfileiradaSoMudaOrigemQuandoSeuTurnoComeca()
    {
        await using var banco = await Banco.CriarAsync(); using var app = CriarApp(banco.ConnectionString);
        var brain = app.GetRequiredService<TelegramBrain>(); await SelecionarAsync(brain);
        var registro = app.GetRequiredService<RegistroDeOperacoesBrain>(); using var ferramentas = Provedor(app);
        var drivers = new FakeSessionDriverFactory();
        await using var sessoes = new SessionRegistry(drivers, NullLogger<SessionRegistry>.Instance, toolServers: new CompositorDeFerramentas([ferramentas]));
        var a = Mensagem("Conteúdo do turno A", 200); var b = Mensagem("Conteúdo do turno B", 201);
        var inicio = await sessoes.StartAsync(new(42, AgentKind.Codex, JobExecutionContext.General(AppContext.BaseDirectory), BrainConversation: a.ParaFerramentas()));
        var id = inicio.Session!.Id;
        Assert.Equal(SubmitOutcome.TurnStarted, (await sessoes.SubmitAsync(42, id, new AgentInput(a.Text!, []) { BrainConversation = a.ParaFerramentas() })).Outcome);
        await brain.AtenderAsync(b, b.Text!, id); // B chegou ao adapter mas A ainda executa.
        Assert.Equal(SubmitOutcome.Queued, (await sessoes.SubmitAsync(42, id, new AgentInput(b.Text!, []) { BrainConversation = b.ParaFerramentas() })).Outcome);
        await using var cliente = await Cliente.ConectarAsync(Assert.Single(drivers.Created.Single().StartOptions!.ToolServers!));
        async Task Capturar(TelegramMessage m) => Assert.False((await cliente.ChamarAsync("brain_capturar_conhecimento", new { conteudo = m.Text, natureza = "DitoPeloUsuario", justificativa = "pedido" }))["isError"]!.GetValue<bool>());
        await Capturar(a);
        Assert.True((await cliente.ChamarAsync("brain_capturar_conhecimento", new { conteudo = b.Text, natureza = "DitoPeloUsuario", justificativa = "ainda não iniciou" }))["isError"]!.GetValue<bool>());
        drivers.Created.Single().Emit(new TurnCompletedEvent(AgentTurnOutcome.Completed));
        using var prazo = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (drivers.Created.Single().TurnInputs.Count < 2) await Task.Delay(10, prazo.Token);
        await Capturar(b);
        await using (var db = banco.Contexto())
        {
            var itens = await db.CandidatosDeConhecimento.ToListAsync(); Assert.Equal(2, itens.Count);
            Assert.EndsWith(":200", itens.Single(x => x.Conteudo == a.Text).Proveniencia.ReferenciaDaFonte);
            Assert.EndsWith(":201", itens.Single(x => x.Conteudo == b.Text).Proveniencia.ReferenciaDaFonte);
        }
        ferramentas.EndSession(id);
        Assert.False(registro.ConversaPermitida(id, a));
        Assert.Throws<UnauthorizedAccessException>(() => registro.Atual(id));
        var escopo = (await brain.ResolverEscopoMcpAsync(b))!;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => app.GetRequiredService<OperacoesMcpDoBrain>().ExecutarAsync(escopo, b, id, "codex",
            "brain_capturar_conhecimento", JsonSerializer.SerializeToElement(new { conteudo = b.Text, natureza = "DitoPeloUsuario", justificativa = "chamada após revogação" })));
        await using (var db = banco.Contexto()) Assert.Equal(2, await db.CandidatosDeConhecimento.CountAsync());
    }
    [PostgreSqlFact]
    public async Task SteerNativoAceitoAtualizaOrigemERecusadoPreservaEvidencia()
    {
        await using var banco = await Banco.CriarAsync(); using var app = CriarApp(banco.ConnectionString);
        var brain = app.GetRequiredService<TelegramBrain>(); await SelecionarAsync(brain);
        var registro = app.GetRequiredService<RegistroDeOperacoesBrain>(); using var ferramentas = Provedor(app);
        var drivers = new FakeSessionDriverFactory();
        await using var sessoes = new SessionRegistry(drivers, NullLogger<SessionRegistry>.Instance, toolServers: new CompositorDeFerramentas([ferramentas]));
        var a = Mensagem("Decisão exclusiva de A", 300); var b = Mensagem("Decisão exclusiva de B", 301); var c = Mensagem("Decisão recusada de C", 302);
        AgentInput Entrada(TelegramMessage m) => new(m.Text!, []) { BrainConversation = m.ParaFerramentas() };
        var inicio = await sessoes.StartAsync(new(42, AgentKind.Codex, JobExecutionContext.General(AppContext.BaseDirectory), BrainConversation: a.ParaFerramentas()));
        var id = inicio.Session!.Id; var driver = drivers.Created.Single(); Assert.True(driver.Capabilities.NativeSteer);
        Assert.Equal(SubmitOutcome.TurnStarted, (await sessoes.SubmitAsync(42, id, Entrada(a))).Outcome);
        await using var cliente = await Cliente.ConectarAsync(Assert.Single(driver.StartOptions!.ToolServers!));
        async Task<bool> Capturar(TelegramMessage m) => (await cliente.ChamarAsync("brain_capturar_conhecimento", new { conteudo = m.Text, natureza = "DitoPeloUsuario", justificativa = "pedido" }))["isError"]!.GetValue<bool>();
        driver.SteerFailure = new AgentSteerRejectedException("recusado");
        Assert.Equal(SubmitOutcome.Rejected, (await sessoes.SubmitAsync(42, id, Entrada(c), MessageDelivery.Steer)).Outcome);
        Assert.Equal(300, registro.Atual(id).MessageId); Assert.False(await Capturar(a)); Assert.True(await Capturar(c));
        driver.SteerFailure = null;
        Assert.Equal(SubmitOutcome.Steered, (await sessoes.SubmitAsync(42, id, Entrada(b), MessageDelivery.Steer)).Outcome);
        Assert.Equal(301, registro.Atual(id).MessageId); Assert.False(await Capturar(b)); Assert.True(await Capturar(a));
        driver.SteerFailure = new AgentSteerRejectedException("recusado novamente");
        Assert.Equal(SubmitOutcome.Rejected, (await sessoes.SubmitAsync(42, id, Entrada(c), MessageDelivery.Steer)).Outcome);
        Assert.Equal(301, registro.Atual(id).MessageId); Assert.True(await Capturar(c));
        await using var db = banco.Contexto(); var itens = await db.CandidatosDeConhecimento.ToListAsync(); Assert.Equal(2, itens.Count);
        Assert.EndsWith(":300", itens.Single(x => x.Conteudo == a.Text).Proveniencia.ReferenciaDaFonte);
        Assert.EndsWith(":301", itens.Single(x => x.Conteudo == b.Text).Proveniencia.ReferenciaDaFonte);
    }
    [PostgreSqlFact]
    public async Task RelacoesRestritasNaoConsomemLimiteDasPermitidasEmOutraPagina()
    {
        await using var banco = await Banco.CriarAsync(); using var app = CriarApp(banco.ConnectionString);
        var brain = app.GetRequiredService<TelegramBrain>(); await SelecionarAsync(brain);
        var m = Mensagem("consultar relações"); var escopo = (await brain.ResolverEscopoMcpAsync(m))!;
        app.GetRequiredService<RegistroDeOperacoesBrain>().RegistrarSessao("S1", m);
        var instante = DateTimeOffset.UtcNow.AddMinutes(-10);
        var prova = new ProvenienciaDoConhecimento(escopo.Acesso.IdUsuario, "evidência restrita", "fonte da relação", trechoDaFonte: "prova não exibida");
        Conhecimento Novo(string texto, Sensibilidade classe) => new(escopo.Acesso.IdEspacoDeConhecimento, escopo.Acesso.IdProjeto, TipoDeConhecimento.Nota,
            texto, null, StatusDoConhecimento.Inferido, null, classe, null, null, [], prova, instante);
        var raiz = Novo("raiz permitida", Sensibilidade.Pessoal);
        var restritos = Enumerable.Range(0, 101).Select(i => Novo($"restrito {i}", i % 2 == 0 ? Sensibilidade.Secreto : Sensibilidade.Confidencial)).ToArray();
        var permitidos = new[] { Novo("permitido A", Sensibilidade.Pessoal), Novo("permitido B", Sensibilidade.Trabalho) };
        await using (var db = banco.Contexto())
        {
            db.Add(raiz); db.AddRange(restritos); db.AddRange(permitidos);
            foreach (var (item, i) in restritos.Concat(permitidos).Select((item, i) => (item, i)))
                db.Add(new Dante.Domain.RelacoesDeConhecimento.RelacaoDeConhecimento(raiz, item, Dante.Domain.RelacoesDeConhecimento.TipoDeRelacao.DependeDe, prova, instante.AddSeconds(i + 1)));
            await db.SaveChangesAsync();
        }
        var ops = app.GetRequiredService<OperacoesMcpDoBrain>();
        async Task<Dante.Application.RelacoesDeConhecimento.VizinhancaDto> Listar(Guid id, int limite) =>
            (Dante.Application.RelacoesDeConhecimento.VizinhancaDto)(await ops.ExecutarAsync(escopo, m, "S1", "codex", "brain_listar_relacoes", JsonSerializer.SerializeToElement(new { id, limite })))!;
        var um = await Listar(raiz.Id, 1); Assert.True(um.LimiteAtingido); Assert.Equal(permitidos[0].Id, Assert.Single(um.Relacoes).IdDestino);
        var dois = await Listar(raiz.Id, 2); Assert.False(dois.LimiteAtingido); Assert.Equal(permitidos.Select(x => x.Id), dois.Relacoes.Select(x => x.IdDestino));
        Assert.All(dois.Relacoes, r => { Assert.Contains("protegida", r.Proveniencia.Origem); Assert.Null(r.Proveniencia.TrechoDaFonte); });
        // Uma raiz permitida com somente vizinhos restritos esgota sem revelar contagem/truncamento.
        var isolada = Novo("raiz sem vizinhos permitidos", Sensibilidade.Pessoal);
        await using (var db = banco.Contexto())
        {
            db.Add(isolada); db.Add(new Dante.Domain.RelacoesDeConhecimento.RelacaoDeConhecimento(isolada, restritos[0], Dante.Domain.RelacoesDeConhecimento.TipoDeRelacao.DependeDe, prova, instante.AddMinutes(3)));
            await db.SaveChangesAsync();
        }
        var nenhuma = await Listar(isolada.Id, 1); Assert.Empty(nenhuma.Relacoes); Assert.False(nenhuma.LimiteAtingido);
    }
    [PostgreSqlFact]
    public async Task LoteSeparadoComPostgresDeduplicacaoConfirmacaoENovaSessao()
    {
        await using var banco = await Banco.CriarAsync(); using var app = CriarApp(banco.ConnectionString);
        var brain = app.GetRequiredService<TelegramBrain>(); var registro = app.GetRequiredService<RegistroDeOperacoesBrain>();
        await SelecionarAsync(brain);
        using var ferramentas = Provedor(app);
        var drivers = new FakeSessionDriverFactory(); var compositor = new CompositorDeFerramentas([new PlanilhasSimuladas(), ferramentas]);
        await using var sessoes = new SessionRegistry(drivers, NullLogger<SessionRegistry>.Instance, toolServers: compositor);
        var conteudos = Enumerable.Range(1, 24).Select(i => $"Decisão canônica {i}: D.A.N.T.E. coordena agentes locais para tarefa {i}.").ToArray();
        var mensagem = Mensagem(string.Join('\n', conteudos)); registro.Observar(mensagem);
        var started = await sessoes.StartAsync(new(42, AgentKind.Codex, JobExecutionContext.Repository("cognexa-backend", AppContext.BaseDirectory), BrainConversation: mensagem.ParaFerramentas()));
        Assert.True(started.Accepted); Assert.Equal(2, drivers.Created.Single().StartOptions!.ToolServers!.Count);
        var server = drivers.Created.Single().StartOptions!.ToolServers!.Single(x => x.Name == "dante_brain");
        Assert.Empty(server.Environment); Assert.DoesNotContain(banco.ConnectionString, string.Join(' ', server.Arguments));
        await using var cliente = await Cliente.ConectarAsync(server);
        for (var i = 0; i < conteudos.Length; i++)
            Assert.False((await cliente.ChamarAsync("brain_capturar_conhecimento", new { titulo = $"Decisão {i+1}", conteudo = conteudos[i], tags = new[] { "dante" }, natureza = "DitoPeloUsuario", justificativa = "Captura solicitada pelo usuário" }))["isError"]!.GetValue<bool>());
        var repetido = await cliente.ChamarAsync("brain_capturar_conhecimento", new { conteudo = conteudos[0], natureza = "DitoPeloUsuario", justificativa = "Repetição" });
        Assert.Contains("true", repetido["content"]![0]!["text"]!.GetValue<string>());
        await using (var db = banco.Contexto())
        {
            Assert.Empty(await db.Conhecimentos.ToListAsync()); var candidatos = await db.CandidatosDeConhecimento.ToListAsync(); Assert.Equal(24, candidatos.Count);
            Assert.All(candidatos, c => { Assert.Equal(EstadoDoCandidato.Pendente, c.Estado); Assert.Contains("MCP/agente:codex", c.Proveniencia.Origem); Assert.Contains(":123", c.Proveniencia.ReferenciaDaFonte); Assert.NotNull(c.Titulo); });
            var primeiro = candidatos.Single(x => x.Conteudo == conteudos[0]);
            var preparar = await cliente.ChamarAsync("brain_confirmar_candidato", new { id = primeiro.Id, revisao = primeiro.Revisao }); Assert.False(preparar["isError"]!.GetValue<bool>());
            Assert.Empty(await db.Conhecimentos.ToListAsync());
        }
        Assert.Contains("consolidado", await brain.AtenderAsync(Mensagem("confirmar", 124), "confirmar", started.Session!.Id));
        await using (var db = banco.Contexto())
        { var item = await db.Conhecimentos.SingleAsync(); Assert.Equal(StatusDoConhecimento.Confirmado, item.Status); Assert.Equal(["dante"], item.Tags); Assert.NotNull(item.DadosEstruturados); }
        var nova = await sessoes.StartAsync(new(42, AgentKind.Claude, JobExecutionContext.General(AppContext.BaseDirectory), BrainConversation: Mensagem("consulte orquestração", 125).ParaFerramentas()));
        Assert.True(nova.Accepted); await using var novoCliente = await Cliente.ConectarAsync(drivers.Created.Last().StartOptions!.ToolServers!.Single(x => x.Name == "dante_brain"));
        var busca = await novoCliente.ChamarAsync("brain_buscar_conhecimento", new { texto = "coordena agentes" }); Assert.Contains(conteudos[0], busca["content"]![0]!["text"]!.GetValue<string>());
        await brain.AtenderAsync(Mensagem("criar projeto Outro", 126), "criar projeto Outro", nova.Session!.Id);
        await brain.AtenderAsync(Mensagem("usar projeto D.A.N.T.E.", 127), "usar projeto D.A.N.T.E.", nova.Session!.Id);
        Assert.True((await novoCliente.ChamarAsync("brain_obter_escopo", new { }))["isError"]!.GetValue<bool>());
    }
    [PostgreSqlFact]
    public async Task AutorizacaoRevisoesProvenienciaERelacoesNaoPodemSerBurladas()
    {
        await using var banco = await Banco.CriarAsync(); using var app = CriarApp(banco.ConnectionString); var brain = app.GetRequiredService<TelegramBrain>();
        await SelecionarAsync(brain); var m = Mensagem("Arquitetura hexagonal separa camadas. Correção solicitada: Arquitetura hexagonal isola camadas."); var escopo = (await brain.ResolverEscopoMcpAsync(m))!;
        var ops = app.GetRequiredService<OperacoesMcpDoBrain>();
        app.GetRequiredService<RegistroDeOperacoesBrain>().RegistrarSessao("S1", m);
        Task<object?> Operar(string nome, object args) => ops.ExecutarAsync(escopo, m, "S1", "claude", nome, JsonSerializer.SerializeToElement(args));
        await Assert.ThrowsAsync<ArgumentException>(() => Operar("brain_obter_escopo", new { usuario = "outro" }));
        await Assert.ThrowsAsync<ArgumentException>(() => Operar("brain_capturar_conhecimento", new { conteudo = "inventado", natureza = "DitoPeloUsuario", justificativa = "solicitado" }));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Operar("brain_capturar_conhecimento", new { conteudo = m.Text, natureza = "DitoPeloUsuario", sensibilidade = "Secreto", justificativa = "solicitado" }));
        await Operar("brain_capturar_conhecimento", new { conteudo = m.Text, natureza = "DitoPeloUsuario", justificativa = "solicitado" });
        Guid id; await using (var db = banco.Contexto()) id = (await db.CandidatosDeConhecimento.SingleAsync()).Id;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Operar("brain_confirmar_candidato", new { id, revisao = 0 }));
        await Operar("brain_confirmar_candidato", new { id, revisao = 1 });
        await Assert.ThrowsAsync<ArgumentException>(() => Operar("brain_corrigir_candidato", new { id, revisao = 1, conteudo = "conteúdo que o usuário não escreveu", natureza = "DitoPeloUsuario", justificativa = "correção" }));
        await using (var db = banco.Contexto()) { var c = await db.CandidatosDeConhecimento.SingleAsync(); Assert.Equal(1, c.Revisao); Assert.Equal(m.Text, c.Conteudo); }
        await Operar("brain_corrigir_candidato", new { id, revisao = 1, conteudo = "Arquitetura hexagonal isola camadas.", natureza = "DitoPeloUsuario", justificativa = "correção" });
        Assert.Contains("não foi confirmada", await brain.AtenderAsync(Mensagem("confirmar", 124), "confirmar", "S1"));
        await using (var db = banco.Contexto()) Assert.Empty(await db.Conhecimentos.ToListAsync());
        await Operar("brain_atualizar_contexto_de_trabalho", new { revisao = 0, objetivo = "implementar MCP", tarefa = "issue 226", progresso = "testando", ultimo_resultado = "captura" });
        await using (var db = banco.Contexto()) { Assert.Empty(await db.Conhecimentos.ToListAsync()); Assert.Single(await db.Set<Dante.Domain.ContextosDeTrabalho.ContextoDeTrabalho>().ToListAsync()); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => Operar("brain_atualizar_contexto_de_trabalho", new { revisao = 0, objetivo = "X", tarefa = "X", progresso = "X", ultimo_resultado = "X" }));
        await brain.AtenderAsync(Mensagem("criar projeto Outro"), "criar projeto Outro"); var outro = (await brain.ResolverEscopoMcpAsync(m))!;
        app.GetRequiredService<RegistroDeOperacoesBrain>().RegistrarSessao("S2", m);
        await Assert.ThrowsAsync<ArgumentException>(() => ops.ExecutarAsync(outro, m, "S2", "codex", "brain_mostrar_origem", JsonSerializer.SerializeToElement(new { id, origem = "candidato" })));
    }
    [PostgreSqlFact]
    public async Task UsuariosProjetosSensibilidadeECancelamentoDeRelacaoSaoIsolados()
    {
        await using var banco = await Banco.CriarAsync(); using var app = CriarApp(banco.ConnectionString);
        var brain = app.GetRequiredService<TelegramBrain>(); await SelecionarAsync(brain);
        var m = Mensagem("pedido"); var escopo = (await brain.ResolverEscopoMcpAsync(m))!;
        var ops = app.GetRequiredService<OperacoesMcpDoBrain>();
        app.GetRequiredService<RegistroDeOperacoesBrain>().RegistrarSessao("S1", m);
        var prova = new ProvenienciaDoConhecimento(escopo.Acesso.IdUsuario, "fonte de teste", "mensagem:1", trechoDaFonte: "evidência");
        Conhecimento Novo(string texto, Sensibilidade classe, Guid? projeto = null) => new(escopo.Acesso.IdEspacoDeConhecimento, projeto ?? escopo.Acesso.IdProjeto,
            TipoDeConhecimento.Fato, texto, null, StatusDoConhecimento.Inferido, null, classe, null, null, [], prova, DateTimeOffset.UtcNow);
        var a = Novo("camadas hexagonais", Sensibilidade.Pessoal); var b = Novo("interfaces de aplicação", Sensibilidade.Pessoal);
        var secreto = Novo("segredo canônico", Sensibilidade.Secreto); var confidencial = Novo("dado confidencial", Sensibilidade.Confidencial);
        var projetoB = new Projeto(escopo.Acesso.IdEspacoDeConhecimento, "Projeto B"); var fora = Novo("projeto B", Sensibilidade.Pessoal, projetoB.Id);
        var protegida = new ProvenienciaDoConhecimento(escopo.Acesso.IdUsuario, "evidência privada da relação", "fonte restrita", trechoDaFonte: "conteúdo protegido da relação");
        await using(var db = banco.Contexto())
        {
            db.AddRange(projetoB, a, b, secreto, confidencial, fora);
            db.AddRange(new Dante.Domain.RelacoesDeConhecimento.RelacaoDeConhecimento(a, secreto, Dante.Domain.RelacoesDeConhecimento.TipoDeRelacao.RelacionadoA, protegida, DateTimeOffset.UtcNow),
                new Dante.Domain.RelacoesDeConhecimento.RelacaoDeConhecimento(a, confidencial, Dante.Domain.RelacoesDeConhecimento.TipoDeRelacao.RelacionadoA, protegida, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }
        Task<object?> Operar(string nome, object args) => ops.ExecutarAsync(escopo, m, "S1", "codex", nome, JsonSerializer.SerializeToElement(args));
        foreach (var alvo in new[] { secreto, confidencial, fora })
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Operar("brain_mostrar_origem", new { id = alvo.Id, origem = "conhecimento" }));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Operar("brain_criar_relacao", new { origem = a.Id, destino = fora.Id, revisao_origem = 1, revisao_destino = 1, tipo = "RelacionadoA" }));
        await Operar("brain_criar_relacao", new { origem = a.Id, destino = b.Id, revisao_origem = 1, revisao_destino = 1, tipo = "RelacionadoA" });
        await using(var db = banco.Contexto()) Assert.Equal(2, await db.RelacoesDeConhecimento.CountAsync());
        Assert.Contains("cancelada", await brain.AtenderAsync(Mensagem("cancelar"), "cancelar", "S1"));
        await using(var db = banco.Contexto()) Assert.Equal(2, await db.RelacoesDeConhecimento.CountAsync());
        await Operar("brain_criar_relacao", new { origem = a.Id, destino = b.Id, revisao_origem = 1, revisao_destino = 1, tipo = "RelacionadoA" });
        Assert.Contains("registrada", await brain.AtenderAsync(Mensagem("confirmar"), "confirmar", "S1"));
        var vizinhas = System.Text.Json.JsonSerializer.Serialize(await Operar("brain_listar_relacoes", new { id = a.Id })); Assert.Contains(b.Id.ToString(), vizinhas);
        Assert.DoesNotContain(secreto.Id.ToString(), vizinhas); Assert.DoesNotContain(confidencial.Id.ToString(), vizinhas);
        Assert.DoesNotContain("evidência privada", vizinhas); Assert.DoesNotContain("conteúdo protegido", vizinhas);
        Assert.DoesNotContain("mensagem:123", vizinhas); Assert.Contains("canal MCP", vizinhas);
        await brain.AtenderAsync(m with { From = new(7) }, "criar espaço Outro usuário");
        var escopoB = (await brain.ResolverEscopoMcpAsync(m with { From = new(7) }))!;
        app.GetRequiredService<RegistroDeOperacoesBrain>().RegistrarSessao("S2", m with { From = new(7) });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ops.ExecutarAsync(escopoB, m with { From = new(7) }, "S2", "claude", "brain_mostrar_origem", JsonSerializer.SerializeToElement(new { id = a.Id, origem = "conhecimento" })));
        await Assert.ThrowsAsync<ArgumentException>(() => Operar("brain_capturar_conhecimento", new { conteudo = "senha=supersecreta123", natureza = "ConclusaoDoAgente", justificativa = "teste" }));
    }

    [PostgreSqlFact]
    public async Task ProcessoStdioRealEncaminhaAoWorkerSemConfiguracaoDeBanco()
    {
        await using var banco = await Banco.CriarAsync(); using var app = CriarApp(banco.ConnectionString); var brain = app.GetRequiredService<TelegramBrain>();
        await SelecionarAsync(brain); using var ferramentas = Provedor(app);
        var servidor = Assert.Single(await ferramentas.ForSessionAsync("STDIO", new(42, AgentKind.Codex, JobExecutionContext.General(AppContext.BaseDirectory), BrainConversation: Mensagem("pedido").ParaFerramentas())));
        var start = new System.Diagnostics.ProcessStartInfo(servidor.Command) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in servidor.Arguments) start.ArgumentList.Add(arg);
        using var processo = System.Diagnostics.Process.Start(start)!;
        var erro = processo.StandardError.ReadToEndAsync();
        try
        {
            await processo.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize"}""");
            var linha = await processo.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(linha is not null, processo.HasExited ? await erro : "MCP não respondeu");
            Assert.Equal("dante_brain", JsonNode.Parse(linha!)!["result"]!["serverInfo"]!["name"]!.GetValue<string>());
            await processo.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"brain_obter_escopo","arguments":{}}}""");
            var resultado = JsonNode.Parse((await processo.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)))!)!;
            Assert.Contains("Desenvolvimento", resultado["result"]!["content"]![0]!["text"]!.GetValue<string>());
        }
        finally { if (!processo.HasExited) processo.Kill(true); }
    }

    [PostgreSqlFact]
    public async Task PropostaMcpNaoDeixaConfirmacaoNaturalAntigaReutilizavel()
    {
        await using var banco = await Banco.CriarAsync(); using var app = CriarApp(banco.ConnectionString); var brain = app.GetRequiredService<TelegramBrain>();
        await SelecionarAsync(brain);
        Assert.Contains("candidato", await brain.AtenderAsync(Mensagem("registre no Brain: conteúdo antigo", 120), "registre no Brain: conteúdo antigo"));
        var mensagem = Mensagem("conteúdo novo", 121); var escopo = (await brain.ResolverEscopoMcpAsync(mensagem))!;
        var ops = app.GetRequiredService<OperacoesMcpDoBrain>();
        app.GetRequiredService<RegistroDeOperacoesBrain>().RegistrarSessao("S1", mensagem);
        await ops.ExecutarAsync(escopo, mensagem, "S1", "codex", "brain_capturar_conhecimento", JsonSerializer.SerializeToElement(new { conteudo = mensagem.Text, natureza = "DitoPeloUsuario", justificativa = "pedido novo" }));
        Guid id; await using(var db = banco.Contexto()) id = (await db.CandidatosDeConhecimento.SingleAsync(x => x.Conteudo == "conteúdo novo")).Id;
        await ops.ExecutarAsync(escopo, mensagem, "S1", "codex", "brain_confirmar_candidato", JsonSerializer.SerializeToElement(new { id, revisao = 1 }));
        Assert.Contains("consolidado", await brain.AtenderAsync(Mensagem("confirmar", 122), "confirmar", "S1"));
        await brain.AtenderAsync(Mensagem("confirmar", 123), "confirmar", "S1");
        await using(var db = banco.Contexto()) Assert.Equal("conteúdo novo", (await db.Conhecimentos.SingleAsync()).Conteudo);
    }

    internal static ServiceProvider CriarApp(string conexao)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Dante"] = conexao, ["Telegram:AllowedUserIds"] = "42,7" }).Build();
        var services = new ServiceCollection().AddApplication().AddInfrastructure(config);
        services.AddSingleton<RegistroDeOperacoesBrain>(); services.AddSingleton(p => new TelegramBrain(p.GetRequiredService<IServiceScopeFactory>(), config, p.GetRequiredService<RegistroDeOperacoesBrain>()));
        services.AddSingleton<OperacoesMcpDoBrain>(); return services.BuildServiceProvider();
    }
    internal static FerramentasDoBrainParaAgentes Provedor(ServiceProvider app) => new(app.GetRequiredService<TelegramBrain>(), app.GetRequiredService<OperacoesMcpDoBrain>(), app.GetRequiredService<RegistroDeOperacoesBrain>(), NullLogger<FerramentasDoBrainParaAgentes>.Instance,
        ("/usr/bin/dotnet", [CaminhoWorker()]));
    private static string CaminhoWorker()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Dante.sln")))
                return Path.Combine(dir.FullName, "src", "Dante.Worker", "bin", new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name, "net10.0", "Dante.Worker.dll");
        throw new DirectoryNotFoundException();
    }
    internal static TelegramMessage Mensagem(string texto, long id = 123) => new(new(55), texto, new(42), MessageId: id);
    internal static async Task SelecionarAsync(TelegramBrain brain)
    {
        Assert.Contains("criado", await brain.AtenderAsync(Mensagem("criar espaço Desenvolvimento"), "criar espaço Desenvolvimento"));
        Assert.Contains("selecionado", await brain.AtenderAsync(Mensagem("criar projeto D.A.N.T.E."), "criar projeto D.A.N.T.E."));
    }
    private sealed class PlanilhasSimuladas : IProvedorDeFerramentas
    {
        public Task<IReadOnlyList<AgentToolServer>> ForAsync(long u, AgentKind a, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AgentToolServer>>([new("dante_planilhas", "/usr/bin/dante", ["--mcp-planilhas"], new Dictionary<string,string>(), ["ler_intervalo"])]);
    }
    internal sealed class Cliente(NamedPipeClientStream pipe) : IAsyncDisposable
    {
        private readonly StreamReader leitor = new(pipe, Encoding.UTF8, false, 4096, true);
        private readonly StreamWriter escritor = new(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        public static async Task<Cliente> ConectarAsync(AgentToolServer server)
        {
            var pipe = new NamedPipeClientStream(".", server.Arguments[^1], PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(5000); return new(pipe);
        }
        public async Task<JsonObject> ChamarAsync(string nome, object argumentos)
        {
            await escritor.WriteLineAsync(JsonSerializer.Serialize(new { nome, argumentos }));
            return JsonNode.Parse((await leitor.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)))!)!.AsObject();
        }
        public async ValueTask DisposeAsync() { escritor.Dispose(); leitor.Dispose(); await pipe.DisposeAsync(); }
    }
}
