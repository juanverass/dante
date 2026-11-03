using System.Text.Json;
using System.Text.Json.Nodes;
using Dante.Application.BuscaDoBrain;
using Dante.Application.CapturaDeConhecimento;
using Dante.Application.ConstrucaoDeContexto;
using Dante.Application.SegurancaDoBrain;
using Dante.Domain.Agentes;
using Dante.Domain.CapturaDeConhecimento;
using Dante.Domain.Conhecimentos;
using Dante.Domain.EspacosDeConhecimento;
using Dante.Domain.Projetos;
using Dante.Infrastructure.Data;
using Dante.Infrastructure.Modulos.CapturaDeConhecimento;
using Dante.Worker.Brain;
using Dante.Worker.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dante.Tests;

public sealed class FiltrosDeCandidatosTests
{
    [Fact]
    public void SchemaAnunciaFiltrosOpcionaisSemMudarPaginacaoOuLeitura()
    {
        var ferramenta = ServidorMcpDoBrain.Ferramentas.Single(f => f.Nome == "brain_listar_candidatos");
        Assert.Empty(ferramenta.Obrigatorios); Assert.True(ferramenta.Leitura); Assert.False(ferramenta.Destrutiva);
        Assert.Equal(50, ferramenta.Propriedades["tags"]!["maxItems"]!.GetValue<int>());
        Assert.Equal(100, ferramenta.Propriedades["tags"]!["items"]!["maxLength"]!.GetValue<int>());
        Assert.Equal(2000, ferramenta.Propriedades["texto"]!["maxLength"]!.GetValue<int>());
        Assert.Equal(25, ferramenta.Propriedades["limite"]!["maximum"]!.GetValue<int>());
        Assert.Equal(10000, ferramenta.Propriedades["deslocamento"]!["maximum"]!.GetValue<int>());
        Assert.Contains("revisão atual", ferramenta.Descricao); Assert.Contains("substring literal", ferramenta.Descricao);
        Assert.Contains("AND", ferramenta.Descricao);
    }

    [PostgreSqlFact]
    public async Task TagsETextoUsamSomenteRevisaoAtualESaoLiteraisAntesDaPaginacao()
    {
        await using var banco = await Banco.CriarAsync(); var e = new EspacoDeConhecimento(Guid.NewGuid(), "Filtros");
        var ambos = Novo(e, "Conteúdo com MiStUrA e 100%_literal; ' OR TRUE --", "Título Unicode ÁRVORE", ["Teste-147", "Brain"]);
        var uma = Novo(e, "outro conteúdo", "Título com Mistura", ["teste-147"]);
        var semTags = Novo(e, "mistura sem tags", null, []);
        var legadoNulo = Novo(e, "mistura legado nulo");
        var legadoAusente = Novo(e, "mistura legado ausente");
        var corrigido = Novo(e, "conteúdo antigo", "Título antigo", ["antiga"]);
        corrigido.Corrigir(1, corrigido.Tipo, "conteúdo vigente", corrigido.Sensibilidade, "correção", corrigido.Proveniencia,
            DateTimeOffset.UtcNow, "Título vigente", ["nova"]);
        await using (var db = banco.Contexto())
        {
            db.AddRange(e, ambos, uma, semTags, legadoNulo, legadoAusente, corrigido); await db.SaveChangesAsync();
            // Simula os formatos JSON do legado sem inventar metadados no domínio.
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE brain_data.candidatos_de_conhecimento SET historico=jsonb_set(historico, '{{0,Tags}}', 'null'::jsonb) WHERE id={legadoNulo.Id}");
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE brain_data.candidatos_de_conhecimento SET historico=historico #- '{{0,Tags}}' WHERE id={legadoAusente.Id}");
        }
        using var app = BrainMcpTests.CriarApp(banco.ConnectionString); using var scope = Escopo(app, e);
        var captura = scope.ServiceProvider.GetRequiredService<ICapturaDeConhecimentoAppService>();
        async Task<Guid[]> Listar(string[]? tags = null, string? texto = null) =>
            (await captura.ListarPendentesAsync(e.Id, null, tags: tags, texto: texto)).Select(x => x.Id).ToArray();
        Assert.Equal(new[] { ambos.Id, uma.Id }.Order(), await Listar(["TESTE-147"]));
        Assert.Equal([ambos.Id], await Listar(["brain", "TESTE-147"]));
        Assert.Equal([ambos.Id], await Listar(["brain", "BRAIN"]));
        Assert.Empty(await Listar(["inexistente"]));
        Assert.Equal([ambos.Id], await Listar(["brain"], "mistura"));
        Assert.Empty(await Listar(["brain"], "vigente"));
        Assert.Equal([uma.Id], await Listar(texto: "TÍTULO COM MISTURA"));
        Assert.Equal([ambos.Id], await Listar(texto: "árvore"));
        Assert.Equal([ambos.Id], await Listar(texto: "100%_literal"));
        Assert.Empty(await Listar(texto: "%inexistente%"));
        Assert.Equal([ambos.Id], await Listar(texto: "' OR TRUE --"));
        Assert.Empty(await Listar(["antiga"])); Assert.Empty(await Listar(texto: "antigo"));
        Assert.Equal([corrigido.Id], await Listar(["NOVA"], "vigente"));
        var semFiltro = await Listar();
        Assert.Equal(new[] { ambos.Id, uma.Id, semTags.Id, legadoNulo.Id, legadoAusente.Id, corrigido.Id }.Order(), semFiltro);
        Assert.Equal(semFiltro, await Listar([]));
        Assert.Equal(new[] { ambos.Id, uma.Id, semTags.Id, legadoNulo.Id, legadoAusente.Id }.Order(), await Listar(texto: "MISTURA"));
    }

    [PostgreSqlFact]
    public async Task PendentesFiltradosPreservamEstadosOrdemLimitesEDeslocamento()
    {
        await using var banco = await Banco.CriarAsync(); var e = new EspacoDeConhecimento(Guid.NewGuid(), "Páginas");
        var itens = Enumerable.Range(0, 32).Select(i => Novo(e, $"paginação conteúdo {i}", tags: i % 2 == 0 ? ["selecionada"] : [])).ToArray();
        var promovido = Novo(e, "paginação promovido", tags: ["selecionada"]);
        var rejeitado = Novo(e, "paginação rejeitado", tags: ["selecionada"]);
        var descartado = Novo(e, "paginação descartado", tags: ["selecionada"]);
        var k = promovido.Promover(1, promovido.Proveniencia, DateTimeOffset.UtcNow);
        rejeitado.Rejeitar(1, rejeitado.Proveniencia, DateTimeOffset.UtcNow);
        descartado.Descartar(1, descartado.Proveniencia, DateTimeOffset.UtcNow);
        await using (var db = banco.Contexto()) { db.Add(e); db.AddRange(itens); db.AddRange(promovido, rejeitado, descartado, k); await db.SaveChangesAsync(); }
        using var app = BrainMcpTests.CriarApp(banco.ConnectionString); using var scope = Escopo(app, e);
        var captura = scope.ServiceProvider.GetRequiredService<ICapturaDeConhecimentoAppService>();
        var ids = itens.Where((_, i) => i % 2 == 0).Select(x => x.Id).Order().ToArray();
        var pagina = await captura.ListarPendentesAsync(e.Id, null, 3, deslocamento: 2, tags: ["selecionada"], texto: "PAGINAÇÃO");
        Assert.Equal(ids.Skip(2).Take(3), pagina.Select(x => x.Id));
        Assert.All(pagina, x => Assert.Equal(EstadoDoCandidato.Pendente, x.Estado));
        Assert.Equal(itens.Select(x => x.Id).Order(), (await captura.ListarPendentesAsync(e.Id, null)).Select(x => x.Id));
        Assert.Equal(25, (await captura.ListarPendentesAsync(e.Id, null, 25)).Count);
        Assert.Empty(await captura.ListarPendentesAsync(e.Id, null, 100, deslocamento: 10000, tags: ["selecionada"]));
        Assert.Empty(await captura.ListarPendentesAsync(e.Id, null, deslocamento: ids.Length, tags: ["selecionada"]));
    }

    [PostgreSqlFact]
    public async Task SqlComponivelPreservaUsuarioTenantEspacoProjetoESensibilidade()
    {
        await using var banco = await Banco.CriarAsync(); var e = new EspacoDeConhecimento(Guid.NewGuid(), "Meu");
        var outroEspaco = new EspacoDeConhecimento(e.IdUsuario, "Outro espaço");
        var outroUsuario = new EspacoDeConhecimento(Guid.NewGuid(), "Outro usuário");
        var outroTenant = new EspacoDeConhecimento(e.IdUsuario, "Outro tenant", idTenant: Guid.NewGuid());
        var p = new Projeto(e.Id, "A"); var q = new Projeto(e.Id, "B");
        var livre = Novo(e, "alvo livre", tags: ["isolamento"]);
        var emP = Novo(e, "alvo projeto A", tags: ["isolamento"], projeto: p.Id);
        var emQ = Novo(e, "alvo projeto B", tags: ["isolamento"], projeto: q.Id);
        var confidencial = Novo(e, "alvo confidencial", tags: ["isolamento"], classe: Sensibilidade.Confidencial);
        var secreto = Novo(e, "alvo secreto", tags: ["isolamento"], classe: Sensibilidade.Secreto);
        await using (var db = banco.Contexto())
        {
            db.AddRange(e, outroEspaco, outroUsuario, outroTenant, p, q, livre, emP, emQ, confidencial, secreto,
                Novo(outroEspaco, "alvo outro espaço", tags: ["isolamento"]), Novo(outroUsuario, "alvo outro usuário", tags: ["isolamento"]),
                Novo(outroTenant, "alvo outro tenant", tags: ["isolamento"]));
            await db.SaveChangesAsync();
        }
        using var app = BrainMcpTests.CriarApp(banco.ConnectionString);
        using (var scope = Escopo(app, e))
        {
            var service = scope.ServiceProvider.GetRequiredService<ICapturaDeConhecimentoAppService>();
            Assert.Equal(livre.Id, Assert.Single(await service.ListarPendentesAsync(e.Id, null, tags: ["ISOLAMENTO"], texto: "ALVO")).Id);
            var repo = scope.ServiceProvider.GetRequiredService<ICandidatoDeConhecimentoRepository>();
            Assert.Empty(await repo.ListarPendentesAsync(outroEspaco.Id, null, 25, tags: ["isolamento"]));
            Assert.Empty(await repo.ListarPendentesAsync(outroUsuario.Id, null, 25, tags: ["isolamento"]));
            Assert.Empty(await repo.ListarPendentesAsync(outroTenant.Id, null, 25, tags: ["isolamento"]));
            Assert.Empty(await repo.ListarPendentesAsync(e.Id, p.Id, 25, tags: ["isolamento"]));
        }
        using (var scope = Escopo(app, e, p.Id))
        {
            var repo = scope.ServiceProvider.GetRequiredService<ICandidatoDeConhecimentoRepository>();
            Assert.Equal(emP.Id, Assert.Single(await repo.ListarPendentesAsync(e.Id, p.Id, 25, tags: ["isolamento"], texto: "alvo")).Id);
            Assert.Empty(await repo.ListarPendentesAsync(e.Id, null, 25, tags: ["isolamento"]));
            Assert.Empty(await repo.ListarPendentesAsync(e.Id, q.Id, 25, tags: ["isolamento"]));
        }
        // Mesmo ID de espaço, identidade errada: nem SQL bruto pode contornar a propriedade do espaço.
        foreach (var identidade in new[] { new IdentidadeDoBrain(AutorizacaoDoBrain.TenantLocal, Guid.NewGuid()), new IdentidadeDoBrain(Guid.NewGuid(), e.IdUsuario) })
        {
            var auth = new AutorizacaoDoBrain(); auth.Estabelecer(identidade, new(identidade.IdUsuario, e.Id, null));
            await using var db = new DanteDbContext(new DbContextOptionsBuilder<DanteDbContext>().UseNpgsql(banco.ConnectionString).Options, auth);
            Assert.Empty(await new CandidatoDeConhecimentoRepository(db).ListarPendentesAsync(e.Id, null, 25, tags: ["isolamento"], texto: "alvo"));
        }
        await using var semAuth = new DanteDbContext(new DbContextOptionsBuilder<DanteDbContext>().UseNpgsql(banco.ConnectionString).Options, new AutorizacaoDoBrain());
        Assert.Empty(await new CandidatoDeConhecimentoRepository(semAuth).ListarPendentesAsync(e.Id, null, 25, tags: ["isolamento"]));
    }

    [PostgreSqlFact]
    public async Task CanalMcpFiltraClassificaEntradasERevogaSemExporProtegidos()
    {
        await using var banco = await Banco.CriarAsync(); using var app = BrainMcpTests.CriarApp(banco.ConnectionString);
        var brain = app.GetRequiredService<Dante.Worker.Telegram.TelegramBrain>(); await BrainMcpTests.SelecionarAsync(brain);
        var m = BrainMcpTests.Mensagem("liste os candidatos"); var escopo = (await brain.ResolverEscopoMcpAsync(m))!;
        var e = new EspacoDeConhecimento(escopo.Acesso.IdUsuario, "fixture");
        CandidatoDeConhecimento Criar(string conteudo, Sensibilidade classe = Sensibilidade.Pessoal) => new(escopo.Acesso.IdEspacoDeConhecimento,
            escopo.Acesso.IdProjeto, TipoDeConhecimento.Incidente, conteudo, classe, NaturezaDoConteudo.ConclusaoDoAgente,
            ModoDeCaptura.Explicita, "teste", new(e.IdUsuario, "teste", "fonte:253", trechoDaFonte: conteudo), DateTimeOffset.UtcNow, titulo: "Título MCP", tags: ["teste-147"]);
        var permitidos = Enumerable.Range(0, 28).Select(i => Criar($"conteúdo MCP {i}")).ToArray();
        await using (var db = banco.Contexto())
        {
            db.AddRange(permitidos); db.AddRange(Criar("conteúdo protegido C", Sensibilidade.Confidencial), Criar("conteúdo protegido S", Sensibilidade.Secreto));
            await db.SaveChangesAsync();
        }
        using var ferramentas = BrainMcpTests.Provedor(app);
        var servidores = await ferramentas.ForSessionAsync("S253", new(42, AgentKind.Codex,
            JobExecutionContext.General(AppContext.BaseDirectory), BrainConversation: m.ParaFerramentas()));
        await using var cliente = await BrainMcpTests.Cliente.ConectarAsync(Assert.Single(servidores));
        var resposta = await cliente.ChamarAsync("brain_listar_candidatos", new { tags = new[] { "TESTE-147" }, texto = "mcp", limite = 25, deslocamento = 1 });
        Assert.False(resposta["isError"]!.GetValue<bool>());
        var pagina = JsonNode.Parse(resposta["content"]![0]!["text"]!.GetValue<string>())!;
        Assert.Equal(25, pagina["candidatos"]!.AsArray().Count);
        Assert.Equal(permitidos.Select(x => x.Id).Order().Skip(1).Take(25), pagina["candidatos"]!.AsArray().Select(x => Guid.Parse(x!["Id"]!.GetValue<string>())));
        Assert.All(pagina["candidatos"]!.AsArray(), x => { Assert.Equal("Pendente", x!["estado"]!.GetValue<string>()); Assert.Equal("Incidente", x["tipo"]!.GetValue<string>()); Assert.Equal("ConclusaoDoAgente", x["natureza"]!.GetValue<string>()); });
        Assert.DoesNotContain("protegido", pagina.ToJsonString());
        foreach (var args in new object[] { new { tags = "teste-147" }, new { tags = new object[] { 1 } }, new { tags = new string?[] { null } },
            new { tags = new[] { " " } }, new { tags = Enumerable.Repeat("x", 51).ToArray() }, new { tags = new[] { new string('x', 101) } },
            new { texto = 1 }, new { texto = " " }, new { texto = new string('x', 2001) }, new { texto = (string?)null },
            new { limite = 26 }, new { limite = 0 }, new { deslocamento = 10001 }, new { deslocamento = -1 }, new { projeto = Guid.NewGuid() } })
        {
            var erro = await cliente.ChamarAsync("brain_listar_candidatos", args);
            Assert.True(erro["isError"]!.GetValue<bool>()); Assert.Equal("entrada_invalida", Codigo(erro));
        }
        var vazia = await cliente.ChamarAsync("brain_listar_candidatos", new { tags = new[] { "não-existe" } });
        Assert.Empty(JsonNode.Parse(vazia["content"]![0]!["text"]!.GetValue<string>())!["candidatos"]!.AsArray());
        await brain.AtenderAsync(BrainMcpTests.Mensagem("criar projeto Outro", 124), "criar projeto Outro", "S253");
        var revogada = await cliente.ChamarAsync("brain_listar_candidatos", new { tags = new[] { "teste-147" } });
        Assert.Equal("escopo_mudou", Codigo(revogada));
    }

    [PostgreSqlFact]
    public async Task CandidatoRecuperadoNaoEntraNaBuscaDeConhecimentosNemNoContexto()
    {
        await using var banco = await Banco.CriarAsync(); var e = new EspacoDeConhecimento(Guid.NewGuid(), "Separação");
        var candidato = Novo(e, "marcadorunicocandidato", "Título reservado", ["teste-147"]);
        await using (var db = banco.Contexto()) { db.AddRange(e, candidato); await db.SaveChangesAsync(); }
        using var app = BrainMcpTests.CriarApp(banco.ConnectionString); using var scope = Escopo(app, e);
        Assert.Single(await scope.ServiceProvider.GetRequiredService<ICapturaDeConhecimentoAppService>().ListarPendentesAsync(e.Id, null, tags: ["teste-147"], texto: "marcador"));
        var acesso = new AcessoAoBrain(e.IdUsuario, e.Id, null);
        var busca = await scope.ServiceProvider.GetRequiredService<BuscaDoBrainAppService>().BuscarAsync(acesso, new() { Texto = "marcadorunicocandidato" });
        Assert.Empty(busca.Resultados);
        var pacote = await scope.ServiceProvider.GetRequiredService<ConstrutorDeContextoAppService>().ConstruirAsync(acesso,
            new() { Mensagem = "marcadorunicocandidato", IncluirSnapshot = false });
        Assert.Empty(pacote.Itens); Assert.DoesNotContain(candidato.Conteudo, pacote.TextoParaInjecao);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void ValidatorRecusaFiltrosVazios(string invalido)
    {
        Assert.Throws<ArgumentException>(() => CandidatoDeConhecimentoValidator.ValidarFiltros([invalido], null));
        Assert.Throws<ArgumentException>(() => CandidatoDeConhecimentoValidator.ValidarFiltros(null, invalido));
        CandidatoDeConhecimentoValidator.ValidarFiltros([], null);
        CandidatoDeConhecimentoValidator.ValidarFiltros(null, null);
    }

    private static CandidatoDeConhecimento Novo(EspacoDeConhecimento e, string conteudo, string? titulo = null, string[]? tags = null,
        Guid? projeto = null, Sensibilidade classe = Sensibilidade.Pessoal) => new(e.Id, projeto, TipoDeConhecimento.Nota, conteudo, classe,
            NaturezaDoConteudo.ConclusaoDoAgente, ModoDeCaptura.Explicita, "teste", new(e.IdUsuario, "teste", "fonte:253", trechoDaFonte: conteudo), DateTimeOffset.UtcNow, titulo: titulo, tags: tags);
    private static IServiceScope Escopo(IServiceProvider app, EspacoDeConhecimento e, Guid? projeto = null)
    {
        var scope = app.CreateScope(); scope.ServiceProvider.GetRequiredService<AutorizacaoDoBrain>().Estabelecer(
            new(e.IdTenant, e.IdUsuario), new(e.IdUsuario, e.Id, projeto)); return scope;
    }
    private static string Codigo(JsonObject resposta) => JsonNode.Parse(resposta["content"]![0]!["text"]!.GetValue<string>())!["erro"]!.GetValue<string>();
}
