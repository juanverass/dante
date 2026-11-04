using System.Text.Json;
using System.Text.Json.Nodes;
using Dante.Application.CapturaDeConhecimento;
using Dante.Application.ConstrucaoDeContexto;
using Dante.Application.RelacoesDeConhecimento;
using Dante.Domain.CapturaDeConhecimento;
using Dante.Domain.Conhecimentos;
using Dante.Domain.EspacosDeConhecimento;
using Dante.Domain.RelacoesDeConhecimento;
using Dante.Worker.Brain;
using Dante.Worker.Telegram;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Dante.Tests;

// #259: intenção de ResolvidoPor entre candidatos pendentes; a relação só nasce com os dois lados consolidados.
public sealed class VinculoEntreCandidatosTests
{
    private const string Ferramenta = "brain_vincular_candidatos";
    private const string TextoIncidente = "Incidente 259: SYN IPv6 sem resposta trava o polling do Telegram até o timeout.";
    private const string TextoSolucao = "Solução 259: conexão Happy Eyeballs intercala IPv6 e IPv4 e cancela a perdedora.";

    [Fact]
    public void SchemaExigeOsDoisCandidatosComRevisoesSemEscopoNemTipo()
    {
        var f = ServidorMcpDoBrain.Ferramentas.Single(x => x.Nome == Ferramenta);
        Assert.False(f.Leitura); Assert.False(f.Destrutiva);
        Assert.Equal(["incidente", "revisao_incidente", "revisao_solucao", "solucao"], f.Propriedades.Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.Equal(["incidente", "revisao_incidente", "solucao", "revisao_solucao"], f.Obrigatorios);
        var valido = new Dictionary<string, object> { ["incidente"] = Guid.NewGuid(), ["revisao_incidente"] = 1, ["solucao"] = Guid.NewGuid(), ["revisao_solucao"] = 1 };
        ServidorMcpDoBrain.Validar(Ferramenta, JsonSerializer.SerializeToElement(valido));
        foreach (var extra in new[] { "tipo", "tenant", "id_projeto", "status", "confirmado" })
            Assert.Throws<ArgumentException>(() => ServidorMcpDoBrain.Validar(Ferramenta, JsonSerializer.SerializeToElement(new Dictionary<string, object>(valido) { [extra] = "x" })));
        Assert.Throws<ArgumentException>(() => ServidorMcpDoBrain.Validar(Ferramenta, JsonSerializer.SerializeToElement(new { incidente = Guid.NewGuid(), revisao_incidente = 1 })));
        Assert.Contains(Ferramenta, ContratoDoBrainParaAgentes.Instrucoes);
        Assert.Contains("Não cria relação", f.Descricao);
    }

    [Fact]
    public void DominioExigeTiposPendentesEEscopoEEncerraUmaVez()
    {
        var espaco = Guid.NewGuid(); var agora = DateTimeOffset.UtcNow;
        var prova = new ProvenienciaDoConhecimento(Guid.NewGuid(), "teste", "mensagem:1", trechoDaFonte: "trecho");
        CandidatoDeConhecimento Novo(TipoDeConhecimento tipo, Guid? e = null) => new(e ?? espaco, null, tipo, $"conteúdo {Guid.NewGuid()}", Sensibilidade.Pessoal,
            NaturezaDoConteudo.ConclusaoDoAgente, ModoDeCaptura.Explicita, "teste", prova, agora);
        var inc = Novo(TipoDeConhecimento.Incidente); var sol = Novo(TipoDeConhecimento.Solucao);
        Assert.Throws<ArgumentException>(() => new VinculoEntreCandidatos(sol, inc, prova, agora));
        Assert.Throws<ArgumentException>(() => new VinculoEntreCandidatos(inc, inc, prova, agora));
        Assert.Throws<ArgumentException>(() => new VinculoEntreCandidatos(inc, Novo(TipoDeConhecimento.Solucao, Guid.NewGuid()), prova, agora));
        var v = new VinculoEntreCandidatos(inc, sol, prova, agora);
        Assert.Equal((EstadoDoVinculo.Pendente, null, null), (v.Estado, v.IdConhecimentoIncidente, v.IdRelacao));
        Assert.False(v.RegistrarConsolidacao(inc.Id, Guid.NewGuid(), 1, agora));
        Assert.Throws<InvalidOperationException>(() => v.Aplicar(Guid.NewGuid(), prova, agora));
        Assert.Throws<ArgumentException>(() => v.RegistrarConsolidacao(Guid.NewGuid(), Guid.NewGuid(), 1, agora));
        Assert.True(v.RegistrarConsolidacao(sol.Id, Guid.NewGuid(), 1, agora));
        Assert.False(v.PodeEfetivar(null, null));
        v.Cancelar("teste", prova, agora);
        Assert.Throws<InvalidOperationException>(() => v.Cancelar("de novo", prova, agora));
        Assert.Throws<InvalidOperationException>(() => v.Aplicar(Guid.NewGuid(), prova, agora));
        var pendenteEncerrado = Novo(TipoDeConhecimento.Incidente); pendenteEncerrado.Descartar(1, prova, agora);
        Assert.Throws<InvalidOperationException>(() => new VinculoEntreCandidatos(pendenteEncerrado, Novo(TipoDeConhecimento.Solucao), prova, agora));
    }

    [PostgreSqlTheory]
    [InlineData(true, "ConclusaoDoAgente")]
    [InlineData(false, "ConclusaoDoAgente")]
    [InlineData(true, "DitoPeloUsuario")]
    [InlineData(false, "DitoPeloUsuario")]
    public async Task OrdemDeConfirmacaoCriaUmaRelacaoSemMudarAutoriaNemStatus(bool incidentePrimeiro, string natureza)
    {
        await using var c = await Cenario.CriarAsync();
        var (inc, sol) = await c.CapturarParAsync(natureza);
        var vinculo = Json(await c.OperarAsync(Ferramenta, new { incidente = inc.Id, revisao_incidente = inc.Revisao, solucao = sol.Id, revisao_solucao = sol.Revisao }));
        Assert.False(vinculo["relacaoCriada"]!.GetValue<bool>()); Assert.Equal("Pendente", vinculo["vinculo"]!["estado"]!.GetValue<string>());
        // Repetir o pedido devolve a mesma intenção.
        Assert.Equal(vinculo["vinculo"]!["Id"]!.GetValue<Guid>(),
            Json(await c.OperarAsync(Ferramenta, new { incidente = inc.Id, revisao_incidente = inc.Revisao, solucao = sol.Id, revisao_solucao = sol.Revisao }))["vinculo"]!["Id"]!.GetValue<Guid>());
        await c.SemRelacaoAsync();
        var (primeiro, segundo) = incidentePrimeiro ? (inc, sol) : (sol, inc);
        var proposta = Json(await c.OperarAsync("brain_confirmar_candidato", new { id = primeiro.Id, revisao = primeiro.Revisao }));
        Assert.Single(proposta["vinculosPendentes"]!.AsArray());
        Assert.Contains("aguarda a confirmação do outro candidato", await c.ConfirmarNoTelegramAsync());
        await c.SemRelacaoAsync();
        await using (var db = c.Banco.Contexto())
        {
            var v = await db.Set<VinculoEntreCandidatos>().SingleAsync();
            Assert.Equal(EstadoDoVinculo.Pendente, v.Estado);
            Assert.True(incidentePrimeiro ? v.IdConhecimentoIncidente is not null && v.IdConhecimentoSolucao is null : v.IdConhecimentoSolucao is not null && v.IdConhecimentoIncidente is null);
        }
        await c.OperarAsync("brain_confirmar_candidato", new { id = segundo.Id, revisao = segundo.Revisao });
        Assert.Contains("Relação ResolvidoPor registrada", await c.ConfirmarNoTelegramAsync());
        // Confirmar de novo não promove nem duplica.
        await BrainMcpTests.RecusaAsync(CategoriaDeErroMcp.RevisaoDesatualizada, () => c.OperarAsync("brain_confirmar_candidato", new { id = segundo.Id, revisao = segundo.Revisao }));
        await using (var db = c.Banco.Contexto())
        {
            var relacao = await db.RelacoesDeConhecimento.SingleAsync();
            var conhecimentos = await db.Conhecimentos.ToListAsync(); Assert.Equal(2, conhecimentos.Count);
            var ki = conhecimentos.Single(x => x.Tipo == TipoDeConhecimento.Incidente); var ks = conhecimentos.Single(x => x.Tipo == TipoDeConhecimento.Solucao);
            Assert.Equal((TipoDeRelacao.ResolvidoPor, ki.Id, ks.Id), (relacao.Tipo, relacao.IdOrigem, relacao.IdDestino));
            var esperado = natureza == "DitoPeloUsuario" ? StatusDoConhecimento.Confirmado : StatusDoConhecimento.Inferido;
            // A relação não muda status, tipo nem natureza de origem (AD-59): conclusão do agente segue Inferida.
            Assert.All(conhecimentos, k => { Assert.Equal(esperado, k.Status); Assert.Equal(Enum.Parse<NaturezaDoConteudo>(natureza), k.NaturezaDeOrigem); });
            var v = await db.Set<VinculoEntreCandidatos>().SingleAsync();
            Assert.Equal((EstadoDoVinculo.Aplicado, relacao.Id, ki.Id, ks.Id), (v.Estado, v.IdRelacao, v.IdConhecimentoIncidente, v.IdConhecimentoSolucao));
            Assert.Equal(relacao.Proveniencia.ReferenciaDaFonte, v.ProvenienciaDoEncerramento!.ReferenciaDaFonte);
        }
        var listadas = (VizinhancaDto)(await c.OperarAsync("brain_listar_relacoes", new { id = (await c.ConhecimentoAsync(TipoDeConhecimento.Incidente)).Id }))!;
        Assert.Equal(TipoDeRelacao.ResolvidoPor, Assert.Single(listadas.Relacoes).Tipo);
    }

    [PostgreSqlTheory]
    [InlineData("descartar-incidente")]
    [InlineData("descartar-solucao")]
    [InlineData("rejeitar-solucao-apos-incidente")]
    [InlineData("incidente-invalidado")]
    [InlineData("tipo-corrigido")]
    [InlineData("incidente-alterado-apos-consolidar")]
    [InlineData("incidente-confidencial")]
    public async Task LadoEncerradoOuInvalidoCancelaSemRelacaoOrfa(string caso)
    {
        await using var c = await Cenario.CriarAsync();
        var (inc, sol) = await c.CapturarParAsync("ConclusaoDoAgente");
        await c.OperarAsync(Ferramenta, new { incidente = inc.Id, revisao_incidente = inc.Revisao, solucao = sol.Id, revisao_solucao = sol.Revisao });
        using var s = c.Scope(); var captura = s.ServiceProvider.GetRequiredService<ICapturaDeConhecimentoAppService>();
        var prova = new Dante.Application.Conhecimentos.ProvenienciaDto { IdResponsavel = c.Escopo.Acesso.IdUsuario, Origem = "teste", ReferenciaDaFonte = "conversa:55:0:300", TrechoDaFonte = "cancelar" };
        switch (caso)
        {
            case "descartar-incidente":
                var proposta = Json(await c.OperarAsync("brain_cancelar_candidato", new { id = inc.Id, revisao = inc.Revisao }));
                Assert.Contains("cancela o vínculo", proposta["instrucao"]!.GetValue<string>());
                Assert.Contains("descartado", await c.ConfirmarNoTelegramAsync());
                await c.OperarAsync("brain_confirmar_candidato", new { id = sol.Id, revisao = sol.Revisao });
                var confirmacao = await c.ConfirmarNoTelegramAsync();
                Assert.Contains("foi cancelado", confirmacao); Assert.DoesNotContain("Relação ResolvidoPor registrada", confirmacao);
                break;
            case "descartar-solucao":
                await captura.DescartarAsync(c.Escopo.Acesso.IdEspacoDeConhecimento, c.Escopo.Acesso.IdProjeto, sol.Id, sol.Revisao, prova);
                await c.OperarAsync("brain_confirmar_candidato", new { id = inc.Id, revisao = inc.Revisao });
                await c.ConfirmarNoTelegramAsync();
                break;
            case "rejeitar-solucao-apos-incidente":
                await c.OperarAsync("brain_confirmar_candidato", new { id = inc.Id, revisao = inc.Revisao }); await c.ConfirmarNoTelegramAsync();
                await captura.RejeitarAsync(c.Escopo.Acesso.IdEspacoDeConhecimento, c.Escopo.Acesso.IdProjeto, sol.Id, sol.Revisao, prova);
                break;
            case "incidente-invalidado":
                await c.OperarAsync("brain_confirmar_candidato", new { id = inc.Id, revisao = inc.Revisao }); await c.ConfirmarNoTelegramAsync();
                await using (var db = c.Banco.Contexto())
                {
                    var k = await db.Conhecimentos.SingleAsync(); k.Invalidar(k.Revisao, new(c.Escopo.Acesso.IdUsuario, "teste", "conversa:55:0:301", trechoDaFonte: "invalidar"), DateTimeOffset.UtcNow);
                    await db.SaveChangesAsync();
                }
                await c.OperarAsync("brain_confirmar_candidato", new { id = sol.Id, revisao = sol.Revisao }); await c.ConfirmarNoTelegramAsync();
                break;
            case "incidente-alterado-apos-consolidar":
            case "incidente-confidencial":
                // O lado já consolidado muda de versão (ou passa a Confidencial) antes da outra confirmação: a efetivação revalida e cancela.
                await c.OperarAsync("brain_confirmar_candidato", new { id = inc.Id, revisao = inc.Revisao }); await c.ConfirmarNoTelegramAsync();
                await using (var db = c.Banco.Contexto())
                {
                    var k = await db.Conhecimentos.SingleAsync();
                    k.Corrigir(k.Revisao, k.Tipo, k.Conteudo + " Corrigido.", k.DadosEstruturados, k.Confianca,
                        caso == "incidente-confidencial" ? Sensibilidade.Confidencial : k.Sensibilidade, k.ValidoDesde, k.ValidoAte, k.Tags,
                        new(c.Escopo.Acesso.IdUsuario, "teste", "conversa:55:0:302", trechoDaFonte: "corrigir"), DateTimeOffset.UtcNow, NaturezaDoConteudo.ConclusaoDoAgente);
                    await db.SaveChangesAsync();
                }
                await c.OperarAsync("brain_confirmar_candidato", new { id = sol.Id, revisao = sol.Revisao });
                Assert.Contains("foi cancelado", await c.ConfirmarNoTelegramAsync());
                break;
            case "tipo-corrigido":
                // Correção pendente muda a revisão; o tipo deixa de ser Incidente e a consolidação cancela a intenção.
                await c.OperarAsync("brain_corrigir_candidato", new { id = inc.Id, revisao = inc.Revisao, conteudo = TextoIncidente, natureza = "ConclusaoDoAgente", justificativa = "reclassificado", tipo = "Nota" });
                await c.OperarAsync("brain_confirmar_candidato", new { id = inc.Id, revisao = inc.Revisao + 1 }); await c.ConfirmarNoTelegramAsync();
                await c.OperarAsync("brain_confirmar_candidato", new { id = sol.Id, revisao = sol.Revisao }); await c.ConfirmarNoTelegramAsync();
                break;
        }
        await c.SemRelacaoAsync();
        await using (var db = c.Banco.Contexto())
        {
            var v = await db.Set<VinculoEntreCandidatos>().SingleAsync();
            Assert.Equal(EstadoDoVinculo.Cancelado, v.Estado); Assert.Null(v.IdRelacao); Assert.False(string.IsNullOrWhiteSpace(v.MotivoDoEncerramento));
            // Nenhum outro candidato ou conhecimento é alterado automaticamente.
            Assert.All(await db.Conhecimentos.ToListAsync(), k => Assert.Equal(NaturezaDoConteudo.ConclusaoDoAgente, k.NaturezaDeOrigem));
        }
    }

    [PostgreSqlFact]
    public async Task RevisaoAlteradaAntesDaConfirmacaoEReinicioDoWorkerPreservamAIntencao()
    {
        await using var banco = await Banco.CriarAsync(); Guid idIncidente, idSolucao; int revisaoIncidente;
        using (var anterior = BrainMcpTests.CriarApp(banco.ConnectionString))
        {
            var c = await Cenario.CriarAsync(banco, anterior);
            var (inc, sol) = await c.CapturarParAsync("ConclusaoDoAgente"); idIncidente = inc.Id; idSolucao = sol.Id;
            await BrainMcpTests.RecusaAsync(CategoriaDeErroMcp.RevisaoDesatualizada,
                () => c.OperarAsync(Ferramenta, new { incidente = inc.Id, revisao_incidente = inc.Revisao + 1, solucao = sol.Id, revisao_solucao = sol.Revisao }));
            await c.OperarAsync(Ferramenta, new { incidente = inc.Id, revisao_incidente = inc.Revisao, solucao = sol.Id, revisao_solucao = sol.Revisao });
            await c.OperarAsync("brain_corrigir_candidato", new { id = inc.Id, revisao = inc.Revisao, conteudo = TextoIncidente + " Revisado.", natureza = "ConclusaoDoAgente", justificativa = "detalhe" });
            revisaoIncidente = inc.Revisao + 1;
            await BrainMcpTests.RecusaAsync(CategoriaDeErroMcp.RevisaoDesatualizada, () => c.OperarAsync("brain_confirmar_candidato", new { id = inc.Id, revisao = inc.Revisao }));
            await c.OperarAsync("brain_confirmar_candidato", new { id = inc.Id, revisao = revisaoIncidente });
            Assert.Contains("aguarda", await c.ConfirmarNoTelegramAsync());
        }
        // Novo processo: contador de sessão recomeça; a intenção persistida é efetivada na confirmação da solução.
        using var atual = BrainMcpTests.CriarApp(banco.ConnectionString);
        var novo = await Cenario.CriarAsync(banco, atual, selecionarExistente: true);
        await novo.OperarAsync("brain_confirmar_candidato", new { id = idSolucao, revisao = 1 });
        Assert.Contains("Relação ResolvidoPor registrada", await novo.ConfirmarNoTelegramAsync());
        await using var db = banco.Contexto();
        var relacao = await db.RelacoesDeConhecimento.SingleAsync(); Assert.Equal(TipoDeRelacao.ResolvidoPor, relacao.Tipo);
        Assert.Contains(TextoIncidente + " Revisado.", (await db.Conhecimentos.SingleAsync(x => x.Id == relacao.IdOrigem)).Conteudo);
        Assert.Equal(EstadoDoVinculo.Aplicado, (await db.Set<VinculoEntreCandidatos>().SingleAsync()).Estado);
    }

    [PostgreSqlFact]
    public async Task IdsInvalidosEscopoSensibilidadeESessaoNaoVazamExistencia()
    {
        await using var c = await Cenario.CriarAsync();
        var (inc, sol) = await c.CapturarParAsync("ConclusaoDoAgente");
        object Args(object incidente, object solucao, int ri = 1, int rs = 1) => new { incidente, revisao_incidente = ri, solucao, revisao_solucao = rs };
        await BrainMcpTests.RecusaAsync(CategoriaDeErroMcp.EntradaInvalida, () => c.OperarAsync(Ferramenta, Args("nao-guid", sol.Id)));
        await BrainMcpTests.RecusaAsync(CategoriaDeErroMcp.EntradaInvalida, () => c.OperarAsync(Ferramenta, Args(Guid.Empty, sol.Id)));
        await BrainMcpTests.RecusaAsync(CategoriaDeErroMcp.EntradaInvalida, () => c.OperarAsync(Ferramenta, Args(inc.Id, inc.Id)));
        Assert.Equal(ErrosDoBrainMcp.DetalheTiposDoVinculo, BrainMcpTests.DetalheDe(await BrainMcpTests.RecusaAsync(CategoriaDeErroMcp.EntradaInvalida, () => c.OperarAsync(Ferramenta, Args(sol.Id, inc.Id)))));
        var inexistente = await BrainMcpTests.RecusaAsync(CategoriaDeErroMcp.NaoEncontradoOuNaoAutorizado, () => c.OperarAsync(Ferramenta, Args(Guid.NewGuid(), sol.Id)));
        // Candidato de outro espaço (outro usuário/tenant), de outro projeto e Confidencial: mesma resposta byte a byte.
        var outroEspaco = new EspacoDeConhecimento(Guid.NewGuid(), "Alheio"); var prova = new ProvenienciaDoConhecimento(Guid.NewGuid(), "teste", "mensagem:9", trechoDaFonte: "x");
        var alheio = new CandidatoDeConhecimento(outroEspaco.Id, null, TipoDeConhecimento.Incidente, "alheio", Sensibilidade.Pessoal, NaturezaDoConteudo.ConclusaoDoAgente, ModoDeCaptura.Explicita, "t", prova, DateTimeOffset.UtcNow);
        var protegido = new CandidatoDeConhecimento(c.Escopo.Acesso.IdEspacoDeConhecimento, c.Escopo.Acesso.IdProjeto, TipoDeConhecimento.Incidente, "protegido", Sensibilidade.Confidencial,
            NaturezaDoConteudo.ConclusaoDoAgente, ModoDeCaptura.Explicita, "t", prova, DateTimeOffset.UtcNow);
        var semProjeto = new CandidatoDeConhecimento(c.Escopo.Acesso.IdEspacoDeConhecimento, null, TipoDeConhecimento.Incidente, "sem projeto", Sensibilidade.Pessoal,
            NaturezaDoConteudo.ConclusaoDoAgente, ModoDeCaptura.Explicita, "t", prova, DateTimeOffset.UtcNow);
        await using (var db = c.Banco.Contexto()) { db.Add(outroEspaco); db.AddRange(alheio, protegido, semProjeto); await db.SaveChangesAsync(); }
        foreach (var oculto in new[] { alheio.Id, protegido.Id, semProjeto.Id })
            Assert.Equal(inexistente, await BrainMcpTests.RecusaAsync(CategoriaDeErroMcp.NaoEncontradoOuNaoAutorizado, () => c.OperarAsync(Ferramenta, Args(oculto, sol.Id))));
        await BrainMcpTests.RecusaAsync(CategoriaDeErroMcp.RevisaoDesatualizada, () => c.OperarAsync(Ferramenta, Args(inc.Id, sol.Id, 2, 1)));
        // ID de conhecimento no lugar de candidato.
        await c.OperarAsync("brain_confirmar_candidato", new { id = inc.Id, revisao = inc.Revisao }); await c.ConfirmarNoTelegramAsync();
        var conhecimento = await c.ConhecimentoAsync(TipoDeConhecimento.Incidente);
        Assert.Equal(ErrosDoBrainMcp.DetalheIdDeConhecimento, BrainMcpTests.DetalheDe(await BrainMcpTests.RecusaAsync(CategoriaDeErroMcp.EntradaInvalida, () => c.OperarAsync(Ferramenta, Args(conhecimento.Id, sol.Id)))));
        await BrainMcpTests.RecusaAsync(CategoriaDeErroMcp.RevisaoDesatualizada, () => c.OperarAsync(Ferramenta, Args(inc.Id, sol.Id)));
        c.App.GetRequiredService<RegistroDeOperacoesBrain>().Revogar(Cenario.Sessao);
        await BrainMcpTests.RecusaAsync(CategoriaDeErroMcp.EscopoMudou, () => c.OperarAsync(Ferramenta, Args(inc.Id, sol.Id), registrar: false));
        await using var verificar = c.Banco.Contexto(); Assert.Empty(await verificar.Set<VinculoEntreCandidatos>().ToListAsync()); Assert.Empty(await verificar.RelacoesDeConhecimento.ToListAsync());
    }

    [PostgreSqlFact]
    public async Task EscopoAlteradoDuranteAConfirmacaoNaoEfetivaNemCriaRelacao()
    {
        await using var c = await Cenario.CriarAsync();
        var (inc, sol) = await c.CapturarParAsync("ConclusaoDoAgente");
        await c.OperarAsync(Ferramenta, new { incidente = inc.Id, revisao_incidente = inc.Revisao, solucao = sol.Id, revisao_solucao = sol.Revisao });
        await c.OperarAsync("brain_confirmar_candidato", new { id = inc.Id, revisao = inc.Revisao }); await c.ConfirmarNoTelegramAsync();
        await c.OperarAsync("brain_confirmar_candidato", new { id = sol.Id, revisao = sol.Revisao });
        Assert.Contains("selecionado", await c.Brain.AtenderAsync(BrainMcpTests.Mensagem("criar projeto Outro", 400), "criar projeto Outro"));
        Assert.DoesNotContain("Relação ResolvidoPor registrada", await c.ConfirmarNoTelegramAsync());
        await c.SemRelacaoAsync();
        await using var db = c.Banco.Contexto();
        Assert.Equal(EstadoDoCandidato.Pendente, (await db.CandidatosDeConhecimento.SingleAsync(x => x.Id == sol.Id)).Estado);
        Assert.Equal(EstadoDoVinculo.Pendente, (await db.Set<VinculoEntreCandidatos>().SingleAsync()).Estado);
    }

    [PostgreSqlFact]
    public async Task ConfirmacoesConcorrentesNaoDuplicamNemDeixamIntencaoOrfa()
    {
        await using var c = await Cenario.CriarAsync();
        var (inc, sol) = await c.CapturarParAsync("ConclusaoDoAgente");
        await c.OperarAsync(Ferramenta, new { incidente = inc.Id, revisao_incidente = inc.Revisao, solucao = sol.Id, revisao_solucao = sol.Revisao });
        var prova = new Dante.Application.Conhecimentos.ProvenienciaDto { IdResponsavel = c.Escopo.Acesso.IdUsuario, Origem = "confirmação Telegram", ReferenciaDaFonte = "conversa:55:0:500", TrechoDaFonte = "confirmar" };
        async Task<Exception?> Confirmar(Guid id)
        {
            using var s = c.Scope();
            try { await s.ServiceProvider.GetRequiredService<ICapturaDeConhecimentoAppService>().ConfirmarAsync(c.Escopo.Acesso.IdEspacoDeConhecimento, c.Escopo.Acesso.IdProjeto, id, 1, prova); return null; }
            catch (Exception ex) { return ex; }
        }
        // Os dois lados em paralelo: no máximo um conflita (token da intenção); nenhum resultado parcial é gravado.
        var falhas = (await Task.WhenAll(Confirmar(inc.Id), Confirmar(sol.Id))).OfType<Exception>().ToArray();
        Assert.All(falhas, f => Assert.IsType<Dante.Application.Comum.ConflitoDeConcorrenciaException>(f));
        await using (var db = c.Banco.Contexto())
            foreach (var pendente in await db.CandidatosDeConhecimento.Where(x => x.Estado == EstadoDoCandidato.Pendente).Select(x => x.Id).ToListAsync())
                Assert.Null(await Confirmar(pendente));
        // Repetir depois de consolidado é recusado e não cria outra relação.
        Assert.IsType<InvalidOperationException>(await Confirmar(inc.Id));
        await using var verificar = c.Banco.Contexto();
        Assert.Equal(TipoDeRelacao.ResolvidoPor, (await verificar.RelacoesDeConhecimento.SingleAsync()).Tipo);
        Assert.Equal(2, await verificar.Conhecimentos.CountAsync());
        Assert.Equal(EstadoDoVinculo.Aplicado, (await verificar.Set<VinculoEntreCandidatos>().SingleAsync()).Estado);
    }

    [PostgreSqlFact]
    public async Task IntencaoDesatualizadaConflitaPeloTokenDeConcorrencia()
    {
        await using var c = await Cenario.CriarAsync();
        var (inc, sol) = await c.CapturarParAsync("ConclusaoDoAgente");
        await c.OperarAsync(Ferramenta, new { incidente = inc.Id, revisao_incidente = inc.Revisao, solucao = sol.Id, revisao_solucao = sol.Revisao });
        // Leitura antes da primeira consolidação, gravada depois dela: o token xmin recusa a cópia obsoleta.
        await using var obsoleto = c.Banco.Contexto(); var copia = await obsoleto.Set<VinculoEntreCandidatos>().SingleAsync();
        await c.OperarAsync("brain_confirmar_candidato", new { id = inc.Id, revisao = inc.Revisao }); await c.ConfirmarNoTelegramAsync();
        copia.RegistrarConsolidacao(sol.Id, Guid.NewGuid(), 1, DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => obsoleto.SaveChangesAsync());
    }

    [PostgreSqlFact]
    public async Task ContextBuilderExpandePorResolvidoPorSoDepoisDaConsolidacaoESemRotularComoFato()
    {
        await using var c = await Cenario.CriarAsync();
        var (inc, sol) = await c.CapturarParAsync("ConclusaoDoAgente");
        await c.OperarAsync(Ferramenta, new { incidente = inc.Id, revisao_incidente = inc.Revisao, solucao = sol.Id, revisao_solucao = sol.Revisao });
        var antes = await c.ContextoAsync("polling");
        Assert.DoesNotContain(antes.Itens, x => x.Origem is "conhecimento" or "candidato");
        await c.OperarAsync("brain_confirmar_candidato", new { id = inc.Id, revisao = inc.Revisao }); await c.ConfirmarNoTelegramAsync();
        var meio = await c.ContextoAsync("polling");
        Assert.Single(meio.Itens, x => x.Origem == "conhecimento"); Assert.DoesNotContain(meio.Itens, x => x.Motivo.Contains("Expansão por ResolvidoPor"));
        await c.OperarAsync("brain_confirmar_candidato", new { id = sol.Id, revisao = sol.Revisao }); await c.ConfirmarNoTelegramAsync();
        var depois = await c.ContextoAsync("polling");
        var solucao = Assert.Single(depois.Itens, x => x.Tipo == TipoDeConhecimento.Solucao);
        Assert.Contains("Expansão por ResolvidoPor", solucao.Motivo);
        Assert.All(depois.Itens.Where(x => x.Origem == "conhecimento"), x => { Assert.Equal(StatusDoConhecimento.Inferido, x.Status); Assert.Contains("não confirmad", x.Rotulo); });
    }

    [PostgreSqlFact]
    public async Task MigrationDesceESobeSemAlterarCandidatosERelacoes()
    {
        await using var c = await Cenario.CriarAsync();
        var (inc, sol) = await c.CapturarParAsync("ConclusaoDoAgente");
        await c.OperarAsync(Ferramenta, new { incidente = inc.Id, revisao_incidente = inc.Revisao, solucao = sol.Id, revisao_solucao = sol.Revisao });
        await using (var db = c.Banco.Contexto())
        {
            var migrator = db.GetService<IMigrator>(); var atual = db.Database.GetMigrations().Single(x => x.EndsWith("_VinculosEntreCandidatos", StringComparison.Ordinal));
            await migrator.MigrateAsync(db.Database.GetMigrations().Last(x => string.CompareOrdinal(x, atual) < 0)); await migrator.MigrateAsync();
            Assert.Equal(db.Database.GetMigrations(), await db.Database.GetAppliedMigrationsAsync());
        }
        await using (var db = c.Banco.Contexto())
        {
            Assert.Empty(await db.Set<VinculoEntreCandidatos>().ToListAsync());
            Assert.Equal(2, await db.CandidatosDeConhecimento.CountAsync(x => x.Estado == EstadoDoCandidato.Pendente));
        }
        // Depois de reaplicada, a tabela aceita nova intenção normalmente.
        Assert.Equal("Pendente", Json(await c.OperarAsync(Ferramenta, new { incidente = inc.Id, revisao_incidente = inc.Revisao, solucao = sol.Id, revisao_solucao = sol.Revisao }))["vinculo"]!["estado"]!.GetValue<string>());
    }

    private static JsonNode Json(object? resultado) => JsonNode.Parse(JsonSerializer.Serialize(resultado))!;

    private sealed class Cenario(Banco banco, ServiceProvider app, TelegramBrain brain, TelegramBrain.EscopoBrainDaSessao escopo, bool proprio) : IAsyncDisposable
    {
        public const string Sessao = "S000001";
        private long mensagem = 200;
        public Banco Banco => banco; public ServiceProvider App => app; public TelegramBrain Brain => brain; public TelegramBrain.EscopoBrainDaSessao Escopo => escopo;
        public static async Task<Cenario> CriarAsync()
        {
            var banco = await Banco.CriarAsync(); var app = BrainMcpTests.CriarApp(banco.ConnectionString);
            return await CriarAsync(banco, app, proprio: true);
        }
        public static async Task<Cenario> CriarAsync(Banco banco, ServiceProvider app, bool selecionarExistente = false, bool proprio = false)
        {
            var brain = app.GetRequiredService<TelegramBrain>();
            if (!selecionarExistente) await BrainMcpTests.SelecionarAsync(brain);
            else
            {
                Assert.Contains("selecionado", await brain.AtenderAsync(BrainMcpTests.Mensagem("usar espaço Desenvolvimento"), "usar espaço Desenvolvimento"));
                Assert.Contains("selecionado", await brain.AtenderAsync(BrainMcpTests.Mensagem("usar projeto D.A.N.T.E."), "usar projeto D.A.N.T.E."));
            }
            var inicial = BrainMcpTests.Mensagem(TextoIncidente + "\n" + TextoSolucao, 150);
            app.GetRequiredService<RegistroDeOperacoesBrain>().RegistrarSessao(Sessao, inicial);
            return new(banco, app, brain, (await brain.ResolverEscopoMcpAsync(inicial))!, proprio);
        }
        public IServiceScope Scope() => EscoposBrainDeTeste.Criar(app, escopo.Acesso.IdUsuario, escopo.Acesso.IdEspacoDeConhecimento, escopo.Acesso.IdProjeto);
        public Task<object?> OperarAsync(string ferramenta, object args, bool registrar = true)
        {
            var m = BrainMcpTests.Mensagem(TextoIncidente + "\n" + TextoSolucao, 150);
            if (registrar) app.GetRequiredService<RegistroDeOperacoesBrain>().RegistrarSessao(Sessao, m);
            return app.GetRequiredService<OperacoesMcpDoBrain>().ExecutarAsync(escopo, m, Sessao, "claude", ferramenta, JsonSerializer.SerializeToElement(args));
        }
        public async Task<string> ConfirmarNoTelegramAsync() => await brain.AtenderAsync(BrainMcpTests.Mensagem("confirmar", ++mensagem), "confirmar", Sessao) ?? "";
        public async Task<(CandidatoDeConhecimentoDto Incidente, CandidatoDeConhecimentoDto Solucao)> CapturarParAsync(string natureza)
        {
            await OperarAsync("brain_capturar_conhecimento", new { conteudo = TextoIncidente, tipo = "Incidente", natureza, justificativa = "diagnóstico", tags = new[] { "teste-259" } });
            await OperarAsync("brain_capturar_conhecimento", new { conteudo = TextoSolucao, tipo = "Solucao", natureza, justificativa = "correção aplicada", tags = new[] { "teste-259" } });
            using var s = Scope(); var captura = s.ServiceProvider.GetRequiredService<ICapturaDeConhecimentoAppService>();
            var pendentes = await captura.ListarPendentesAsync(escopo.Acesso.IdEspacoDeConhecimento, escopo.Acesso.IdProjeto);
            return (pendentes.Single(x => x.Tipo == TipoDeConhecimento.Incidente), pendentes.Single(x => x.Tipo == TipoDeConhecimento.Solucao));
        }
        public async Task<Conhecimento> ConhecimentoAsync(TipoDeConhecimento tipo)
        { await using var db = banco.Contexto(); return await db.Conhecimentos.SingleAsync(x => x.Tipo == tipo); }
        public async Task SemRelacaoAsync() { await using var db = banco.Contexto(); Assert.Empty(await db.RelacoesDeConhecimento.ToListAsync()); }
        public async Task<PacoteDeContextoDto> ContextoAsync(string texto)
        {
            using var s = Scope();
            return await s.ServiceProvider.GetRequiredService<ConstrutorDeContextoAppService>().ConstruirAsync(escopo.Acesso,
                new() { Mensagem = texto, Filtros = new() { Texto = texto }, OrcamentoDeTokens = 30000, LimiteDeItens = 20 });
        }
        public async ValueTask DisposeAsync() { if (proprio) { await app.DisposeAsync(); await banco.DisposeAsync(); } }
    }
}
