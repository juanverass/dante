using System.Text.Json;
using Dante.Domain.Conhecimentos;
using Dante.Domain.Projetos;
using Dante.Worker.Brain;
using Dante.Worker.Telegram;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dante.Tests;

public sealed class BrainMcpConhecimentoTests
{
    [PostgreSqlFact]
    public async Task AtualizacaoConfirmadaPreservaIdHistoricoESensibilidadeSemCriarDuplicata()
    {
        await using var a = await Ambiente.CriarAsync();
        var alvo = a.Novo("arquitetura antiga", Sensibilidade.Trabalho, dados: "{\"titulo\":\"antigo\",\"referencia\":\"README.md\"}");
        alvo.Confirmar(1, a.Prova, DateTimeOffset.UtcNow);
        await a.GravarAsync(alvo);
        var pedido = new { id = alvo.Id, revisao = 2, titulo = "Arquitetura atual", conteudo = "arquitetura investigada no repositório", natureza = "ConclusaoDoAgente", justificativa = "análise do README" };
        await a.OperarAsync("brain_atualizar_conhecimento", pedido);
        await using (var db = a.Banco.Contexto()) Assert.Equal("arquitetura antiga", (await db.Conhecimentos.SingleAsync()).Conteudo);
        Assert.Null(await a.Brain.AtenderAsync(BrainMcpTests.Mensagem("confirmar") with { From = new(7) }, "confirmar"));
        Assert.Contains("cancelada", await a.ConfirmarAsync("cancelar"));
        await using (var db = a.Banco.Contexto()) Assert.Equal(2, (await db.Conhecimentos.SingleAsync()).Revisao);
        await a.OperarAsync("brain_atualizar_conhecimento", pedido);
        Assert.Contains("mesmo ID", await a.ConfirmarAsync());
        await using (var db = a.Banco.Contexto())
        {
            var atual = await db.Conhecimentos.SingleAsync();
            Assert.Equal(alvo.Id, atual.Id); Assert.Equal(pedido.conteudo, atual.Conteudo);
            Assert.Equal("README.md", JsonDocument.Parse(atual.DadosEstruturados!).RootElement.GetProperty("referencia").GetString());
            Assert.Equal("Arquitetura atual", JsonDocument.Parse(atual.DadosEstruturados!).RootElement.GetProperty("titulo").GetString());
            Assert.Equal(3, atual.Revisao); Assert.Equal(Sensibilidade.Trabalho, atual.Sensibilidade);
            Assert.Equal(TipoDeConhecimento.Inferencia, atual.Tipo); Assert.Equal(StatusDoConhecimento.Inferido, atual.Status);
            Assert.Equal("arquitetura antiga", atual.Historico[0].Conteudo);
            Assert.Contains("confirmacao:", atual.Proveniencia.Origem);
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => a.OperarAsync("brain_atualizar_conhecimento", pedido));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => a.OperarAsync("brain_atualizar_conhecimento", new { id = alvo.Id, revisao = 3,
            conteudo = "nova", natureza = "ConclusaoDoAgente", justificativa = "análise", sensibilidade = "Publico" }));
    }

    [PostgreSqlFact]
    public async Task ConsolidacaoConfirmadaIncorporaFontesESubstituiSomenteDuplicatasSelecionadas()
    {
        await using var a = await Ambiente.CriarAsync();
        var destino = a.Novo("arquitetura de referência"); var duplicata = a.Novo("descrição equivalente"); var outro = a.Novo("outro assunto");
        await a.GravarAsync(destino, duplicata, outro);
        var pedido = new { id = destino.Id, revisao = 1, duplicatas = new[] { new { id = duplicata.Id, revisao = 1 } } };
        await a.OperarAsync("brain_consolidar_duplicatas", pedido);
        await using (var db = a.Banco.Contexto()) Assert.All(await db.Conhecimentos.ToListAsync(), x => Assert.Equal(StatusDoConhecimento.Inferido, x.Status));
        Assert.Contains("consolidadas", await a.ConfirmarAsync());
        await using (var db = a.Banco.Contexto())
        {
            var itens = await db.Conhecimentos.ToListAsync(); Assert.Equal(3, itens.Count);
            var alvo = itens.Single(x => x.Id == destino.Id); var substituida = itens.Single(x => x.Id == duplicata.Id);
            Assert.Equal(destino.Conteudo, alvo.Conteudo); Assert.Equal(StatusDoConhecimento.Inferido, alvo.Status);
            Assert.Contains(alvo.Historico, x => x.Proveniencia.ReferenciaDaFonte == duplicata.Proveniencia.ReferenciaDaFonte);
            Assert.Equal(StatusDoConhecimento.Substituido, substituida.Status); Assert.Equal(destino.Id, substituida.IdConhecimentoSubstituto);
            Assert.Equal(1, itens.Single(x => x.Id == outro.Id).Revisao);
            Assert.Single(await db.RelacoesDeConhecimento.ToListAsync());
        }
        Assert.DoesNotContain("consolidadas", await a.ConfirmarAsync() ?? "");
    }

    [PostgreSqlFact]
    public async Task PropostasRevalidamRevisoesEscopoEAcessoSemAlteracaoParcial()
    {
        await using var a = await Ambiente.CriarAsync();
        var destino = a.Novo("destino"); var duplicata = a.Novo("duplicata"); var secreto = a.Novo("conteúdo protegido", Sensibilidade.Secreto);
        var projeto = new Projeto(a.Escopo.Acesso.IdEspacoDeConhecimento, "outro projeto"); var fora = a.Novo("outro escopo", projeto: projeto.Id);
        await using (var db = a.Banco.Contexto()) { db.Add(projeto); db.AddRange(destino, duplicata, secreto, fora); await db.SaveChangesAsync(); }
        foreach (var alvo in new[] { secreto, fora })
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => a.OperarAsync("brain_atualizar_conhecimento", new { id = alvo.Id, revisao = 1, conteudo = "x", natureza = "ConclusaoDoAgente", justificativa = "análise" }));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => a.OperarAsync("brain_consolidar_duplicatas", new { id = destino.Id, revisao = 1, duplicatas = new[] { new { id = alvo.Id, revisao = 1 } } }));
        }
        await Assert.ThrowsAsync<ArgumentException>(() => a.OperarAsync("brain_atualizar_conhecimento", new { id = destino.Id, revisao = 1, conteudo = "não dito", natureza = "DitoPeloUsuario", justificativa = "x" }));
        await Assert.ThrowsAsync<ArgumentException>(() => a.OperarAsync("brain_consolidar_duplicatas", new { id = destino.Id, revisao = 1, duplicatas = new[] { new { id = destino.Id, revisao = 1 } } }));
        await a.OperarAsync("brain_consolidar_duplicatas", new { id = destino.Id, revisao = 1, duplicatas = new[] { new { id = duplicata.Id, revisao = 1 } } });
        await using (var db = a.Banco.Contexto())
        {
            var atual = await db.Conhecimentos.SingleAsync(x => x.Id == duplicata.Id);
            atual.Corrigir(1, atual.Tipo, "edição concorrente", null, null, atual.Sensibilidade, null, null, [], a.Prova, DateTimeOffset.UtcNow); await db.SaveChangesAsync();
        }
        Assert.Contains("não foi confirmada", await a.ConfirmarAsync());
        await a.OperarAsync("brain_atualizar_conhecimento", new { id = destino.Id, revisao = 1, conteudo = "nova", natureza = "ConclusaoDoAgente", justificativa = "análise" });
        await a.Brain.AtenderAsync(BrainMcpTests.Mensagem("criar projeto Outro"), "criar projeto Outro", "S1");
        Assert.DoesNotContain("atualizado", await a.ConfirmarAsync() ?? "");
        await using (var db = a.Banco.Contexto())
        {
            Assert.Equal(1, (await db.Conhecimentos.SingleAsync(x => x.Id == destino.Id)).Revisao);
            Assert.Equal(StatusDoConhecimento.Inferido, (await db.Conhecimentos.SingleAsync(x => x.Id == duplicata.Id)).Status);
            Assert.Empty(await db.RelacoesDeConhecimento.ToListAsync());
        }
    }

    [PostgreSqlFact]
    public async Task ConsolidacaoRecusaReducaoDeSensibilidadeEConflitosSemEfeitoParcial()
    {
        await using var a = await Ambiente.CriarAsync();
        var destino = a.Novo("destino"); var sensivel = a.Novo("duplicata de trabalho", Sensibilidade.Trabalho); var conflito = a.Novo("contradição");
        await a.GravarAsync(destino, sensivel, conflito);
        await a.OperarAsync("brain_consolidar_duplicatas", new { id = destino.Id, revisao = 1, duplicatas = new[] { new { id = sensivel.Id, revisao = 1 } } });
        Assert.Contains("não foi confirmada", await a.ConfirmarAsync());
        await using (var db = a.Banco.Contexto())
        {
            var origem = await db.Conhecimentos.SingleAsync(x => x.Id == destino.Id);
            var outro = await db.Conhecimentos.SingleAsync(x => x.Id == conflito.Id);
            db.Add(new Dante.Domain.RelacoesDeConhecimento.RelacaoDeConhecimento(origem, outro,
                Dante.Domain.RelacoesDeConhecimento.TipoDeRelacao.Contradiz, a.Prova, DateTimeOffset.UtcNow)); await db.SaveChangesAsync();
        }
        await a.OperarAsync("brain_consolidar_duplicatas", new { id = destino.Id, revisao = 1, duplicatas = new[] { new { id = conflito.Id, revisao = 1 } } });
        Assert.Contains("não foi confirmada", await a.ConfirmarAsync());
        await using (var db = a.Banco.Contexto())
        {
            Assert.All(await db.Conhecimentos.ToListAsync(), x => { Assert.Equal(1, x.Revisao); Assert.Equal(StatusDoConhecimento.Inferido, x.Status); });
            Assert.Equal(Dante.Domain.RelacoesDeConhecimento.TipoDeRelacao.Contradiz, (await db.RelacoesDeConhecimento.SingleAsync()).Tipo);
        }
    }

    private sealed class Ambiente(Banco banco, ServiceProvider app, TelegramBrain brain, TelegramBrain.EscopoBrainDaSessao escopo) : IAsyncDisposable
    {
        public Banco Banco => banco;
        public TelegramBrain Brain => brain;
        public TelegramBrain.EscopoBrainDaSessao Escopo => escopo;
        public ProvenienciaDoConhecimento Prova => new(escopo.Acesso.IdUsuario, "fonte verificável", "fonte:" + Guid.NewGuid(), trechoDaFonte: "evidência");
        public static async Task<Ambiente> CriarAsync()
        {
            var banco = await Dante.Tests.Banco.CriarAsync(); var app = BrainMcpTests.CriarApp(banco.ConnectionString); var brain = app.GetRequiredService<TelegramBrain>();
            await BrainMcpTests.SelecionarAsync(brain); var escopo = (await brain.ResolverEscopoMcpAsync(BrainMcpTests.Mensagem("pedido")))!;
            app.GetRequiredService<RegistroDeOperacoesBrain>().RegistrarSessao("S1", BrainMcpTests.Mensagem("pedido"));
            return new(banco, app, brain, escopo);
        }
        public Conhecimento Novo(string texto, Sensibilidade classe = Sensibilidade.Pessoal, Guid? projeto = null, string? dados = null) =>
            new(escopo.Acesso.IdEspacoDeConhecimento, projeto ?? escopo.Acesso.IdProjeto, TipoDeConhecimento.Fato, texto, dados,
                StatusDoConhecimento.Inferido, null, classe, null, null, [], Prova, DateTimeOffset.UtcNow);
        public async Task GravarAsync(params Conhecimento[] itens) { await using var db = banco.Contexto(); db.AddRange(itens); await db.SaveChangesAsync(); }
        public Task<object?> OperarAsync(string nome, object args) => app.GetRequiredService<OperacoesMcpDoBrain>().ExecutarAsync(escopo,
            BrainMcpTests.Mensagem("pedido"), "S1", "codex", nome, JsonSerializer.SerializeToElement(args));
        public Task<string?> ConfirmarAsync(string texto = "confirmar") => brain.AtenderAsync(BrainMcpTests.Mensagem(texto, 124), texto, "S1");
        public async ValueTask DisposeAsync() { app.Dispose(); await banco.DisposeAsync(); }
    }
}
