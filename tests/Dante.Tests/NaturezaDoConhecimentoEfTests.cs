using Dante.Application.Conhecimentos;
using Dante.Application.CapturaDeConhecimento;
using Dante.Application.RelacoesDeConhecimento;
using Dante.Application.SegurancaDoBrain;
using Dante.Domain.CapturaDeConhecimento;
using Dante.Domain.Conhecimentos;
using Dante.Domain.EspacosDeConhecimento;
using Dante.Domain.RelacoesDeConhecimento;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Dante.Tests;

public sealed class NaturezaDoConhecimentoEfTests
{
    [PostgreSqlFact]
    public async Task MigrationPreencheSomenteOrigemComCandidatoSemReescreverStatusOuQualquerHistorico()
    {
        await using var banco = await Banco.CriarAsync(migrar: false);
        var espaco = new EspacoDeConhecimento(Guid.NewGuid(), "Legado #265");
        var instante = DateTimeOffset.UtcNow.AddHours(-1); var prova = NaturezaDoConhecimentoTests.Prova;
        var candidatos = new[]
        {
            new CandidatoDeConhecimento(espaco.Id, null, TipoDeConhecimento.Fato, "declaração antiga", Sensibilidade.Pessoal,
                NaturezaDoConteudo.DitoPeloUsuario, ModoDeCaptura.Explicita, "pedido", prova, instante),
            new CandidatoDeConhecimento(espaco.Id, null, TipoDeConhecimento.Solucao, "conclusão antiga", Sensibilidade.Pessoal,
                NaturezaDoConteudo.ConclusaoDoAgente, ModoDeCaptura.Explicita, "pedido", prova, instante),
            new CandidatoDeConhecimento(espaco.Id, null, TipoDeConhecimento.Inferencia, "inferência dita pelo usuário", Sensibilidade.Pessoal,
                NaturezaDoConteudo.FonteSelecionada, ModoDeCaptura.Explicita, "pedido", prova, instante)
        };
        var itens = candidatos.Select(c => c.Promover(1, prova, instante.AddMinutes(1))).ToList();
        foreach (var tipo in new[] { TipoDeConhecimento.Fato, TipoDeConhecimento.Inferencia })
        {
            var semCandidato = new Conhecimento(espaco.Id, null, tipo, "MCP/agente texto não comprova autoria " + tipo, null,
                StatusDoConhecimento.Inferido, null, Sensibilidade.Pessoal, null, null, [], prova, instante, NaturezaDoConteudo.DitoPeloUsuario);
            semCandidato.Confirmar(1, prova, instante.AddMinutes(1)); itens.Add(semCandidato);
        }
        string[] antes;
        await using (var db = banco.Contexto())
        {
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("20261102150504_AutoriaDasFontes");
            db.Add(espaco); await db.SaveChangesAsync();
            foreach (var item in itens) await ConhecimentoLegadoDeTeste.InserirAsync(db, item);
            db.AddRange(candidatos); await db.SaveChangesAsync();
            antes = await db.Database.SqlQueryRaw<string>("SELECT to_jsonb(k)::text AS \"Value\" FROM brain_data.conhecimentos k ORDER BY id").ToArrayAsync();
            await migrator.MigrateAsync(); await migrator.MigrateAsync();
            var depois = await db.Database.SqlQueryRaw<string>("SELECT (to_jsonb(k)-'natureza'-'natureza_de_origem')::text AS \"Value\" FROM brain_data.conhecimentos k ORDER BY id").ToArrayAsync();
            Assert.Equal(antes, depois);
        }
        await using (var db = banco.Contexto())
        {
            var salvos = await db.Conhecimentos.ToListAsync();
            foreach (var item in salvos)
            {
                var candidato = candidatos.SingleOrDefault(c => c.IdConhecimento == item.Id);
                Assert.Equal(candidato?.Natureza ?? NaturezaDoConteudo.Desconhecida, item.NaturezaDeOrigem);
                // Sem natureza por revisão na origem, não se inventa a autoria da revisão vigente.
                Assert.Equal(NaturezaDoConteudo.Desconhecida, item.Natureza);
                Assert.All(item.Historico, h => Assert.Null(h.Natureza));
                Assert.Equal(itens.Single(x => x.Id == item.Id).Status, item.Status);
                Assert.Throws<InvalidOperationException>(() => item.Confirmar(item.Revisao, prova, DateTimeOffset.UtcNow));
            }
            var legado = salvos.Single(x => x.Id == itens[^1].Id);
            legado.Corrigir(legado.Revisao, legado.Tipo, "revisão explícita", null, null, legado.Sensibilidade, null, null, [], prova, DateTimeOffset.UtcNow,
                NaturezaDoConteudo.DitoPeloUsuario);
            Assert.Equal(NaturezaDoConteudo.Desconhecida, legado.NaturezaDeOrigem);
            Assert.Equal(StatusDoConhecimento.Inferido, legado.Status);
            Assert.Throws<InvalidOperationException>(() => legado.Confirmar(legado.Revisao, prova, DateTimeOffset.UtcNow));
            Assert.All(legado.Historico.Take(legado.Revisao - 1), h => Assert.Null(h.Natureza));
            await db.SaveChangesAsync();
            var json = await db.Database.SqlQuery<string>($"SELECT historico::text AS \"Value\" FROM brain_data.conhecimentos WHERE id = {legado.Id}").SingleAsync();
            var revisoes = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsArray();
            Assert.All(revisoes.Take(revisoes.Count - 1), h => Assert.False(h!.AsObject().ContainsKey("Natureza")));
            Assert.Equal((int)NaturezaDoConteudo.DitoPeloUsuario, revisoes[^1]!["Natureza"]!.GetValue<int>());
        }
    }

    [PostgreSqlFact]
    public async Task NovoConhecimentoHumanoDerivadoEConfirmavelSemAlterarOriginalDoAgente()
    {
        await using var banco = await Banco.CriarAsync(); var espaco = new EspacoDeConhecimento(Guid.NewGuid(), "Derivação");
        await using (var db = banco.Contexto()) { db.Add(espaco); await db.SaveChangesAsync(); }
        using var app = BrainMcpTests.CriarApp(banco.ConnectionString);
        using var scope = EscoposBrainDeTeste.Criar(app, espaco.IdUsuario, espaco.Id);
        var captura = scope.ServiceProvider.GetRequiredService<ICapturaDeConhecimentoAppService>();
        var servico = scope.ServiceProvider.GetRequiredService<IConhecimentoAppService>();
        var prova = new ProvenienciaDto { IdResponsavel = espaco.IdUsuario, Origem = "mensagem autenticada", ReferenciaDaFonte = "mensagem:1", TrechoDaFonte = "conteúdo literal" };
        async Task<Guid> Capturar(NaturezaDoConteudo natureza, string texto)
        {
            var c = await captura.CapturarAsync(new() { IdEspacoDeConhecimento = espaco.Id, Tipo = TipoDeConhecimento.Fato, Conteudo = texto,
                Natureza = natureza, Sensibilidade = Sensibilidade.Pessoal, Justificativa = "registro explícito", Proveniencia = prova });
            return await captura.ConfirmarAsync(espaco.Id, null, c.Id, c.Revisao, prova);
        }
        var original = await Capturar(NaturezaDoConteudo.ConclusaoDoAgente, "hipótese do agente");
        var item = (await servico.ObterPorIdAsync(original))!;
        var corrigido = (await servico.CorrigirAsync(original, item with { Conteudo = "correção do usuário", Natureza = NaturezaDoConteudo.DitoPeloUsuario }))!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => servico.ConfirmarAsync(original, corrigido.Revisao, prova));
        Assert.Equal(NaturezaDoConteudo.ConclusaoDoAgente, corrigido.NaturezaDeOrigem);
        Assert.Equal(NaturezaDoConteudo.DitoPeloUsuario, corrigido.Natureza);
        var antes = System.Text.Json.JsonSerializer.Serialize(await servico.ObterPorIdAsync(original));
        var novo = await Capturar(NaturezaDoConteudo.DitoPeloUsuario, "o usuário afirma com suas próprias palavras");
        await scope.ServiceProvider.GetRequiredService<IRelacaoDeConhecimentoAppService>().RelacionarAsync(espaco.Id, null, novo, original, TipoDeRelacao.DerivadoDe, prova);
        Assert.Equal(antes, System.Text.Json.JsonSerializer.Serialize(await servico.ObterPorIdAsync(original)));
        var declarado = (await servico.ObterPorIdAsync(novo))!;
        Assert.Equal(StatusDoConhecimento.Confirmado, declarado.Status);
        Assert.Equal(NaturezaDoConteudo.DitoPeloUsuario, declarado.NaturezaDeOrigem);
        Assert.Equal(NaturezaDoConteudo.DitoPeloUsuario, declarado.Natureza);
        await using var verificar = banco.Contexto();
        var relacao = await verificar.RelacoesDeConhecimento.SingleAsync();
        Assert.Equal(TipoDeRelacao.DerivadoDe, relacao.Tipo); Assert.Equal(original, relacao.IdDestino);
    }
}
