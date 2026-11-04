using System.Text.Json;
using Dante.Application.BuscaDoBrain;
using Dante.Application.CapturaDeConhecimento;
using Dante.Application.DocumentosFonte;
using Dante.Application.SegurancaDoBrain;
using Dante.Domain.Agentes;
using Dante.Domain.CapturaDeConhecimento;
using Dante.Domain.Conhecimentos;
using Dante.Domain.DocumentosFonte;
using Dante.Domain.EspacosDeConhecimento;
using Dante.Worker.Brain;
using Dante.Worker.Jobs;
using Dante.Worker.Sessions;
using Dante.Worker.Telegram;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json.Nodes;

namespace Dante.Tests;

// #257: evidências de teste/validação registradas pelo agente como fonte bruta (DocumentoFonte), nunca como fato.
public sealed class RegistroDeEvidenciasTests
{
    private const string Ferramenta = "brain_registrar_evidencia";
    private const string Evidencia = "# Resultado da validação 257\n\nSuíte focada: 42 aprovados, 0 falhas. Marcador evidenciasentinela257.";

    [Fact]
    public void SchemaNaoOfereceAutoriaOrigemNemEscopo()
    {
        var f = ServidorMcpDoBrain.Ferramentas.Single(x => x.Nome == Ferramenta);
        Assert.False(f.Leitura); Assert.False(f.Destrutiva);
        Assert.Equal(["conteudo", "justificativa", "sensibilidade", "tags", "titulo"], f.Propriedades.Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.Equal(["titulo", "conteudo"], f.Obrigatorios);
        Assert.Equal(["Publico", "Pessoal", "Trabalho"], f.Propriedades["sensibilidade"]!["enum"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal(DocumentoFonteAppService.TamanhoMaximoDaEvidencia, f.Propriedades["conteudo"]!["maxLength"]!.GetValue<int>());
        Assert.Equal(DocumentoFonte.TamanhoMaximoDoTitulo, f.Propriedades["titulo"]!["maxLength"]!.GetValue<int>());
        Assert.Equal((20_000, 200), (DocumentoFonteAppService.TamanhoMaximoDaEvidencia, DocumentoFonte.TamanhoMaximoDoTitulo));
        foreach (var extra in new[] { "autoria", "origem", "tenant", "usuario", "espaco", "id_projeto", "formato" })
            Assert.Throws<ArgumentException>(() => ServidorMcpDoBrain.Validar(Ferramenta, Args(new Dictionary<string, object> { ["titulo"] = "t", ["conteudo"] = "c", [extra] = "Usuario" })));
        Assert.Throws<ArgumentException>(() => ServidorMcpDoBrain.Validar(Ferramenta, Args(new Dictionary<string, object> { ["conteudo"] = "c" })));
        Assert.Throws<ArgumentException>(() => ServidorMcpDoBrain.Validar(Ferramenta, Args(new Dictionary<string, object> { ["titulo"] = "t" })));
        Assert.Contains(Ferramenta, ContratoDoBrainParaAgentes.Instrucoes);
        Assert.Contains("fonte:<id>@<rev>", f.Descricao);
    }

    [Fact]
    public void MetadadosSaoOpcionaisNormalizadosEForaDoHash()
    {
        var espaco = Guid.NewGuid(); var usuario = Guid.NewGuid(); var agora = DateTimeOffset.UtcNow;
        var legado = new DocumentoFonte(espaco, null, "nota:x", "markdown", "conteúdo", AutoriaDaFonte.Usuario, Sensibilidade.Pessoal, usuario, agora);
        Assert.Null(legado.Titulo); Assert.Empty(legado.Tags); Assert.Null(legado.Justificativa); Assert.Null(legado.Proveniencia);
        var nova = new DocumentoFonte(espaco, null, "evidencia:1", "markdown", "conteúdo", AutoriaDaFonte.Agente, Sensibilidade.Pessoal, usuario, agora,
            " Título ", [" teste-257 ", "TESTE-257", "mcp"], " porque sim ", "MCP/agente:claude;sessao:a:S1");
        Assert.Equal("Título", nova.Titulo); Assert.Equal(["teste-257", "mcp"], nova.Tags); Assert.Equal("porque sim", nova.Justificativa);
        Assert.Equal(legado.Hash, nova.Hash);
        Assert.False(nova.Atualizar(1, "conteúdo", AutoriaDaFonte.Agente, Sensibilidade.Pessoal, usuario, agora));
        Assert.True(nova.Atualizar(1, "outro", AutoriaDaFonte.Agente, Sensibilidade.Pessoal, usuario, agora));
        Assert.Equal("Título", nova.Titulo); Assert.Equal(["teste-257", "mcp"], nova.Tags);
        DocumentoFonte Com(string? titulo = null, IReadOnlyList<string>? tags = null, string? justificativa = null, string? proveniencia = null) =>
            new(espaco, null, "evidencia:2", "markdown", "c", AutoriaDaFonte.Agente, Sensibilidade.Pessoal, usuario, agora, titulo, tags, justificativa, proveniencia);
        Assert.Throws<ArgumentException>(() => Com(titulo: new string('t', 201)));
        Assert.Throws<ArgumentException>(() => Com(titulo: "linha\nquebrada"));
        Assert.Throws<ArgumentException>(() => Com(tags: Enumerable.Range(0, 51).Select(i => $"t{i}").ToArray()));
        Assert.Throws<ArgumentException>(() => Com(tags: [new string('t', 101)]));
        Assert.Throws<ArgumentException>(() => Com(tags: [" "]));
        Assert.Throws<ArgumentException>(() => Com(justificativa: "  "));
        Assert.Throws<ArgumentException>(() => Com(justificativa: new string('j', 2001)));
        Assert.Throws<ArgumentException>(() => Com(proveniencia: new string('p', 501)));
        Assert.Equal(50, Com(tags: Enumerable.Range(0, 50).Select(i => $"t{i}").ToArray()).Tags.Count);
    }

    [Fact]
    public void ContextBuilderAplicaTagsAFontesSemPromoverFiltrosDeConhecimento()
    {
        var agora = DateTimeOffset.UtcNow;
        var comTags = new DocumentoFonte(Guid.NewGuid(), null, "evidencia:x", "markdown", "c", AutoriaDaFonte.Agente, Sensibilidade.Pessoal, Guid.NewGuid(), agora, "t", ["Teste-257", "mcp"]);
        var semTags = new DocumentoFonte(Guid.NewGuid(), null, "nota:x", "markdown", "c", AutoriaDaFonte.Usuario, Sensibilidade.Pessoal, Guid.NewGuid(), agora);
        bool Atende(DocumentoFonte d, BuscaDoBrainSearchDto f) => Dante.Application.ConstrucaoDeContexto.ElegibilidadeDeContexto.AtendeFiltros(d, f);
        Assert.True(Atende(comTags, new() { Texto = "x", Tags = ["teste-257"] }));
        Assert.True(Atende(comTags, new() { Texto = "x", Tags = ["MCP", "teste-257"] }));
        Assert.False(Atende(comTags, new() { Texto = "x", Tags = ["teste-257", "outra"] }));
        Assert.False(Atende(semTags, new() { Texto = "x", Tags = ["teste-257"] }));
        Assert.True(Atende(semTags, new() { Texto = "x" }));
        Assert.False(Atende(comTags, new() { Texto = "x", Tags = ["teste-257"], Tipo = TipoDeConhecimento.Fato }));
        Assert.False(Atende(comTags, new() { Texto = "x", NaturezaDeOrigem = NaturezaDoConteudo.ConclusaoDoAgente }));
    }

    [Theory]
    [InlineData("análise <thinking> interna", true)]
    [InlineData("Chain-of-Thought completo", true)]
    [InlineData("Raciocínio privado do agente", true)]
    [InlineData("Resultado: 10 aprovados", false)]
    public void MarcadoresDeRaciocinioPrivadoSaoOsMesmosDoSnapshot(string texto, bool contem) =>
        Assert.Equal(contem, ProtecaoDeConteudoOperacional.ContemRaciocinioPrivado(texto));

    [PostgreSqlFact]
    public async Task RegistroCriaFonteDoAgenteBuscavelPorTagsSemCandidatoNemConhecimento()
    {
        await using var c = await Cenario.CriarAsync();
        // O usuário pede o registro e a mensagem contém o texto literal: a autoria continua do agente (AD-59 P2).
        var pedido = BrainMcpTests.Mensagem("registre esta evidência: " + Evidencia);
        var resposta = Json(await c.OperarAsync(pedido, "S1", new { titulo = "Validação 257", conteudo = Evidencia, tags = new[] { " Teste-257 ", "teste-257", "mcp" },
            sensibilidade = "Trabalho", justificativa = "Preservar o resultado detalhado da suíte" }));
        var id = resposta["fonte"]!["Id"]!.GetValue<Guid>();
        Assert.Equal($"fonte:{id:D}@1", resposta["referencia"]!.GetValue<string>());
        Assert.False(resposta["possivelDuplicidade"]!.GetValue<bool>()); Assert.False(resposta["confirmado"]!.GetValue<bool>());
        Assert.Equal("Agente", resposta["fonte"]!["autoria"]!.GetValue<string>());
        Assert.Equal("Fonte bruta de autoria do agente, não confirmada", resposta["fonte"]!["Rotulo"]!.GetValue<string>());
        await using (var db = c.Banco.Contexto())
        {
            var fonte = await db.Set<DocumentoFonte>().SingleAsync();
            Assert.Equal(id, fonte.Id); Assert.StartsWith("evidencia:", fonte.Origem); Assert.True(Guid.TryParse(fonte.Origem["evidencia:".Length..], out _));
            Assert.Equal("markdown", fonte.Formato); Assert.Equal(Evidencia, fonte.Conteudo);
            Assert.Equal(AutoriaDaFonte.Agente, fonte.Autoria); Assert.Equal(Sensibilidade.Trabalho, fonte.Sensibilidade);
            Assert.Equal("Validação 257", fonte.Titulo); Assert.Equal(["Teste-257", "mcp"], fonte.Tags);
            Assert.Equal("Preservar o resultado detalhado da suíte", fonte.Justificativa);
            Assert.Equal($"{OperacoesMcpDoBrain.Origem("claude", c.Brain.Instancia, "S1")};conversa:55:0:123", fonte.Proveniencia);
            Assert.Equal(c.Escopo.Acesso.IdUsuario, fonte.IdResponsavel);
            Assert.Empty(await db.CandidatosDeConhecimento.ToListAsync()); Assert.Empty(await db.Conhecimentos.ToListAsync());
        }
        var busca = (BuscaDoBrainDto)(await c.OperarAsync(pedido, "S1", new { texto = "evidenciasentinela257" }, "brain_buscar_conhecimento"))!;
        var achado = Assert.Single(busca.Resultados);
        Assert.Equal(OrigemDoResultado.FonteBruta, achado.Origem); Assert.Equal(AutoriaDaFonte.Agente, achado.Item.AutoriaDaFonte);
        Assert.Equal(["Teste-257", "mcp"], achado.Item.Tags); Assert.Equal(id, achado.Fonte!.IdDocumento);
        Assert.Single(((BuscaDoBrainDto)(await c.OperarAsync(pedido, "S1", new { texto = "evidenciasentinela257", tags = new[] { "TESTE-257", "MCP" } }, "brain_buscar_conhecimento"))!).Resultados);
        Assert.Empty(((BuscaDoBrainDto)(await c.OperarAsync(pedido, "S1", new { texto = "evidenciasentinela257", tags = new[] { "teste-257", "outra" } }, "brain_buscar_conhecimento"))!).Resultados);
        Assert.Empty(((BuscaDoBrainDto)(await c.OperarAsync(pedido, "S1", new { texto = "evidenciasentinela257", tipo = "Fato" }, "brain_buscar_conhecimento"))!).Resultados);
        // Telegram apresenta a fonte como do agente e explica título/proveniência na origem.
        var consulta = await c.Brain.AtenderAsync(BrainMcpTests.Mensagem("busque evidenciasentinela257", 130), "busque evidenciasentinela257");
        Assert.Contains("autoria do agente", consulta); Assert.DoesNotContain("autoria do usuário", consulta);
        var origem = await c.Brain.AtenderAsync(BrainMcpTests.Mensagem("mostre a origem do 1", 131), "mostre a origem do 1");
        Assert.Contains("Título: Validação 257", origem); Assert.Contains("Registrada por: MCP/agente:claude", origem); Assert.Contains("autoria do agente", origem);
        using var s = c.Scope();
        var listada = Assert.Single(await s.ServiceProvider.GetRequiredService<DocumentoFonteAppService>().ListarAsync(c.Escopo.Acesso));
        Assert.Equal(("Validação 257", "Preservar o resultado detalhado da suíte"), (listada.Titulo, listada.Justificativa));
        Assert.StartsWith(OperacoesMcpDoBrain.Origem("claude", c.Brain.Instancia, "S1"), listada.Proveniencia);
    }

    [PostgreSqlFact]
    public async Task ConteudoRepetidoPreservaCadaOcorrenciaESoInformaDuplicidadeVisivel()
    {
        await using var c = await Cenario.CriarAsync();
        var primeira = Json(await c.OperarAsync(BrainMcpTests.Mensagem("registre", 123), "S1", new { titulo = "Primeira", conteudo = Evidencia, tags = new[] { "a" }, justificativa = "primeira execução" }));
        var refPrimeira = primeira["referencia"]!.GetValue<string>();
        // Fonte protegida com o mesmo conteúdo não pode ser revelada como duplicidade.
        await using (var db = c.Banco.Contexto())
        {
            db.Add(new DocumentoFonte(c.Escopo.Acesso.IdEspacoDeConhecimento, c.Escopo.Acesso.IdProjeto, "nota:protegida", "markdown", Evidencia,
                AutoriaDaFonte.Usuario, Sensibilidade.Confidencial, c.Escopo.Acesso.IdUsuario, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }
        var segunda = Json(await c.OperarAsync(BrainMcpTests.Mensagem("registre de novo", 124), "S2", new { titulo = "Segunda", conteudo = Evidencia, tags = new[] { "b" }, justificativa = "reexecução", sensibilidade = "Publico" }, agente: "codex"));
        Assert.True(segunda["possivelDuplicidade"]!.GetValue<bool>());
        Assert.Equal([refPrimeira], segunda["duplicidades"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.NotEqual(refPrimeira, segunda["referencia"]!.GetValue<string>());
        var diferente = Json(await c.OperarAsync(BrainMcpTests.Mensagem("outra", 125), "S1", new { titulo = "Outra", conteudo = Evidencia + " Variação." }));
        Assert.False(diferente["possivelDuplicidade"]!.GetValue<bool>());
        await using (var db = c.Banco.Contexto())
        {
            var evidencias = await db.Set<DocumentoFonte>().Where(x => x.Origem.StartsWith("evidencia:")).OrderBy(x => x.AtualizadoEm).ToListAsync();
            Assert.Equal(3, evidencias.Count); Assert.Equal(3, evidencias.Select(x => x.Origem).Distinct().Count());
            Assert.Equal(evidencias[0].Hash, evidencias[1].Hash);
            Assert.Equal(("Primeira", "a", "primeira execução", Sensibilidade.Pessoal), (evidencias[0].Titulo, evidencias[0].Tags.Single(), evidencias[0].Justificativa, evidencias[0].Sensibilidade));
            Assert.Equal(("Segunda", "b", "reexecução", Sensibilidade.Publico), (evidencias[1].Titulo, evidencias[1].Tags.Single(), evidencias[1].Justificativa, evidencias[1].Sensibilidade));
            Assert.Equal($"{OperacoesMcpDoBrain.Origem("claude", c.Brain.Instancia, "S1")};conversa:55:0:123", evidencias[0].Proveniencia);
            Assert.Equal($"{OperacoesMcpDoBrain.Origem("codex", c.Brain.Instancia, "S2")};conversa:55:0:124", evidencias[1].Proveniencia);
            Assert.Null(evidencias[2].Justificativa); Assert.Empty(evidencias[2].Tags);
            Assert.All(evidencias, x => { Assert.Equal(AutoriaDaFonte.Agente, x.Autoria); Assert.Equal(1, x.Revisao); });
        }
    }

    [PostgreSqlFact]
    public async Task RecusasDeSegurancaLimitesEEscopoNaoPersistemNada()
    {
        await using var c = await Cenario.CriarAsync(); var m = BrainMcpTests.Mensagem("registre a evidência");
        async Task Recusa(object args, CategoriaDeErroMcp categoria = CategoriaDeErroMcp.EntradaInvalida, string? detalhe = null, string sessao = "S1")
        {
            var resposta = await BrainMcpTests.RecusaAsync(categoria, () => c.OperarAsync(m, sessao, args, registrar: sessao == "S1"));
            if (detalhe is not null) Assert.Equal(detalhe, BrainMcpTests.DetalheDe(resposta));
        }
        foreach (var credencial in new object[] {
            new { titulo = "t", conteudo = "Resultado ok. senha=SegredoSintetico257" }, new { titulo = "token: abcdef123456", conteudo = Evidencia },
            new { titulo = "t", conteudo = Evidencia, tags = new[] { "api_key=abcd1234" } }, new { titulo = "t", conteudo = Evidencia, justificativa = "postgres://u:senha@host/db" },
            new { titulo = "t", conteudo = "ghp_" + new string('a', 30) } })
            await Recusa(credencial, detalhe: ErrosDoBrainMcp.DetalheCredencial);
        foreach (var privado in new object[] {
            new { titulo = "t", conteudo = "Resumo\n<thinking>passo interno</thinking>" }, new { titulo = "chain-of-thought", conteudo = Evidencia },
            new { titulo = "t", conteudo = Evidencia, justificativa = "contém raciocínio privado" }, new { titulo = "t", conteudo = Evidencia, tags = new[] { "<thinking>" } } })
            await Recusa(privado, detalhe: ErrosDoBrainMcp.DetalheConteudoOperacional);
        foreach (var classe in new[] { "Confidencial", "Secreto" })
            await Recusa(new { titulo = "t", conteudo = Evidencia, sensibilidade = classe }, detalhe: ErrosDoBrainMcp.DetalheSensibilidade);
        foreach (var invalido in new object[] {
            new { titulo = "t", conteudo = Evidencia, autoria = "Usuario" }, new { titulo = "t", conteudo = Evidencia, origem = "nota:usuario" },
            new { titulo = "t", conteudo = Evidencia, id_projeto = Guid.NewGuid() }, new { titulo = "t", conteudo = Evidencia, tenant = "outro" },
            new { conteudo = Evidencia }, new { titulo = "t" }, new { titulo = "t", conteudo = 42 }, new { titulo = "t", conteudo = "" },
            new { titulo = "t", conteudo = new string('x', DocumentoFonteAppService.TamanhoMaximoDaEvidencia + 1) },
            new { titulo = new string('t', 201), conteudo = Evidencia }, new { titulo = "t", conteudo = Evidencia, tags = "teste" },
            new { titulo = "t", conteudo = Evidencia, tags = Enumerable.Range(0, 51).Select(i => $"t{i}").ToArray() },
            new { titulo = "t", conteudo = Evidencia, tags = new[] { new string('t', 101) } }, new { titulo = "t", conteudo = Evidencia, justificativa = "" },
            new { titulo = "t", conteudo = Evidencia, justificativa = new string('j', 2001) }, new { titulo = "t", conteudo = Evidencia, sensibilidade = "Desconhecida" } })
            await Recusa(invalido);
        // Sessão sem registro/autorização na conversa e escopo trocado falham fechado.
        await Recusa(new { titulo = "t", conteudo = Evidencia }, CategoriaDeErroMcp.EscopoMudou, sessao: "S-desconhecida");
        await using (var db = c.Banco.Contexto()) Assert.Empty(await db.Set<DocumentoFonte>().ToListAsync());
        Assert.False(Json(await c.OperarAsync(m, "S1", new { titulo = "Limite", conteudo = new string('x', DocumentoFonteAppService.TamanhoMaximoDaEvidencia) }))["possivelDuplicidade"]!.GetValue<bool>());
        c.App.GetRequiredService<RegistroDeOperacoesBrain>().Revogar("S1");
        await BrainMcpTests.RecusaAsync(CategoriaDeErroMcp.EscopoMudou, () => c.OperarAsync(m, "S1", new { titulo = "t", conteudo = Evidencia }, registrar: false));
        await using (var db = c.Banco.Contexto()) Assert.Single(await db.Set<DocumentoFonte>().ToListAsync());
        // Application: autoria fixa, sensibilidade de agente e acesso autorizado, mesmo sem o adapter MCP.
        var outro = new EspacoDeConhecimento(Guid.NewGuid(), "Alheio");
        await using (var db = c.Banco.Contexto()) { db.Add(outro); await db.SaveChangesAsync(); }
        using var s = c.Scope(); var app = s.ServiceProvider.GetRequiredService<DocumentoFonteAppService>();
        var valida = new RegistroDeEvidenciaDto("t", Evidencia, [], Sensibilidade.Pessoal, null, "MCP/agente:claude;sessao:x:S1");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => app.RegistrarEvidenciaAsync(new AcessoAoBrain(outro.IdUsuario, outro.Id, null), valida));
        await Assert.ThrowsAsync<ArgumentException>(() => app.RegistrarEvidenciaAsync(c.Escopo.Acesso, valida with { Sensibilidade = Sensibilidade.Confidencial }));
        await Assert.ThrowsAsync<ArgumentException>(() => app.RegistrarEvidenciaAsync(c.Escopo.Acesso, valida with { Proveniencia = " " }));
        await Assert.ThrowsAsync<ArgumentException>(() => app.RegistrarEvidenciaAsync(c.Escopo.Acesso, valida with { Conteudo = "x <thinking>" }));
        await Assert.ThrowsAsync<ArgumentException>(() => app.RegistrarEvidenciaAsync(c.Escopo.Acesso, valida with { Justificativa = "senha=abcd12345" }));
    }

    [PostgreSqlFact]
    public async Task EvidenciaPersisteEContinuaBrutaDepoisDoReinicioDoWorker()
    {
        await using var banco = await Banco.CriarAsync(); string referencia; string instanciaAnterior;
        using (var anterior = BrainMcpTests.CriarApp(banco.ConnectionString))
        {
            var brain = anterior.GetRequiredService<TelegramBrain>(); await BrainMcpTests.SelecionarAsync(brain); instanciaAnterior = brain.Instancia;
            var m = BrainMcpTests.Mensagem("registre a evidência", 121); anterior.GetRequiredService<RegistroDeOperacoesBrain>().RegistrarSessao("S000001", m);
            referencia = Json(await anterior.GetRequiredService<OperacoesMcpDoBrain>().ExecutarAsync((await brain.ResolverEscopoMcpAsync(m))!, m, "S000001", "claude", Ferramenta,
                JsonSerializer.SerializeToElement(new { titulo = "Antes do reinício", conteudo = Evidencia, tags = new[] { "teste-257" }, justificativa = "continuidade" })))["referencia"]!.GetValue<string>();
        }
        // Reinício simulado: novo processo, mesmo banco, contador de sessões recomeça em S000001 (#249).
        using var atual = BrainMcpTests.CriarApp(banco.ConnectionString); var novo = atual.GetRequiredService<TelegramBrain>();
        Assert.NotEqual(instanciaAnterior, novo.Instancia);
        Assert.Contains("selecionado", await novo.AtenderAsync(BrainMcpTests.Mensagem("usar espaço Desenvolvimento"), "usar espaço Desenvolvimento"));
        Assert.Contains("selecionado", await novo.AtenderAsync(BrainMcpTests.Mensagem("usar projeto D.A.N.T.E."), "usar projeto D.A.N.T.E."));
        var pedido = BrainMcpTests.Mensagem("retome", 122); atual.GetRequiredService<RegistroDeOperacoesBrain>().RegistrarSessao("S000001", pedido);
        var escopo = (await novo.ResolverEscopoMcpAsync(pedido))!; var ops = atual.GetRequiredService<OperacoesMcpDoBrain>();
        var busca = (BuscaDoBrainDto)(await ops.ExecutarAsync(escopo, pedido, "S000001", "codex", "brain_buscar_conhecimento",
            JsonSerializer.SerializeToElement(new { texto = "evidenciasentinela257", tags = new[] { "teste-257" } })))!;
        var achado = Assert.Single(busca.Resultados);
        Assert.Equal(OrigemDoResultado.FonteBruta, achado.Origem); Assert.NotEqual(StatusDoConhecimento.Confirmado, achado.Item.Status);
        Assert.Equal(AutoriaDaFonte.Agente, achado.Item.AutoriaDaFonte); Assert.Equal($"fonte:{achado.Item.Id:D}@1", referencia);
        await ops.ExecutarAsync(escopo, pedido, "S000001", "codex", "brain_atualizar_contexto_de_trabalho", JsonSerializer.SerializeToElement(new { revisao = 0,
            objetivo = "retomar", tarefa = "issue 257", progresso = "após reinício", ultimo_resultado = "evidência preservada", referencias = new[] { referencia } }));
        using var s = EscoposBrainDeTeste.Criar(atual, escopo.Acesso.IdUsuario, escopo.Acesso.IdEspacoDeConhecimento, escopo.Acesso.IdProjeto);
        var fonte = Assert.Single(await s.ServiceProvider.GetRequiredService<DocumentoFonteAppService>().ListarAsync(escopo.Acesso));
        Assert.Equal(("Antes do reinício", "continuidade", AutoriaDaFonte.Agente, 1), (fonte.Titulo, fonte.Justificativa, fonte.Autoria, fonte.Revisao));
        Assert.Equal(["teste-257"], fonte.Tags);
        Assert.Equal($"{OperacoesMcpDoBrain.Origem("claude", instanciaAnterior, "S000001")};conversa:55:0:121", fonte.Proveniencia);
        var pacote = await s.ServiceProvider.GetRequiredService<Dante.Application.ConstrucaoDeContexto.ConstrutorDeContextoAppService>().ConstruirAsync(escopo.Acesso,
            new() { Mensagem = "marcadorsemcorrespondencia257", Filtros = new() { Texto = "marcadorsemcorrespondencia257" }, OrcamentoDeTokens = 30000, LimiteDeItens = 20, JaInjetados = new Dictionary<string, int>() });
        var item = Assert.Single(pacote.Itens, x => x.Id == fonte.Id);
        Assert.Equal(("fonte_bruta", 1, Evidencia), (item.Origem, item.Revisao, item.Conteudo)); Assert.Null(item.Status);
        await using var db = banco.Contexto(); Assert.Empty(await db.Conhecimentos.ToListAsync()); Assert.Empty(await db.CandidatosDeConhecimento.ToListAsync());
    }

    [PostgreSqlFact]
    public async Task CandidatoDaEvidenciaPermaneceInferidoEConfirmacaoNaoMudaAutoriaDaFonte()
    {
        await using var c = await Cenario.CriarAsync();
        var resposta = Json(await c.OperarAsync(BrainMcpTests.Mensagem("registre a evidência"), "S1", new { titulo = "Para candidato", conteudo = Evidencia }));
        var id = resposta["fonte"]!["Id"]!.GetValue<Guid>(); DocumentoFonte antes;
        await using (var db = c.Banco.Contexto()) antes = await db.Set<DocumentoFonte>().AsNoTracking().SingleAsync();
        using var s = c.Scope();
        var candidato = await s.ServiceProvider.GetRequiredService<DocumentoFonteAppService>().GerarCandidatoAsync(c.Escopo.Acesso, id, 1, 0);
        Assert.Equal(NaturezaDoConteudo.ConclusaoDoAgente, candidato.Natureza); Assert.Equal(EstadoDoCandidato.Pendente, candidato.Estado);
        var captura = s.ServiceProvider.GetRequiredService<ICapturaDeConhecimentoAppService>();
        var conhecimento = await captura.ConfirmarAsync(c.Escopo.Acesso.IdEspacoDeConhecimento, c.Escopo.Acesso.IdProjeto, candidato.Id, candidato.Revisao,
            new() { IdResponsavel = c.Escopo.Acesso.IdUsuario, Origem = "confirmação Telegram", ReferenciaDaFonte = "conversa:55:200", TrechoDaFonte = "confirmar" });
        await using var verificar = c.Banco.Contexto();
        Assert.Equal(StatusDoConhecimento.Inferido, (await verificar.Conhecimentos.SingleAsync(x => x.Id == conhecimento)).Status);
        var depois = await verificar.Set<DocumentoFonte>().SingleAsync();
        Assert.Equal((AutoriaDaFonte.Agente, antes.Revisao, antes.Hash, antes.Proveniencia, antes.Titulo, antes.AtualizadoEm.ToUnixTimeMilliseconds()),
            (depois.Autoria, depois.Revisao, depois.Hash, depois.Proveniencia, depois.Titulo, depois.AtualizadoEm.ToUnixTimeMilliseconds()));
    }

    [PostgreSqlFact]
    public async Task EvidenciaDaSessaoAEntraNaSessaoBComoFonteBrutaNaRevisaoReferenciada()
    {
        await using var c = await Cenario.CriarAsync();
        using var ferramentas = BrainMcpTests.Provedor(c.App); var drivers = new FakeSessionDriverFactory();
        await using var sessoes = new SessionRegistry(drivers, NullLogger<SessionRegistry>.Instance, toolServers: new CompositorDeFerramentas([ferramentas]));
        var m = BrainMcpTests.Mensagem("registre a evidência dos testes");
        var a = await sessoes.StartAsync(new(42, AgentKind.Codex, JobExecutionContext.General(AppContext.BaseDirectory), BrainConversation: m.ParaFerramentas())); Assert.True(a.Accepted);
        string referencia;
        await using (var clienteA = await BrainMcpTests.Cliente.ConectarAsync(Assert.Single(drivers.Created[0].StartOptions!.ToolServers!)))
        {
            var registrada = await clienteA.ChamarAsync(Ferramenta, new { titulo = "Suíte focada", conteudo = Evidencia, tags = new[] { "teste-257" } });
            Assert.False(registrada["isError"]!.GetValue<bool>());
            referencia = JsonNode.Parse(registrada["content"]![0]!["text"]!.GetValue<string>())!["referencia"]!.GetValue<string>();
            var salvo = await clienteA.ChamarAsync("brain_atualizar_contexto_de_trabalho", new { revisao = 0, objetivo = "validar evidências", tarefa = "issue 257",
                progresso = "evidência registrada", ultimo_resultado = "suíte focada verde", referencias = new[] { referencia } });
            Assert.False(salvo["isError"]!.GetValue<bool>());
        }
        Guid id; await using (var db = c.Banco.Contexto()) id = (await db.Set<DocumentoFonte>().SingleAsync()).Id;
        Assert.Equal($"fonte:{id:D}@1", referencia);
        var nova = BrainMcpTests.Mensagem("retome o trabalho", 124);
        var b = await sessoes.StartAsync(new(42, AgentKind.Claude, JobExecutionContext.General(AppContext.BaseDirectory), BrainConversation: nova.ParaFerramentas())); Assert.True(b.Accepted);
        var contexto = await c.Brain.PrepararAsync(nova, b.Session!.Id, nova.Text!); Assert.NotNull(contexto);
        var item = Assert.Single(contexto.Pacote.Itens, x => x.Id == id);
        Assert.Equal(("fonte_bruta", 1, AutoriaDaFonte.Agente, Evidencia), (item.Origem, item.Revisao, item.AutoriaDaFonte, item.Conteudo));
        Assert.Null(item.Status); Assert.Equal("Fonte bruta de autoria do agente, não confirmada", item.Rotulo);
        // A revisão fixa o conteúdo: uma revisão nova sem a referência correspondente não é injetada.
        await using (var db = c.Banco.Contexto())
        {
            var fonte = await db.Set<DocumentoFonte>().SingleAsync();
            fonte.Atualizar(1, "Conteúdo posterior não referenciado evidenciasentinela257", AutoriaDaFonte.Agente, fonte.Sensibilidade, fonte.IdResponsavel, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }
        var divergente = await c.Brain.PrepararAsync(nova, b.Session.Id, nova.Text!); Assert.NotNull(divergente);
        Assert.DoesNotContain(divergente.Pacote.Itens, x => x.Id == id);
        Assert.Contains(divergente.Pacote.Registros, x => x.Id == id && x.Motivo.Contains("Revisão da referência diverge"));
        Assert.DoesNotContain("Conteúdo posterior não referenciado", divergente.Texto);
    }

    [PostgreSqlFact]
    public async Task MigrationDeixaFontesAnterioresNeutrasEImportacaoTelegramNaoMuda()
    {
        await using var banco = await Banco.CriarAsync(); var espaco = new EspacoDeConhecimento(Guid.NewGuid(), "Legado");
        var fonte = new DocumentoFonte(espaco.Id, null, "nota:legado", "markdown", "# Texto anterior", AutoriaDaFonte.Usuario,
            Sensibilidade.Pessoal, espaco.IdUsuario, DateTimeOffset.UtcNow, "título", ["tag"], "justificativa", "proveniência");
        await using (var db = banco.Contexto())
        {
            db.AddRange(espaco, fonte); await db.SaveChangesAsync();
            var migrator = db.GetService<IMigrator>(); var anterior = db.Database.GetMigrations().Last(x => string.CompareOrdinal(x, Migracao(db)) < 0);
            await migrator.MigrateAsync(anterior); await migrator.MigrateAsync();
        }
        await using (var verificar = banco.Contexto())
        {
            var legado = await verificar.Set<DocumentoFonte>().SingleAsync();
            Assert.Null(legado.Titulo); Assert.Empty(legado.Tags); Assert.Null(legado.Justificativa); Assert.Null(legado.Proveniencia);
            Assert.Equal((AutoriaDaFonte.Usuario, fonte.Conteudo, fonte.Hash, fonte.Revisao, fonte.Origem), (legado.Autoria, legado.Conteudo, legado.Hash, legado.Revisao, legado.Origem));
        }
        // Importação natural do Telegram: mesma origem nota:<Nome>, autoria do usuário, sem metadados de evidência.
        using var app = BrainMcpTests.CriarApp(banco.ConnectionString); var brain = app.GetRequiredService<TelegramBrain>(); await BrainMcpTests.SelecionarAsync(brain);
        Assert.Contains("Fonte bruta adicionada", await brain.AtenderAsync(BrainMcpTests.Mensagem("importe nota Deploy: reiniciar o worker após migrar"), "importe nota Deploy: reiniciar o worker após migrar"));
        await using (var db = banco.Contexto())
        {
            var nota = await db.Set<DocumentoFonte>().SingleAsync(x => x.Origem.StartsWith("nota:") && x.Origem != "nota:legado");
            Assert.Equal(AutoriaDaFonte.Usuario, nota.Autoria); Assert.Null(nota.Titulo); Assert.Empty(nota.Tags); Assert.Null(nota.Justificativa); Assert.Null(nota.Proveniencia);
        }
        Assert.Contains("já existe", await brain.AtenderAsync(BrainMcpTests.Mensagem("importe nota Deploy: reiniciar o worker depois de migrar"), "importe nota Deploy: reiniciar o worker depois de migrar"));
        static string Migracao(DbContext db) => db.Database.GetMigrations().Single(x => x.EndsWith("_MetadadosDasFontes", StringComparison.Ordinal));
    }

    private static JsonElement Args(Dictionary<string, object> valores) => JsonSerializer.SerializeToElement(valores);
    private static JsonNode Json(object? resultado) => JsonNode.Parse(JsonSerializer.Serialize(resultado))!;

    private sealed class Cenario(Banco banco, ServiceProvider app, TelegramBrain brain, TelegramBrain.EscopoBrainDaSessao escopo) : IAsyncDisposable
    {
        public Banco Banco => banco; public ServiceProvider App => app; public TelegramBrain Brain => brain; public TelegramBrain.EscopoBrainDaSessao Escopo => escopo;
        public static async Task<Cenario> CriarAsync()
        {
            var banco = await Banco.CriarAsync(); var app = BrainMcpTests.CriarApp(banco.ConnectionString); var brain = app.GetRequiredService<TelegramBrain>();
            await BrainMcpTests.SelecionarAsync(brain);
            return new(banco, app, brain, (await brain.ResolverEscopoMcpAsync(BrainMcpTests.Mensagem("escopo")))!);
        }
        public IServiceScope Scope() => EscoposBrainDeTeste.Criar(app, escopo.Acesso.IdUsuario, escopo.Acesso.IdEspacoDeConhecimento, escopo.Acesso.IdProjeto);
        public Task<object?> OperarAsync(TelegramMessage mensagem, string sessao, object args, string ferramenta = Ferramenta, string agente = "claude", bool registrar = true)
        {
            if (registrar) app.GetRequiredService<RegistroDeOperacoesBrain>().RegistrarSessao(sessao, mensagem);
            return app.GetRequiredService<OperacoesMcpDoBrain>().ExecutarAsync(escopo, mensagem, sessao, agente, ferramenta, JsonSerializer.SerializeToElement(args));
        }
        public async ValueTask DisposeAsync() { await app.DisposeAsync(); await banco.DisposeAsync(); }
    }
}
