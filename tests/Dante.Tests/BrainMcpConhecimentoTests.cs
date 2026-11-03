using Dante.Domain.CapturaDeConhecimento;
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
            Assert.Equal(NaturezaDoConteudo.DitoPeloUsuario, atual.NaturezaDeOrigem); Assert.Equal(NaturezaDoConteudo.ConclusaoDoAgente, atual.Natureza);
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
            atual.Corrigir(1, atual.Tipo, "edição concorrente", null, null, atual.Sensibilidade, null, null, [], a.Prova, DateTimeOffset.UtcNow, NaturezaDoConteudo.DitoPeloUsuario); await db.SaveChangesAsync();
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

    // #249: cada ferramenta de escrita grava a sessão qualificada pela instância do Worker.
    [PostgreSqlFact]
    public async Task ProvenienciaDaCorrecaoDeCandidatoQualificaASessaoComAInstancia()
    {
        await using var a = await Ambiente.CriarAsync();
        await a.OperarAsync("brain_capturar_conhecimento", new { conteudo = "conclusão inicial", natureza = "ConclusaoDoAgente", justificativa = "análise" });
        Guid id; await using (var db = a.Banco.Contexto()) id = (await db.CandidatosDeConhecimento.SingleAsync()).Id;
        await a.OperarAsync("brain_corrigir_candidato", new { id, revisao = 1, conteudo = "conclusão corrigida", natureza = "ConclusaoDoAgente", justificativa = "revisão" });
        await using (var db = a.Banco.Contexto())
        {
            var candidato = await db.CandidatosDeConhecimento.SingleAsync();
            Assert.Equal(["capturado", "corrigido"], candidato.Historico.Select(x => x.Acao));
            Assert.All(candidato.Historico, x => Assert.Equal(a.OrigemMcp, x.Proveniencia.Origem));
        }
    }

    [PostgreSqlFact]
    public async Task ProvenienciaDoDescarteDeCandidatoQualificaASessaoComAInstancia()
    {
        await using var a = await Ambiente.CriarAsync();
        await a.OperarAsync("brain_capturar_conhecimento", new { conteudo = "conclusão descartável", natureza = "ConclusaoDoAgente", justificativa = "análise" });
        Guid id; await using (var db = a.Banco.Contexto()) id = (await db.CandidatosDeConhecimento.SingleAsync()).Id;
        await a.OperarAsync("brain_cancelar_candidato", new { id, revisao = 1 });
        Assert.Contains("descartado", await a.ConfirmarAsync());
        await using (var db = a.Banco.Contexto())
        {
            var descarte = (await db.CandidatosDeConhecimento.SingleAsync()).Historico[^1];
            Assert.Equal("descartado", descarte.Acao); Assert.Equal(a.OrigemMcp, descarte.Proveniencia.Origem);
            Assert.EndsWith(":124", descarte.Proveniencia.ReferenciaDaFonte);
        }
    }

    [PostgreSqlFact]
    public async Task ProvenienciaDaAtualizacaoDeConhecimentoQualificaASessaoEMantemAConfirmacao()
    {
        await using var a = await Ambiente.CriarAsync();
        var alvo = a.Novo("conteúdo anterior"); await a.GravarAsync(alvo);
        await a.OperarAsync("brain_atualizar_conhecimento", new { id = alvo.Id, revisao = 1, conteudo = "conteúdo revisado", natureza = "ConclusaoDoAgente", justificativa = "análise" });
        Assert.Contains("mesmo ID", await a.ConfirmarAsync());
        await using (var db = a.Banco.Contexto())
            Assert.Equal($"{a.OrigemMcp};confirmacao:124", (await db.Conhecimentos.SingleAsync()).Historico[^1].Proveniencia.Origem);
    }

    [PostgreSqlFact]
    public async Task ProvenienciaDaConsolidacaoQualificaASessaoComAInstancia()
    {
        await using var a = await Ambiente.CriarAsync();
        var destino = a.Novo("referência canônica"); var duplicata = a.Novo("referência duplicada"); await a.GravarAsync(destino, duplicata);
        await a.OperarAsync("brain_consolidar_duplicatas", new { id = destino.Id, revisao = 1, duplicatas = new[] { new { id = duplicata.Id, revisao = 1 } } });
        Assert.Contains("consolidadas", await a.ConfirmarAsync());
        await using (var db = a.Banco.Contexto())
        {
            var alvo = await db.Conhecimentos.SingleAsync(x => x.Id == destino.Id);
            // Fontes incorporadas mantêm a origem delas; o ato de consolidação leva a sessão MCP.
            Assert.Equal(a.OrigemMcp, alvo.Historico[^1].Proveniencia.Origem);
            Assert.Contains(alvo.Historico, x => x.Proveniencia.Origem == duplicata.Proveniencia.Origem);
        }
    }

    [PostgreSqlFact]
    public async Task ProvenienciaDaRelacaoQualificaASessaoComAInstancia()
    {
        await using var a = await Ambiente.CriarAsync();
        // ResolvidoPor exige Incidente → Solução no Domain (RelacaoDeConhecimento); o cenário respeita essa regra.
        var incidente = a.Novo("incidente observado", tipo: TipoDeConhecimento.Incidente);
        var solucao = a.Novo("solução aplicada", tipo: TipoDeConhecimento.Solucao); await a.GravarAsync(incidente, solucao);
        await a.OperarAsync("brain_criar_relacao", new { origem = incidente.Id, destino = solucao.Id, revisao_origem = 1, revisao_destino = 1, tipo = "ResolvidoPor" });
        Assert.Contains("Relação", await a.ConfirmarAsync());
        await using (var db = a.Banco.Contexto())
        {
            var relacao = await db.RelacoesDeConhecimento.SingleAsync();
            Assert.Equal(Dante.Domain.RelacoesDeConhecimento.TipoDeRelacao.ResolvidoPor, relacao.Tipo);
            Assert.Equal(a.OrigemMcp, relacao.Proveniencia.Origem);
        }
    }

    [PostgreSqlFact]
    public async Task SnapshotUsaOFormatoNovoEOmiteAOrigemDaVersaoAnterior()
    {
        await using var a = await Ambiente.CriarAsync();
        object Estado(int revisao, string progresso) => new { revisao, objetivo = "validar #249", tarefa = "proveniência", progresso, ultimo_resultado = "ok" };
        await a.OperarAsync("brain_atualizar_contexto_de_trabalho", Estado(0, "primeira versão"));
        await a.OperarAsync("brain_atualizar_contexto_de_trabalho", Estado(1, "segunda versão"));
        var atual = (Dante.Application.ContextosDeTrabalho.ContextoDeTrabalhoDto)(await a.OperarAsync("brain_obter_contexto_de_trabalho", new { }))!;
        Assert.Equal(2, atual.Revisao); Assert.StartsWith(a.OrigemMcp + ";", atual.Origem);
        Assert.NotNull(atual.AuditoriaAnterior); Assert.Equal("[origem anterior omitida]", atual.AuditoriaAnterior!.Origem);
    }

    [PostgreSqlFact]
    public async Task PromocaoECorrecaoMcpPreservamOrigemDoAgenteENovaDeclaracaoDerivadaEConfirmavel()
    {
        await using var a = await Ambiente.CriarAsync();
        await a.OperarAsync("brain_capturar_conhecimento", new { conteudo = "hipótese original", natureza = "ConclusaoDoAgente", justificativa = "análise" });
        Guid candidato;
        await using (var db = a.Banco.Contexto()) candidato = (await db.CandidatosDeConhecimento.SingleAsync()).Id;
        await a.OperarAsync("brain_confirmar_candidato", new { id = candidato, revisao = 1 });
        await a.ConfirmarAsync();
        Guid original;
        await using (var db = a.Banco.Contexto())
        {
            var item = await db.Conhecimentos.SingleAsync(); original = item.Id;
            Assert.Equal(NaturezaDoConteudo.ConclusaoDoAgente, item.NaturezaDeOrigem);
            Assert.Equal(NaturezaDoConteudo.ConclusaoDoAgente, item.Natureza);
            Assert.Equal(StatusDoConhecimento.Inferido, item.Status);
        }
        await a.OperarAsync("brain_atualizar_conhecimento", new { id = original, revisao = 1, conteudo = "pedido", natureza = "DitoPeloUsuario", justificativa = "texto literal na mensagem atual" });
        await a.ConfirmarAsync();
        string antes;
        await using (var db = a.Banco.Contexto())
        {
            var item = await db.Conhecimentos.SingleAsync();
            Assert.Equal(NaturezaDoConteudo.ConclusaoDoAgente, item.NaturezaDeOrigem);
            Assert.Equal(NaturezaDoConteudo.DitoPeloUsuario, item.Natureza);
            Assert.Throws<InvalidOperationException>(() => item.Confirmar(item.Revisao, a.Prova, DateTimeOffset.UtcNow));
            antes = JsonSerializer.Serialize(item);
        }
        await a.OperarAsync("brain_capturar_conhecimento", new { conteudo = "pedido", natureza = "DitoPeloUsuario", justificativa = "afirmação explícita do usuário" });
        await using (var db = a.Banco.Contexto()) candidato = (await db.CandidatosDeConhecimento.SingleAsync(x => x.Natureza == NaturezaDoConteudo.DitoPeloUsuario)).Id;
        await a.OperarAsync("brain_confirmar_candidato", new { id = candidato, revisao = 1 }); await a.ConfirmarAsync();
        Guid novo;
        await using (var db = a.Banco.Contexto())
        {
            var item = await db.Conhecimentos.SingleAsync(x => x.Id != original); novo = item.Id;
            Assert.Equal(StatusDoConhecimento.Confirmado, item.Status); Assert.Equal(NaturezaDoConteudo.DitoPeloUsuario, item.NaturezaDeOrigem);
        }
        await a.OperarAsync("brain_criar_relacao", new { origem = novo, destino = original, revisao_origem = 2, revisao_destino = 2, tipo = "DerivadoDe" });
        await a.ConfirmarAsync();
        await using (var db = a.Banco.Contexto())
        {
            Assert.Equal(antes, JsonSerializer.Serialize(await db.Conhecimentos.SingleAsync(x => x.Id == original)));
            Assert.Equal(Dante.Domain.RelacoesDeConhecimento.TipoDeRelacao.DerivadoDe, (await db.RelacoesDeConhecimento.SingleAsync()).Tipo);
        }
    }

    [PostgreSqlFact]
    public async Task ConsolidacaoMcpDeAgenteEmDestinoConfirmadoPreservaNaturezaEEstadoDoDestino()
    {
        await using var a = await Ambiente.CriarAsync();
        var destino = a.Novo("declaração confirmada"); destino.Confirmar(1, a.Prova, DateTimeOffset.UtcNow);
        var duplicata = a.Novo("conclusão equivalente do agente", natureza: NaturezaDoConteudo.ConclusaoDoAgente);
        await a.GravarAsync(destino, duplicata);
        await a.OperarAsync("brain_consolidar_duplicatas", new { id = destino.Id, revisao = 2, duplicatas = new[] { new { id = duplicata.Id, revisao = 1 } } });
        await a.ConfirmarAsync();
        await using var db = a.Banco.Contexto();
        var alvo = await db.Conhecimentos.SingleAsync(x => x.Id == destino.Id);
        var fonte = await db.Conhecimentos.SingleAsync(x => x.Id == duplicata.Id);
        Assert.Equal(StatusDoConhecimento.Confirmado, alvo.Status);
        Assert.Equal(NaturezaDoConteudo.DitoPeloUsuario, alvo.NaturezaDeOrigem); Assert.Equal(NaturezaDoConteudo.DitoPeloUsuario, alvo.Natureza);
        Assert.Equal(JsonSerializer.Serialize(destino.Historico), JsonSerializer.Serialize(alvo.Historico.Take(2)));
        Assert.Equal(NaturezaDoConteudo.ConclusaoDoAgente, fonte.NaturezaDeOrigem); Assert.Equal(NaturezaDoConteudo.ConclusaoDoAgente, fonte.Natureza);
        Assert.Equal(StatusDoConhecimento.Substituido, fonte.Status);
        Assert.DoesNotContain(fonte.Historico, h => h.Status == StatusDoConhecimento.Confirmado);
    }

    private sealed class Ambiente(Banco banco, ServiceProvider app, TelegramBrain brain, TelegramBrain.EscopoBrainDaSessao escopo) : IAsyncDisposable
    {
        public Banco Banco => banco;
        public TelegramBrain Brain => brain;
        public TelegramBrain.EscopoBrainDaSessao Escopo => escopo;
        public string OrigemMcp => OperacoesMcpDoBrain.Origem("codex", brain.Instancia, "S1");
        public ProvenienciaDoConhecimento Prova => new(escopo.Acesso.IdUsuario, "fonte verificável", "fonte:" + Guid.NewGuid(), trechoDaFonte: "evidência");
        public static async Task<Ambiente> CriarAsync()
        {
            var banco = await Dante.Tests.Banco.CriarAsync(); ServiceProvider? app = null;
            try
            {
                app = BrainMcpTests.CriarApp(banco.ConnectionString); var brain = app.GetRequiredService<TelegramBrain>();
                await BrainMcpTests.SelecionarAsync(brain); var escopo = (await brain.ResolverEscopoMcpAsync(BrainMcpTests.Mensagem("pedido")))!;
                app.GetRequiredService<RegistroDeOperacoesBrain>().RegistrarSessao("S1", BrainMcpTests.Mensagem("pedido"));
                return new(banco, app, brain, escopo);
            }
            catch
            {
                // Sem Ambiente devolvido, ninguém chamaria DisposeAsync: o banco temporário ficaria abandonado (#246).
                app?.Dispose(); await banco.DisposeAsync(); throw;
            }
        }
        public Conhecimento Novo(string texto, Sensibilidade classe = Sensibilidade.Pessoal, Guid? projeto = null, string? dados = null,
            TipoDeConhecimento tipo = TipoDeConhecimento.Fato, NaturezaDoConteudo natureza = NaturezaDoConteudo.DitoPeloUsuario) =>
            new(escopo.Acesso.IdEspacoDeConhecimento, projeto ?? escopo.Acesso.IdProjeto, tipo, texto, dados,
                StatusDoConhecimento.Inferido, null, classe, null, null, [], Prova, DateTimeOffset.UtcNow, natureza);
        public async Task GravarAsync(params Conhecimento[] itens) { await using var db = banco.Contexto(); db.AddRange(itens); await db.SaveChangesAsync(); }
        public Task<object?> OperarAsync(string nome, object args) => app.GetRequiredService<OperacoesMcpDoBrain>().ExecutarAsync(escopo,
            BrainMcpTests.Mensagem("pedido"), "S1", "codex", nome, JsonSerializer.SerializeToElement(args));
        public Task<string?> ConfirmarAsync(string texto = "confirmar") => brain.AtenderAsync(BrainMcpTests.Mensagem(texto, 124), texto, "S1");
        public async ValueTask DisposeAsync() { app.Dispose(); await banco.DisposeAsync(); }
    }
}
