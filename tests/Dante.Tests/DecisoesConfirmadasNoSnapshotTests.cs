using System.Text.Json.Nodes;
using Dante.Application.ConstrucaoDeContexto;
using Dante.Application.ContextosDeTrabalho;
using Dante.Application.SegurancaDoBrain;
using Dante.Domain.Agentes;
using Dante.Domain.CapturaDeConhecimento;
using Dante.Domain.Conhecimentos;
using Dante.Domain.ContextosDeTrabalho;
using Dante.Domain.EspacosDeConhecimento;
using Dante.Domain.Projetos;
using Dante.Worker.Brain;
using Dante.Worker.Jobs;
using Dante.Worker.Sessions;
using Dante.Worker.Telegram;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dante.Tests;

public sealed class DecisoesConfirmadasNoSnapshotTests
{
    [Fact]
    public void SchemaAnunciaListaOpcionalDeIdsSemMudarCamposAnteriores()
    {
        var ferramenta = ServidorMcpDoBrain.Ferramentas.Single(x => x.Nome == "brain_atualizar_contexto_de_trabalho");
        Assert.Equal(["revisao", "objetivo", "tarefa", "progresso", "ultimo_resultado"], ferramenta.Obrigatorios);
        var campo = ferramenta.Propriedades["ids_decisoes_confirmadas"]!;
        Assert.Equal("array", campo["type"]!.GetValue<string>()); Assert.Equal(20, campo["maxItems"]!.GetValue<int>());
        Assert.Equal("string", campo["items"]!["type"]!.GetValue<string>()); Assert.Equal("uuid", campo["items"]!["format"]!.GetValue<string>());
        Assert.Null(campo["uniqueItems"]); // O domínio aceita duplicatas e salva a lista deduplicada.
        Assert.Contains("omitir", campo["description"]!.GetValue<string>());
        Assert.Contains("não afirmadas em texto livre", ferramenta.Descricao);
        Assert.Contains("Decisao/DitoPeloUsuario", ContratoDoBrainParaAgentes.Instrucoes);
    }

    [Fact]
    public void DominioMantemLimite20DeduplicacaoEOrdemSemAlterarVersaoEmRecusa()
    {
        var ids = Enumerable.Range(0, 20).Select(_ => Guid.NewGuid()).ToArray(); var agora = DateTimeOffset.UtcNow;
        var snapshot = new ContextoDeTrabalho(Guid.NewGuid(), null, Dados(ids), Sensibilidade.Pessoal, Guid.NewGuid(), "teste", agora, null);
        Assert.Equal(ids, snapshot.Dados.IdsDecisoesConfirmadas);
        snapshot.Substituir(1, Dados([ids[2], ids[2], ids[0]]), Sensibilidade.Pessoal, snapshot.IdResponsavel, "teste", agora, null);
        Assert.Equal([ids[2], ids[0]], snapshot.Dados.IdsDecisoesConfirmadas);
        Assert.Throws<ArgumentException>(() => snapshot.Substituir(2, Dados(Enumerable.Repeat(ids[0], 21).ToArray()),
            Sensibilidade.Pessoal, snapshot.IdResponsavel, "teste", agora, null));
        Assert.Equal(2, snapshot.Revisao); Assert.Equal([ids[2], ids[0]], snapshot.Dados.IdsDecisoesConfirmadas);
    }

    [PostgreSqlFact]
    public async Task McpIncluiConsultaPreservaNoFluxoNaturalRemoveELimpaReferencias()
    {
        await using var c = await Cenario.CriarAsync(); var a = await c.AdicionarAsync("decisão A"); var b = await c.AdicionarAsync("decisão B");
        await using var cliente = await c.ClienteAsync("crud");
        var salvo = Sucesso(await cliente.ChamarAsync("brain_atualizar_contexto_de_trabalho", Atualizacao(0, new[] { a.Id, a.Id, b.Id })));
        Assert.Equal([a.Id, b.Id], Ids(salvo)); Assert.Equal(1, salvo["Revisao"]!.GetValue<int>());
        Assert.Equal([a.Id, b.Id], Ids(Sucesso(await cliente.ChamarAsync("brain_obter_contexto_de_trabalho", new { }))));
        var omitido = Sucesso(await cliente.ChamarAsync("brain_atualizar_contexto_de_trabalho", Atualizacao(1, null, informar: false)));
        Assert.Equal([a.Id, b.Id], Ids(omitido));
        var natural = BrainMcpTests.Mensagem("atualize o contexto de trabalho: progresso: revisão natural", 124);
        Assert.Contains("atualizado", await c.Brain.AtenderAsync(natural, natural.Text!, "crud"));
        var mantido = Sucesso(await cliente.ChamarAsync("brain_obter_contexto_de_trabalho", new { }));
        Assert.Equal([a.Id, b.Id], Ids(mantido)); Assert.Equal(3, mantido["Revisao"]!.GetValue<int>());
        Assert.Equal([b.Id], Ids(Sucesso(await cliente.ChamarAsync("brain_atualizar_contexto_de_trabalho", Atualizacao(3, new[] { b.Id })))));
        Assert.Empty(Ids(Sucesso(await cliente.ChamarAsync("brain_atualizar_contexto_de_trabalho", Atualizacao(4, Array.Empty<Guid>())))));
        var conflito = await cliente.ChamarAsync("brain_atualizar_contexto_de_trabalho", Atualizacao(4, new[] { a.Id }));
        Assert.Equal("revisao_desatualizada", Codigo(conflito));
        Assert.Equal(5, Sucesso(await cliente.ChamarAsync("brain_obter_contexto_de_trabalho", new { }))["Revisao"]!.GetValue<int>());
    }

    [PostgreSqlFact]
    public async Task RecusasDeTipoConfirmacaoValidadeESensibilidadeNaoModificamSnapshot()
    {
        await using var c = await Cenario.CriarAsync(); var valido = await c.AdicionarAsync("decisão confirmada");
        var invalido = new[] {
            await c.AdicionarAsync("fato confirmado", tipo: TipoDeConhecimento.Fato),
            await c.AdicionarAsync("tipo inferência legado", tipo: TipoDeConhecimento.Inferencia),
            await c.AdicionarAsync("decisão ainda não confirmada", confirmado: false),
            await c.AdicionarAsync("conclusão do agente", confirmado: false, natureza: NaturezaDoConteudo.ConclusaoDoAgente),
            await c.AdicionarAsync("decisão expirada", ate: DateTimeOffset.UtcNow.AddHours(-1)),
            await c.AdicionarAsync("decisão futura", desde: DateTimeOffset.UtcNow.AddDays(1)),
            await c.AdicionarAsync("decisão mais sensível que snapshot", classe: Sensibilidade.Trabalho)
        };
        await using var cliente = await c.ClienteAsync("recusas");
        Sucesso(await cliente.ChamarAsync("brain_atualizar_contexto_de_trabalho", Atualizacao(0, new[] { valido.Id })));
        foreach (var item in invalido)
        {
            Assert.Equal("entrada_invalida", Codigo(await cliente.ChamarAsync("brain_atualizar_contexto_de_trabalho", Atualizacao(1, new[] { valido.Id, item.Id }))));
            var atual = Sucesso(await cliente.ChamarAsync("brain_obter_contexto_de_trabalho", new { }));
            Assert.Equal(1, atual["Revisao"]!.GetValue<int>()); Assert.Equal([valido.Id], Ids(atual));
        }
    }

    [PostgreSqlFact]
    public async Task ReferenciasInacessiveisProduzemMesmaRecusaSemOraculo()
    {
        await using var c = await Cenario.CriarAsync();
        var outroEspaco = new EspacoDeConhecimento(c.Acesso.IdUsuario, "Outro espaço");
        var outroUsuario = new EspacoDeConhecimento(Guid.NewGuid(), "Outro usuário");
        var outroTenant = new EspacoDeConhecimento(c.Acesso.IdUsuario, "Outro tenant", idTenant: Guid.NewGuid());
        var outroProjeto = new Projeto(c.Acesso.IdEspacoDeConhecimento, "Outro projeto");
        var inativo = await c.AdicionarAsync("decisão inativa");
        var candidato = new CandidatoDeConhecimento(c.Acesso.IdEspacoDeConhecimento, c.Acesso.IdProjeto, TipoDeConhecimento.Decisao,
            "candidato visível", Sensibilidade.Pessoal, NaturezaDoConteudo.DitoPeloUsuario, ModoDeCaptura.Explicita, "teste",
            new(c.Acesso.IdUsuario, "teste-256", "fonte:256", trechoDaFonte: "candidato visível"), DateTimeOffset.UtcNow);
        await using (var db = c.Banco.Contexto())
        {
            var item = await db.Conhecimentos.SingleAsync(x => x.Id == inativo.Id);
            item.Invalidar(item.Revisao, item.Proveniencia, DateTimeOffset.UtcNow);
            db.AddRange(outroEspaco, outroUsuario, outroTenant, outroProjeto, candidato); await db.SaveChangesAsync();
        }
        var inacessiveis = new[] {
            Guid.NewGuid(), inativo.Id,
            (await c.AdicionarAsync("outro espaço", acesso: c.Acesso with { IdEspacoDeConhecimento = outroEspaco.Id, IdProjeto = null })).Id,
            (await c.AdicionarAsync("outro usuário", acesso: new(outroUsuario.IdUsuario, outroUsuario.Id, null))).Id,
            (await c.AdicionarAsync("outro tenant", acesso: new(outroTenant.IdUsuario, outroTenant.Id, null))).Id,
            (await c.AdicionarAsync("outro projeto", acesso: c.Acesso with { IdProjeto = outroProjeto.Id })).Id,
            (await c.AdicionarAsync("projeto nulo", acesso: c.Acesso with { IdProjeto = null })).Id,
            (await c.AdicionarAsync("confidencial", classe: Sensibilidade.Confidencial)).Id,
            (await c.AdicionarAsync("secreto", classe: Sensibilidade.Secreto)).Id
        };
        await using var cliente = await c.ClienteAsync("isolamento");
        var referencia = await cliente.ChamarAsync("brain_atualizar_contexto_de_trabalho", Atualizacao(0, new[] { inacessiveis[0] }));
        Assert.Equal("nao_encontrado_ou_nao_autorizado", Codigo(referencia));
        foreach (var id in inacessiveis.Skip(1))
            Assert.Equal(referencia.ToJsonString(), (await cliente.ChamarAsync("brain_atualizar_contexto_de_trabalho", Atualizacao(0, new[] { id }))).ToJsonString());
        Assert.Equal("entrada_invalida", Codigo(await cliente.ChamarAsync("brain_atualizar_contexto_de_trabalho", Atualizacao(0, new[] { candidato.Id }))));
        Assert.Equal("null", (await cliente.ChamarAsync("brain_obter_contexto_de_trabalho", new { }))["content"]![0]!["text"]!.GetValue<string>());
    }

    [PostgreSqlFact]
    public async Task Limite20FormatosInvalidosEDeduplicacaoSaoAplicadosAntesDeSalvar()
    {
        await using var c = await Cenario.CriarAsync(); var itens = new List<Conhecimento>();
        for (var i = 0; i < 20; i++) itens.Add(await c.AdicionarAsync($"decisão {i}"));
        await using var cliente = await c.ClienteAsync("limites");
        Sucesso(await cliente.ChamarAsync("brain_atualizar_contexto_de_trabalho", Atualizacao(0, itens.Select(x => x.Id).ToArray())));
        foreach (var lista in new object?[] { null, "não é lista", new[] { "inválido" }, new[] { Guid.Empty }, new object?[] { null }, new[] { 1 },
            Enumerable.Repeat(itens[0].Id, 21).ToArray(), new[] { new string('x', 41) } })
        {
            Assert.Equal("entrada_invalida", Codigo(await cliente.ChamarAsync("brain_atualizar_contexto_de_trabalho", Atualizacao(1, lista))));
            Assert.Equal(20, Ids(Sucesso(await cliente.ChamarAsync("brain_obter_contexto_de_trabalho", new { }))).Length);
        }
        var deduplicado = Sucesso(await cliente.ChamarAsync("brain_atualizar_contexto_de_trabalho", Atualizacao(1, new[] { itens[0].Id, itens[0].Id })));
        Assert.Equal([itens[0].Id], Ids(deduplicado));
    }

    [PostgreSqlFact]
    public async Task ProjetoNuloESessaoCompativelMantemClassificacaoDoSnapshot()
    {
        await using var c = await Cenario.CriarAsync(semProjeto: true); Assert.Null(c.Acesso.IdProjeto);
        var decisao = await c.AdicionarAsync("decisão de trabalho", classe: Sensibilidade.Trabalho);
        await using var cliente = await c.ClienteAsync("classificacao");
        Assert.Equal("entrada_invalida", Codigo(await cliente.ChamarAsync("brain_atualizar_contexto_de_trabalho", Atualizacao(0, new[] { decisao.Id }))));
        using (var scope = c.EscopoAplicacao())
            await scope.ServiceProvider.GetRequiredService<ContextoDeTrabalhoAppService>().SubstituirAsync(c.Acesso, 0, Dados([]), Sensibilidade.Trabalho, "seleção explícita de teste");
        var salvo = Sucesso(await cliente.ChamarAsync("brain_atualizar_contexto_de_trabalho", Atualizacao(1, new[] { decisao.Id })));
        Assert.Equal([decisao.Id], Ids(salvo)); Assert.Equal("Trabalho", salvo["Sensibilidade"]!.GetValue<string>());
    }

    [PostgreSqlFact]
    public async Task ContextBuilderInjetaReferenciaSemMatchLexicalEDescartaQuandoPerdeConfirmacao()
    {
        await using var c = await Cenario.CriarAsync(); var decisao = await c.AdicionarAsync("Escolha aprovada pelo usuário para o ambiente.");
        await using var cliente = await c.ClienteAsync("contexto");
        Sucesso(await cliente.ChamarAsync("brain_atualizar_contexto_de_trabalho", Atualizacao(0, new[] { decisao.Id })));
        async Task<Dante.Application.ConstrucaoDeContexto.PacoteDeContextoDto> Construir()
        {
            using var scope = c.EscopoAplicacao();
            return await scope.ServiceProvider.GetRequiredService<ConstrutorDeContextoAppService>().ConstruirAsync(c.Acesso,
                new() { Mensagem = "marcadorsemcorrespondencialexical", Filtros = new() { Texto = "marcadorsemcorrespondencialexical" } });
        }
        var antes = await Construir();
        Assert.Contains(antes.Itens, x => x.Id == decisao.Id && x.Status == StatusDoConhecimento.Confirmado && x.Motivo.Contains("referenciada"));
        await using (var db = c.Banco.Contexto())
        {
            var item = await db.Conhecimentos.SingleAsync(x => x.Id == decisao.Id);
            item.Corrigir(item.Revisao, item.Tipo, "Conclusão do agente após revisão.", item.DadosEstruturados, null, item.Sensibilidade,
                null, null, item.Tags, item.Proveniencia, DateTimeOffset.UtcNow, NaturezaDoConteudo.ConclusaoDoAgente);
            await db.SaveChangesAsync();
        }
        var depois = await Construir();
        Assert.DoesNotContain(depois.Itens, x => x.Id == decisao.Id);
        Assert.Contains(depois.Registros, x => x.Id == decisao.Id && x.Motivo.Contains("perdeu confirmação"));
        Assert.DoesNotContain("Conclusão do agente após revisão", depois.TextoParaInjecao);
        Assert.Equal([decisao.Id], Ids(Sucesso(await cliente.ChamarAsync("brain_obter_contexto_de_trabalho", new { }))));
        Assert.Equal("entrada_invalida", Codigo(await cliente.ChamarAsync("brain_atualizar_contexto_de_trabalho", Atualizacao(1, null, informar: false))));
        // Uma referência que ficou inválida pode ser removida; não exige revalidar o ID removido.
        Assert.Empty(Ids(Sucesso(await cliente.ChamarAsync("brain_atualizar_contexto_de_trabalho", Atualizacao(1, Array.Empty<Guid>())))));
    }

    [PostgreSqlFact]
    public async Task DecisaoLiteralConfirmadaNoTelegramEReferenciadaRetornaEmNovaSessao()
    {
        const string literal = "Decidi manter as permissões atuais do ambiente.";
        await using var c = await Cenario.CriarAsync(textoDaMensagem: literal);
        var drivers = new FakeSessionDriverFactory();
        await using var sessoes = new SessionRegistry(drivers, NullLogger<SessionRegistry>.Instance,
            toolServers: new CompositorDeFerramentas([c.Ferramentas]));
        var inicio = await sessoes.StartAsync(new(42, AgentKind.Codex, JobExecutionContext.General(AppContext.BaseDirectory), BrainConversation: c.Mensagem.ParaFerramentas()));
        Assert.True(inicio.Accepted);
        await using var cliente = await BrainMcpTests.Cliente.ConectarAsync(Assert.Single(drivers.Created[0].StartOptions!.ToolServers!));
        var candidato = Sucesso(await cliente.ChamarAsync("brain_capturar_conhecimento", new { conteudo = literal, tipo = "Decisao", natureza = "DitoPeloUsuario", justificativa = "Pedido literal do usuário" }))["candidato"]!;
        Assert.Equal("Decisao", candidato["tipo"]!.GetValue<string>());
        Sucesso(await cliente.ChamarAsync("brain_confirmar_candidato", new { id = candidato["Id"]!.GetValue<string>(), revisao = candidato["Revisao"]!.GetValue<int>() }));
        await using (var db = c.Banco.Contexto()) Assert.Empty(await db.Conhecimentos.ToListAsync());
        Assert.Contains("consolidado", await c.Brain.AtenderAsync(BrainMcpTests.Mensagem("confirmar", 124), "confirmar", inicio.Session!.Id));
        var encontrado = Sucesso(await cliente.ChamarAsync("brain_buscar_conhecimento", new { texto = "permissões", tipo = "Decisao" }))["Resultados"]![0]!["Item"]!;
        Assert.Equal("Confirmado", encontrado["Status"]!.GetValue<string>());
        var id = Guid.Parse(encontrado["Id"]!.GetValue<string>());
        Sucesso(await cliente.ChamarAsync("brain_atualizar_contexto_de_trabalho", Atualizacao(0, new[] { id })));
        var m = BrainMcpTests.Mensagem("continue tarefa operacional", 125);
        var nova = await sessoes.StartAsync(new(42, AgentKind.Claude, JobExecutionContext.General(AppContext.BaseDirectory), BrainConversation: m.ParaFerramentas()));
        Assert.True(nova.Accepted); Assert.NotEqual(inicio.Session.Id, nova.Session!.Id);
        await using var novoCliente = await BrainMcpTests.Cliente.ConectarAsync(Assert.Single(drivers.Created.Last().StartOptions!.ToolServers!));
        Assert.Equal([id], Ids(Sucesso(await novoCliente.ChamarAsync("brain_obter_contexto_de_trabalho", new { }))));
        var contexto = await c.Brain.PrepararAsync(m, nova.Session.Id, m.Text!); Assert.NotNull(contexto);
        Assert.Contains(contexto.Pacote.Itens, x => x.Id == id && x.Motivo.Contains("referenciada"));
        Assert.Contains(literal, contexto.Texto);
        Assert.Equal(SubmitOutcome.TurnStarted, (await sessoes.SubmitAsync(42, nova.Session.Id,
            new AgentInput(contexto.Texto, []) { BrainConversation = m.ParaFerramentas() })).Outcome);
        Assert.Contains(literal, Assert.Single(drivers.Created.Last().TurnInputs).Text);
    }

    private static DadosDoContexto Dados(IReadOnlyList<Guid> ids) => new("objetivo operacional", "tarefa 256", "em andamento", ids, [], [], [], "snapshot selecionado");
    private static Dictionary<string, object?> Atualizacao(int revisao, object? ids, bool informar = true)
    {
        var args = new Dictionary<string, object?> { ["revisao"] = revisao, ["objetivo"] = "objetivo operacional", ["tarefa"] = "tarefa 256",
            ["progresso"] = "em andamento", ["ultimo_resultado"] = "snapshot selecionado" };
        if (informar) args["ids_decisoes_confirmadas"] = ids; return args;
    }
    private static JsonNode Sucesso(JsonObject resposta)
    { Assert.False(resposta["isError"]!.GetValue<bool>(), resposta.ToJsonString()); return JsonNode.Parse(resposta["content"]![0]!["text"]!.GetValue<string>())!; }
    private static string Codigo(JsonObject resposta)
    { Assert.True(resposta["isError"]!.GetValue<bool>()); return JsonNode.Parse(resposta["content"]![0]!["text"]!.GetValue<string>())!["erro"]!.GetValue<string>(); }
    private static Guid[] Ids(JsonNode snapshot) => snapshot["Dados"]!["IdsDecisoesConfirmadas"]!.AsArray().Select(x => Guid.Parse(x!.GetValue<string>())).ToArray();

    private sealed class Cenario(Banco banco, ServiceProvider app, TelegramBrain brain, TelegramBrain.EscopoBrainDaSessao escopo, TelegramMessage mensagem) : IAsyncDisposable
    {
        private readonly FerramentasDoBrainParaAgentes ferramentas = BrainMcpTests.Provedor(app);
        public Banco Banco => banco;
        public ServiceProvider App => app;
        public TelegramBrain Brain => brain;
        public TelegramMessage Mensagem => mensagem;
        public AcessoAoBrain Acesso => escopo.Acesso;
        public FerramentasDoBrainParaAgentes Ferramentas => ferramentas;
        public static async Task<Cenario> CriarAsync(bool semProjeto = false, string textoDaMensagem = "referencie as decisões confirmadas")
        {
            var banco = await Banco.CriarAsync(); var app = BrainMcpTests.CriarApp(banco.ConnectionString);
            var brain = app.GetRequiredService<TelegramBrain>();
            if (semProjeto) await brain.AtenderAsync(BrainMcpTests.Mensagem("criar espaço Desenvolvimento"), "criar espaço Desenvolvimento");
            else await BrainMcpTests.SelecionarAsync(brain);
            var m = BrainMcpTests.Mensagem(textoDaMensagem);
            return new(banco, app, brain, (await brain.ResolverEscopoMcpAsync(m))!, m);
        }
        public async Task<BrainMcpTests.Cliente> ClienteAsync(string id)
        {
            var servidores = await ferramentas.ForSessionAsync(id, new(42, AgentKind.Codex, JobExecutionContext.General(AppContext.BaseDirectory), BrainConversation: mensagem.ParaFerramentas()));
            return await BrainMcpTests.Cliente.ConectarAsync(Assert.Single(servidores));
        }
        public async Task<Conhecimento> AdicionarAsync(string texto, TipoDeConhecimento tipo = TipoDeConhecimento.Decisao, bool confirmado = true,
            NaturezaDoConteudo natureza = NaturezaDoConteudo.DitoPeloUsuario, Sensibilidade classe = Sensibilidade.Pessoal,
            DateTimeOffset? desde = null, DateTimeOffset? ate = null, AcessoAoBrain? acesso = null)
        {
            acesso ??= Acesso; var instante = DateTimeOffset.UtcNow.AddDays(-3); var prova = new ProvenienciaDoConhecimento(acesso.IdUsuario, "teste-256", "fonte:256", trechoDaFonte: texto);
            var k = new Conhecimento(acesso.IdEspacoDeConhecimento, acesso.IdProjeto, tipo, texto, null, StatusDoConhecimento.Inferido,
                null, classe, desde, ate, [], prova, instante, natureza);
            if (confirmado) k.Confirmar(1, prova, instante.AddMinutes(1));
            await using var db = banco.Contexto(); db.Add(k); await db.SaveChangesAsync(); return k;
        }
        public IServiceScope EscopoAplicacao()
        { var scope = app.CreateScope(); scope.ServiceProvider.GetRequiredService<AutorizacaoDoBrain>().Estabelecer(escopo.Identidade, Acesso); return scope; }
        public async ValueTask DisposeAsync() { ferramentas.Dispose(); await app.DisposeAsync(); await banco.DisposeAsync(); }
    }
}
