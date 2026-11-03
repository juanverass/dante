using System.Text.Json.Nodes;
using Dante.Application.BuscaDoBrain;
using Dante.Application.ConstrucaoDeContexto;
using Dante.Application.ContextosDeTrabalho;
using Dante.Application.SegurancaDoBrain;
using Dante.Domain.Agentes;
using Dante.Domain.CapturaDeConhecimento;
using Dante.Domain.Conhecimentos;
using Dante.Domain.ContextosDeTrabalho;
using Dante.Domain.DocumentosFonte;
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

public sealed class ReferenciasTipadasNoContextoTests
{
    [Theory]
    [InlineData("conhecimento", 1)]
    [InlineData("candidato", 2)]
    [InlineData("fonte", int.MaxValue)]
    public void ParserReconheceFormatosComIdERevisaoPositiva(string tipo, int revisao)
    {
        var id = Guid.NewGuid(); var r = ReferenciaTipadaDeContexto.Ler($"{tipo}:{id:D}@{revisao}");
        Assert.NotNull(r); Assert.Equal(tipo, r.Tipo); Assert.Equal(id, r.Id); Assert.Equal(revisao, r.Revisao);
    }

    [Theory]
    [InlineData("nota livre")]
    [InlineData("conhecimento:nao-guid@1")]
    [InlineData("candidato:00000000-0000-0000-0000-000000000000@1")]
    [InlineData("fonte:11111111-1111-4111-8111-111111111111@0")]
    [InlineData("fonte:11111111-1111-4111-8111-111111111111@-1")]
    [InlineData("fonte:11111111-1111-4111-8111-111111111111@999999999999")]
    [InlineData("fonte:11111111-1111-4111-8111-111111111111")]
    [InlineData("outro:11111111-1111-4111-8111-111111111111@1")]
    public void ReferenciaMalformadaPermaneceTextoLivre(string texto) => Assert.Null(ReferenciaTipadaDeContexto.Ler(texto));

    [PostgreSqlFact]
    public async Task SnapshotAceitaTresFormatosTextoLivreEMalformadosSemAlterarStrings()
    {
        await using var c = await Cenario.CriarAsync();
        var referencias = new[] { $"conhecimento:{Guid.NewGuid()}@1", $"candidato:{Guid.NewGuid()}@2", $"fonte:{Guid.NewGuid()}@3",
            "nota livre compatível", "candidato:nao-guid@1", "fonte:incompleta" };
        await c.SnapshotAsync(referencias);
        using (var scope = c.Scope())
            Assert.Equal(referencias, (await scope.ServiceProvider.GetRequiredService<ContextoDeTrabalhoAppService>().RetomarAsync(c.Acesso))!.Dados.Referencias);
        var pacote = await c.ConstruirAsync();
        var snapshot = Assert.Single(pacote.Itens, x => x.Origem == "snapshot");
        Assert.Contains("nota livre compatível", snapshot.Conteudo); Assert.Contains("candidato:nao-guid@1", snapshot.Conteudo);
        Assert.Equal(3, pacote.Registros.Count(x => x.Estado == "descartado" && x.Motivo.Contains("Referência indisponível")));
        Assert.DoesNotContain(referencias[0], snapshot.Conteudo);
    }

    [PostgreSqlFact]
    public async Task TresTiposResolvemComNaturezaERotulosSemPromoverCandidatoOuFonte()
    {
        await using var c = await Cenario.CriarAsync(); var (k, p, f) = await c.TrioAsync();
        await c.SnapshotAsync(Refs(k, p, f)); var pacote = await c.ConstruirAsync();
        var conhecimento = Assert.Single(pacote.Itens, x => x.Id == k.Id);
        Assert.Equal(StatusDoConhecimento.Confirmado, conhecimento.Status); Assert.Equal("Confirmado", conhecimento.Rotulo);
        var candidato = Assert.Single(pacote.Itens, x => x.Id == p.Id);
        Assert.Equal("candidato", candidato.Origem); Assert.Null(candidato.Status); Assert.Null(candidato.Tipo);
        Assert.Equal("Pendente, não confirmado", candidato.Rotulo); Assert.Equal(p.Natureza, candidato.Natureza);
        var fonte = Assert.Single(pacote.Itens, x => x.Id == f.Id);
        Assert.Equal("fonte_bruta", fonte.Origem); Assert.Null(fonte.Status); Assert.Null(fonte.Tipo);
        Assert.Equal(AutoriaDaFonte.Agente, fonte.AutoriaDaFonte); Assert.Contains("não confirmada", fonte.Rotulo);
        Assert.All(new[] { conhecimento, candidato, fonte }, x => Assert.True(x.Referenciado));
        Assert.Contains("\"Origem\":\"candidato\"", pacote.TextoParaInjecao);
        Assert.Contains(p.Conteudo, pacote.TextoParaInjecao); Assert.Contains(f.Conteudo, pacote.TextoParaInjecao);
    }

    [PostgreSqlFact]
    public async Task RevisaoDivergenteBloqueiaConteudoNovoMesmoQuandoBuscaTambemEncontra()
    {
        await using var c = await Cenario.CriarAsync(); var (k, p, f) = await c.TrioAsync(); var referencias = Refs(k, p, f);
        await c.SnapshotAsync(referencias);
        await using (var db = c.Banco.Contexto())
        {
            var atual = await db.Conhecimentos.SingleAsync(x => x.Id == k.Id);
            atual.Corrigir(atual.Revisao, atual.Tipo, "sentinela255 conhecimento novo", null, null, atual.Sensibilidade, null, null, [], atual.Proveniencia,
                DateTimeOffset.UtcNow, NaturezaDoConteudo.ConclusaoDoAgente);
            var pendente = await db.CandidatosDeConhecimento.SingleAsync(x => x.Id == p.Id);
            pendente.Corrigir(pendente.Revisao, pendente.Tipo, "sentinela255 candidato novo", pendente.Sensibilidade, "teste", pendente.Proveniencia, DateTimeOffset.UtcNow);
            var fonte = await db.Set<DocumentoFonte>().SingleAsync(x => x.Id == f.Id);
            fonte.Atualizar(fonte.Revisao, "sentinela255 fonte nova", fonte.Autoria, fonte.Sensibilidade, c.Acesso.IdUsuario, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }
        using (var scope = c.Scope())
        {
            var busca = await scope.ServiceProvider.GetRequiredService<BuscaDoBrainAppService>().BuscarAsync(c.Acesso, new() { Texto = "sentinela255" });
            Assert.Contains(busca.Resultados, x => x.Item.Id == k.Id); Assert.Contains(busca.Resultados, x => x.Item.Id == f.Id);
        }
        var pacote = await c.ConstruirAsync("sentinela255");
        Assert.DoesNotContain(pacote.Itens, x => x.Id == k.Id || x.Id == p.Id || x.Id == f.Id);
        Assert.Equal(3, pacote.Registros.Count(x => x.Motivo.Contains("Revisão da referência diverge")));
        Assert.DoesNotContain("conhecimento novo", pacote.TextoParaInjecao); Assert.DoesNotContain("fonte nova", pacote.TextoParaInjecao);
    }

    [PostgreSqlFact]
    public async Task EstadosEncerradosRemovidosOuInativosNaoSaoInjetados()
    {
        await using var c = await Cenario.CriarAsync();
        foreach (var estado in new[] { EstadoDoCandidato.Rejeitado, EstadoDoCandidato.Descartado, EstadoDoCandidato.Promovido })
        {
            var (k, p, f) = await c.TrioAsync(); await c.SnapshotAsync(Refs(k, p, f));
            await using (var db = c.Banco.Contexto())
            {
                var conhecimento = await db.Conhecimentos.SingleAsync(x => x.Id == k.Id); conhecimento.Invalidar(conhecimento.Revisao, conhecimento.Proveniencia, DateTimeOffset.UtcNow);
                var candidato = await db.CandidatosDeConhecimento.SingleAsync(x => x.Id == p.Id);
                if (estado == EstadoDoCandidato.Promovido) db.Add(candidato.Promover(candidato.Revisao, candidato.Proveniencia, DateTimeOffset.UtcNow));
                else if (estado == EstadoDoCandidato.Rejeitado) candidato.Rejeitar(candidato.Revisao, candidato.Proveniencia, DateTimeOffset.UtcNow);
                else candidato.Descartar(candidato.Revisao, candidato.Proveniencia, DateTimeOffset.UtcNow);
                var fonte = await db.Set<DocumentoFonte>().SingleAsync(x => x.Id == f.Id); fonte.Remover(fonte.Revisao, c.Acesso.IdUsuario, DateTimeOffset.UtcNow);
                await db.SaveChangesAsync();
            }
            var pacote = await c.ConstruirAsync();
            Assert.DoesNotContain(pacote.Itens, x => x.Id == k.Id || x.Id == p.Id || x.Id == f.Id);
            Assert.Equal(3, pacote.Registros.Count(x => x.Motivo == ResolucaoDeReferenciasDeContexto.Indisponivel));
        }
    }

    [PostgreSqlFact]
    public async Task EscopoSensibilidadeEConteudoProtegidoSaoRevalidadosParaCadaTipo()
    {
        await using var c = await Cenario.CriarAsync();
        var externo = new EspacoDeConhecimento(Guid.NewGuid(), "Outro usuário"); var projeto = new Projeto(c.Acesso.IdEspacoDeConhecimento, "Outro projeto");
        await using (var db = c.Banco.Contexto()) { db.AddRange(externo, projeto); await db.SaveChangesAsync(); }
        foreach (var (acesso, classe, protegido) in new[] {
            (new AcessoAoBrain(externo.IdUsuario, externo.Id, null), Sensibilidade.Pessoal, false),
            (c.Acesso with { IdProjeto = projeto.Id }, Sensibilidade.Pessoal, false),
            (c.Acesso with { IdProjeto = null }, Sensibilidade.Pessoal, false),
            (c.Acesso, Sensibilidade.Confidencial, false), (c.Acesso, Sensibilidade.Secreto, false),
            (c.Acesso, Sensibilidade.Pessoal, true) })
        {
            var (k, p, f) = await c.TrioAsync(acesso, classe, protegido); await c.SnapshotAsync(Refs(k, p, f));
            var pacote = await c.ConstruirAsync();
            Assert.DoesNotContain(pacote.Itens, x => x.Id == k.Id || x.Id == p.Id || x.Id == f.Id);
            Assert.Equal(3, pacote.Registros.Count(x => x.Motivo == ResolucaoDeReferenciasDeContexto.Indisponivel));
            Assert.DoesNotContain("credencial-sintetica255", pacote.TextoParaInjecao);
            if (classe == Sensibilidade.Confidencial)
                Assert.Equal(3, (await c.ConstruirAsync(confidencial: true)).Itens.Count(x => x.Id == k.Id || x.Id == p.Id || x.Id == f.Id));
            if (classe == Sensibilidade.Secreto)
                Assert.DoesNotContain((await c.ConstruirAsync(confidencial: true, secreto: true)).Itens, x => x.Id == k.Id || x.Id == p.Id || x.Id == f.Id);
        }
    }

    [PostgreSqlFact]
    public async Task DuplicatasBuscaJaInjetadosERevisoesConflitantesNaoDuplicamConteudo()
    {
        await using var c = await Cenario.CriarAsync(); var (k, p, f) = await c.TrioAsync(); var refs = Refs(k, p, f);
        await c.SnapshotAsync(refs.Concat(refs).ToArray()); var primeiro = await c.ConstruirAsync("sentinela255");
        Assert.Equal(3, primeiro.Itens.Count(x => x.Referenciado)); Assert.Equal(primeiro.Itens.Count, primeiro.Itens.Select(x => x.Chave).Distinct().Count());
        var repetido = await c.ConstruirAsync("sentinela255", ja: primeiro.Itens.ToDictionary(x => x.Chave, x => x.Revisao));
        Assert.Empty(repetido.Itens); Assert.Contains(repetido.Registros, x => x.Motivo.Contains("Já injetado"));
        await c.SnapshotAsync(refs.Concat(new[] { $"conhecimento:{k.Id}@{k.Revisao + 1}", $"candidato:{p.Id}@{p.Revisao + 1}", $"fonte:{f.Id}@{f.Revisao + 1}" }).ToArray());
        var conflito = await c.ConstruirAsync("sentinela255");
        Assert.DoesNotContain(conflito.Itens, x => x.Referenciado); Assert.Equal(6, conflito.Registros.Count(x => x.Motivo.Contains("revisões diferentes")));
        await c.SnapshotAsync([]); Assert.DoesNotContain((await c.ConstruirAsync()).Itens, x => x.Referenciado);
    }

    [PostgreSqlFact]
    public async Task PrioridadeOrcamentoELimiteDeItensSaoAplicadosAoConteudoCompleto()
    {
        await using var c = await Cenario.CriarAsync(); var (k, p, f) = await c.TrioAsync(); await c.SnapshotAsync(Refs(k, p, f));
        var um = await c.ConstruirAsync(limite: 1); Assert.Equal(k.Id, Assert.Single(um.Itens).Id);
        Assert.Contains(um.Registros, x => x.Motivo.Contains("Limite de itens"));
        var curto = await c.ConstruirAsync(tokens: 64); Assert.Empty(curto.Itens); Assert.Equal(0, curto.Custo.TokensEstimados);
        Assert.Contains(curto.Registros, x => x.Motivo.Contains("orçamento"));
        foreach (var orcamento in new[] { 200, 500, 2000 })
        {
            var pacote = await c.ConstruirAsync(tokens: orcamento, limite: 2);
            Assert.True(pacote.Custo.TokensEstimados <= orcamento); Assert.True(pacote.Itens.Count <= 2);
            Assert.Equal(ConstrutorDeContextoAppService.EstimarTokens(pacote.TextoParaInjecao), pacote.Custo.TokensEstimados);
        }
    }

    [PostgreSqlFact]
    public async Task FonteGrandeNaoETruncadaNemSubstituidaPorTrechoQuandoNaoCabe()
    {
        await using var c = await Cenario.CriarAsync(); var (_, _, f) = await c.TrioAsync();
        await using (var db = c.Banco.Contexto())
        {
            var fonte = await db.Set<DocumentoFonte>().SingleAsync(x => x.Id == f.Id);
            fonte.Atualizar(fonte.Revisao, string.Join(' ', Enumerable.Repeat("sentinela255 documento completo", 500)),
                fonte.Autoria, fonte.Sensibilidade, c.Acesso.IdUsuario, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(); f = fonte;
        }
        await c.SnapshotAsync([$"fonte:{f.Id}@{f.Revisao}"]);
        var pequeno = await c.ConstruirAsync("sentinela255", tokens: 1500);
        Assert.DoesNotContain(pequeno.Itens, x => x.Id == f.Id);
        Assert.Contains(pequeno.Registros, x => x.Id == f.Id && x.Motivo.Contains("orçamento"));
        Assert.True(pequeno.Custo.TokensEstimados <= 1500);
        var suficiente = await c.ConstruirAsync("sentinela255");
        Assert.Equal(f.Conteudo, Assert.Single(suficiente.Itens, x => x.Id == f.Id).Conteudo);
    }

    [PostgreSqlFact]
    public async Task SessaoARegistraTresCandidatosESessaoBRecuperaPendentesComRevisaoExata()
    {
        await using var c = await Cenario.CriarAsync(); var trio = new List<CandidatoDeConhecimento>();
        for (var i = 0; i < 3; i++) trio.Add((await c.TrioAsync()).P);
        using var ferramentas = BrainMcpTests.Provedor(c.App); var drivers = new FakeSessionDriverFactory();
        await using var sessoes = new SessionRegistry(drivers, NullLogger<SessionRegistry>.Instance, toolServers: new CompositorDeFerramentas([ferramentas]));
        var m = BrainMcpTests.Mensagem("registrar referências operacionais");
        var a = await sessoes.StartAsync(new(42, AgentKind.Codex, JobExecutionContext.General(AppContext.BaseDirectory), BrainConversation: m.ParaFerramentas())); Assert.True(a.Accepted);
        await using var clienteA = await BrainMcpTests.Cliente.ConectarAsync(Assert.Single(drivers.Created[0].StartOptions!.ToolServers!));
        var refs = trio.Select(x => $"candidato:{x.Id}@{x.Revisao}").ToArray();
        var salvo = await clienteA.ChamarAsync("brain_atualizar_contexto_de_trabalho", new { revisao = 0, objetivo = "continuar validação", tarefa = "issue 255", progresso = "referências selecionadas", ultimo_resultado = "contexto operacional", referencias = refs });
        Assert.False(salvo["isError"]!.GetValue<bool>());
        var novaMensagem = BrainMcpTests.Mensagem("retome trabalho", 124);
        var b = await sessoes.StartAsync(new(42, AgentKind.Claude, JobExecutionContext.General(AppContext.BaseDirectory), BrainConversation: novaMensagem.ParaFerramentas())); Assert.True(b.Accepted);
        await using var clienteB = await BrainMcpTests.Cliente.ConectarAsync(Assert.Single(drivers.Created.Last().StartOptions!.ToolServers!));
        var snapshot = await clienteB.ChamarAsync("brain_obter_contexto_de_trabalho", new { });
        Assert.Equal(refs, JsonNode.Parse(snapshot["content"]![0]!["text"]!.GetValue<string>())!["Dados"]!["Referencias"]!.AsArray().Select(x => x!.GetValue<string>()));
        var contexto = await c.Brain.PrepararAsync(novaMensagem, b.Session!.Id, novaMensagem.Text!); Assert.NotNull(contexto);
        Assert.Equal(3, contexto.Pacote.Itens.Count(x => x.Origem == "candidato"));
        Assert.All(contexto.Pacote.Itens.Where(x => x.Origem == "candidato"), x => { Assert.Null(x.Status); Assert.Null(x.Tipo); Assert.Equal("Pendente, não confirmado", x.Rotulo); });
        await using (var db = c.Banco.Contexto())
        {
            var atual = await db.CandidatosDeConhecimento.SingleAsync(x => x.Id == trio[0].Id);
            atual.Corrigir(atual.Revisao, atual.Tipo, "conteúdo novo não referenciado", atual.Sensibilidade, "teste", atual.Proveniencia, DateTimeOffset.UtcNow); await db.SaveChangesAsync();
        }
        var divergente = await c.Brain.PrepararAsync(novaMensagem, b.Session.Id, novaMensagem.Text!); Assert.NotNull(divergente);
        Assert.DoesNotContain(divergente.Pacote.Itens, x => x.Id == trio[0].Id);
        Assert.Contains(divergente.Pacote.Registros, x => x.Id == trio[0].Id && x.Motivo.Contains("Revisão da referência diverge"));
        Assert.DoesNotContain("conteúdo novo não referenciado", divergente.Texto);
    }

    private static string[] Refs(Conhecimento k, CandidatoDeConhecimento p, DocumentoFonte f) =>
        [$"conhecimento:{k.Id}@{k.Revisao}", $"candidato:{p.Id}@{p.Revisao}", $"fonte:{f.Id}@{f.Revisao}"];

    private sealed class Cenario(Banco banco, ServiceProvider app, TelegramBrain brain, TelegramBrain.EscopoBrainDaSessao escopo) : IAsyncDisposable
    {
        public Banco Banco => banco; public ServiceProvider App => app; public TelegramBrain Brain => brain; public AcessoAoBrain Acesso => escopo.Acesso;
        public static async Task<Cenario> CriarAsync()
        {
            var banco = await Banco.CriarAsync(); var app = BrainMcpTests.CriarApp(banco.ConnectionString); var brain = app.GetRequiredService<TelegramBrain>();
            await BrainMcpTests.SelecionarAsync(brain);
            return new(banco, app, brain, (await brain.ResolverEscopoMcpAsync(BrainMcpTests.Mensagem("contexto")))!);
        }
        public IServiceScope Scope(bool confidencial = false, bool secreto = false)
        {
            var s = app.CreateScope(); s.ServiceProvider.GetRequiredService<AutorizacaoDoBrain>().Estabelecer(escopo.Identidade, Acesso with { PermitirConfidencial = confidencial, PermitirSecreto = secreto }); return s;
        }
        public async Task SnapshotAsync(string[] referencias)
        {
            using var s = Scope(); var service = s.ServiceProvider.GetRequiredService<ContextoDeTrabalhoAppService>(); var atual = await service.RetomarAsync(Acesso);
            await service.SubstituirAsync(Acesso, atual?.Revisao ?? 0, new("objetivo operacional", "tarefa 255", "em andamento", [], referencias, [], [], "resultado selecionado"), Sensibilidade.Pessoal, "teste 255");
        }
        public async Task<PacoteDeContextoDto> ConstruirAsync(string texto = "marcadorsemcorrespondencia255", int tokens = 30000, int limite = 20,
            IReadOnlyDictionary<string, int>? ja = null, bool confidencial = false, bool secreto = false)
        {
            using var s = Scope(confidencial, secreto);
            return await s.ServiceProvider.GetRequiredService<ConstrutorDeContextoAppService>().ConstruirAsync(Acesso with { PermitirConfidencial = confidencial, PermitirSecreto = secreto },
                new() { Mensagem = texto, Filtros = new() { Texto = texto }, OrcamentoDeTokens = tokens, LimiteDeItens = limite, JaInjetados = ja ?? new Dictionary<string, int>() });
        }
        public async Task<(Conhecimento K, CandidatoDeConhecimento P, DocumentoFonte F)> TrioAsync(AcessoAoBrain? acesso = null, Sensibilidade classe = Sensibilidade.Pessoal, bool protegido = false)
        {
            acesso ??= Acesso; var texto = (protegido ? "senha=credencial-sintetica255 " : "sentinela255 ") + Guid.NewGuid();
            var prova = new ProvenienciaDoConhecimento(acesso.IdUsuario, "teste", "fonte:255", trechoDaFonte: texto); var agora = DateTimeOffset.UtcNow;
            var k = new Conhecimento(acesso.IdEspacoDeConhecimento, acesso.IdProjeto, TipoDeConhecimento.Fato, texto + " conhecimento", null, StatusDoConhecimento.Inferido,
                null, classe, null, null, [], prova, agora, NaturezaDoConteudo.DitoPeloUsuario); k.Confirmar(1, prova, agora);
            var p = new CandidatoDeConhecimento(acesso.IdEspacoDeConhecimento, acesso.IdProjeto, TipoDeConhecimento.Fato, texto + " candidato", classe, NaturezaDoConteudo.ConclusaoDoAgente,
                ModoDeCaptura.Explicita, "teste", prova, agora, titulo: "Título do candidato");
            var f = new DocumentoFonte(acesso.IdEspacoDeConhecimento, acesso.IdProjeto, "nota:" + Guid.NewGuid(), "markdown", texto + " documento", AutoriaDaFonte.Agente, classe, acesso.IdUsuario, agora);
            await using var db = banco.Contexto(); db.AddRange(k, p, f); await db.SaveChangesAsync(); return (k, p, f);
        }
        public async ValueTask DisposeAsync() { await app.DisposeAsync(); await banco.DisposeAsync(); }
    }
}
