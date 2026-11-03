using System.Text.Json;
using Dante.Application.CapturaDeConhecimento;
using Dante.Application.DocumentosFonte;
using Dante.Application.SegurancaDoBrain;
using Dante.Domain.CapturaDeConhecimento;
using Dante.Domain.Conhecimentos;
using Dante.Domain.DocumentosFonte;
using Dante.Domain.EspacosDeConhecimento;
using Dante.Worker.Brain;
using Dante.Worker.Telegram;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Dante.Tests;

public sealed class AutoriaDasFontesTests
{
    [Fact]
    public void ContextoAutenticadoPreservaAutorEBotEIgnoraOutraConversaOuTopico()
    {
        var mensagem = BrainMcpTests.Mensagem("registre") with
        { ReplyToMessage = BrainMcpTests.Mensagem("trecho", 122) with { From = new(999, IsBot: true) } };
        var reconstruida = mensagem.ParaFerramentas().ParaMensagem();
        Assert.Equal(new TelegramUser(999, true), reconstruida.ReplyToMessage!.From);
        Assert.Equal("trecho", reconstruida.ReplyToMessage.Text);
        Assert.Null((mensagem with { ReplyToMessage = mensagem.ReplyToMessage with { Chat = new(77) } }).ParaFerramentas().TrechoSelecionado);
        Assert.Null((mensagem with { ReplyToMessage = mensagem.ReplyToMessage with { MessageThreadId = 7 } }).ParaFerramentas().TrechoSelecionado);
        Assert.Null((mensagem with { ReplyToMessage = mensagem.ReplyToMessage with { From = null } }).ParaFerramentas().ParaMensagem().ReplyToMessage!.From);
    }

    [PostgreSqlFact]
    public async Task MatrizMcpAutorFerramentaENaturezaValidaOrigemSemAlterarAlvosRecusados()
    {
        await using var banco = await Banco.CriarAsync(); using var app = BrainMcpTests.CriarApp(banco.ConnectionString);
        var brain = app.GetRequiredService<TelegramBrain>(); await BrainMcpTests.SelecionarAsync(brain);
        var ops = app.GetRequiredService<OperacoesMcpDoBrain>(); var registro = app.GetRequiredService<RegistroDeOperacoesBrain>();
        var escopo = (await brain.ResolverEscopoMcpAsync(BrainMcpTests.Mensagem("pedido")))!;
        var numero = 0;
        foreach (var autor in new TelegramUser?[] { new(42), new(999, true), new(7), null })
        foreach (var natureza in Enum.GetValues<NaturezaDoConteudo>().Where(x => x != NaturezaDoConteudo.Desconhecida))
        foreach (var ferramenta in new[] { "brain_capturar_conhecimento", "brain_corrigir_candidato", "brain_atualizar_conhecimento" })
        {
            numero++; var texto = $"Trecho exclusivo da matriz {numero}";
            var mensagem = BrainMcpTests.Mensagem("registre o trecho citado", 200 + numero) with
            { ReplyToMessage = BrainMcpTests.Mensagem(texto, 100 + numero) with { From = autor } };
            // Exercita o mesmo transporte de metadados das sessões reais.
            mensagem = mensagem.ParaFerramentas().ParaMensagem(); registro.RegistrarSessao("M" + numero, mensagem);
            var prova = new ProvenienciaDoConhecimento(escopo.Acesso.IdUsuario, "origem inicial", "mensagem:inicial", trechoDaFonte: "original");
            var candidato = new CandidatoDeConhecimento(escopo.Acesso.IdEspacoDeConhecimento, escopo.Acesso.IdProjeto,
                TipoDeConhecimento.Nota, $"original candidato {numero}", Sensibilidade.Pessoal, natureza, ModoDeCaptura.Explicita,
                "teste", prova, DateTimeOffset.UtcNow);
            var conhecimento = new Conhecimento(escopo.Acesso.IdEspacoDeConhecimento, escopo.Acesso.IdProjeto,
                TipoDeConhecimento.Nota, $"original conhecimento {numero}", null, StatusDoConhecimento.Inferido, null,
                Sensibilidade.Pessoal, null, null, [], prova, DateTimeOffset.UtcNow, NaturezaDoConteudo.DitoPeloUsuario);
            await using (var db = banco.Contexto()) { db.AddRange(candidato, conhecimento); await db.SaveChangesAsync(); }
            var id = ferramenta == "brain_atualizar_conhecimento" ? conhecimento.Id : candidato.Id;
            var args = new Dictionary<string, object> { ["conteudo"] = texto, ["natureza"] = natureza.ToString(), ["justificativa"] = "teste de autoria" };
            if (ferramenta != "brain_capturar_conhecimento") { args["id"] = id; args["revisao"] = 1; }
            Task<object?> Operar() => ops.ExecutarAsync(escopo, mensagem, "M" + numero, "codex", ferramenta, JsonSerializer.SerializeToElement(args));
            var permitido = autor?.Id == 42 || natureza == NaturezaDoConteudo.ConclusaoDoAgente;
            if (!permitido) await Assert.ThrowsAsync<ArgumentException>(Operar);
            else
            {
                await Operar();
                if (ferramenta == "brain_atualizar_conhecimento")
                    Assert.Contains("atualizado", await brain.AtenderAsync(BrainMcpTests.Mensagem("confirmar", 400 + numero), "confirmar", "M" + numero));
            }
            await using var verificar = banco.Contexto();
            var salvo = await verificar.CandidatosDeConhecimento.SingleAsync(x => x.Id == candidato.Id);
            var item = await verificar.Conhecimentos.SingleAsync(x => x.Id == conhecimento.Id);
            Assert.Equal(permitido && ferramenta == "brain_corrigir_candidato" ? texto : $"original candidato {numero}", salvo.Conteudo);
            Assert.Equal(permitido && ferramenta == "brain_atualizar_conhecimento" ? texto : $"original conhecimento {numero}", item.Conteudo);
            if (permitido)
            {
                var autoria = autor?.Id == 42 ? "Usuario" : autor?.IsBot == true ? "Agente" : "Desconhecida";
                var origem = ferramenta == "brain_atualizar_conhecimento" ? item.Proveniencia.Origem :
                    ferramenta == "brain_corrigir_candidato" ? salvo.Proveniencia.Origem :
                    (await verificar.CandidatosDeConhecimento.SingleAsync(x => x.Conteudo == texto)).Proveniencia.Origem;
                Assert.Contains("autoria-da-fonte:" + autoria, origem);
            }
            else Assert.False(await verificar.CandidatosDeConhecimento.AnyAsync(x => x.Conteudo == texto));
        }
        Assert.Equal(36, numero);
    }

    [PostgreSqlFact]
    public async Task ConversaNaturalMantemAutoriaEConfirmacaoNaoTransformaTextoDoBotEmFato()
    {
        await using var banco = await Banco.CriarAsync(); using var app = BrainMcpTests.CriarApp(banco.ConnectionString);
        var brain = app.GetRequiredService<TelegramBrain>(); await BrainMcpTests.SelecionarAsync(brain);
        var numero = 0;
        foreach (var autor in new TelegramUser?[] { new(42), new(999, true), new(7), null })
        {
            numero++; var texto = $"Resposta selecionada {numero}";
            var mensagem = BrainMcpTests.Mensagem("registre no Brain", 500 + numero) with
            { ReplyToMessage = BrainMcpTests.Mensagem(texto, 490 + numero) with { From = autor } };
            var resposta = await brain.AtenderAsync(mensagem, mensagem.Text!);
            if (autor is null || autor.Id == 7)
            {
                Assert.Contains("Só registro", resposta);
                await using var db = banco.Contexto(); Assert.False(await db.CandidatosDeConhecimento.AnyAsync(x => x.Conteudo == texto));
                continue;
            }
            Assert.Contains("candidato", resposta);
            if (autor.IsBot) Assert.Contains("não confirmado", resposta);
            var consolidacao = await brain.AtenderAsync(BrainMcpTests.Mensagem("confirmar", 600 + numero), "confirmar");
            if (autor.IsBot) Assert.Contains("conclusão do agente, não confirmado", consolidacao);
            await using var verificar = banco.Contexto();
            var candidato = await verificar.CandidatosDeConhecimento.SingleAsync(x => x.Conteudo == texto);
            var item = await verificar.Conhecimentos.SingleAsync(x => x.Conteudo == texto);
            Assert.Equal(autor.IsBot ? NaturezaDoConteudo.ConclusaoDoAgente : NaturezaDoConteudo.FonteSelecionada, candidato.Natureza);
            Assert.Equal(autor.IsBot ? StatusDoConhecimento.Inferido : StatusDoConhecimento.Confirmado, item.Status);
            Assert.Equal(candidato.Natureza, item.NaturezaDeOrigem); Assert.Equal(candidato.Natureza, item.Natureza);
            Assert.Equal(TipoDeConhecimento.Nota, item.Tipo);
            Assert.Contains("autoria-da-fonte:" + (autor.IsBot ? "Agente" : "Usuario"), candidato.Historico[0].Proveniencia.Origem);
            Assert.Equal(EstadoDoCandidato.Promovido, candidato.Estado);
        }
    }

    [PostgreSqlFact]
    public async Task DocumentoPersisteAutoriaEGeraNaturezaCorrespondenteAposNovaLeitura()
    {
        await using var banco = await Banco.CriarAsync(); using var app = BrainMcpTests.CriarApp(banco.ConnectionString);
        var brain = app.GetRequiredService<TelegramBrain>(); await BrainMcpTests.SelecionarAsync(brain);
        var escopo = (await brain.ResolverEscopoMcpAsync(BrainMcpTests.Mensagem("pedido")))!;
        foreach (var autoria in new[] { AutoriaDaFonte.Usuario, AutoriaDaFonte.Agente })
        {
            Guid id;
            using (var scope = EscoposBrainDeTeste.Criar(app, escopo.Acesso.IdUsuario, escopo.Acesso.IdEspacoDeConhecimento, escopo.Acesso.IdProjeto))
                id = (await scope.ServiceProvider.GetRequiredService<DocumentoFonteAppService>().ImportarAsync(escopo.Acesso, "nota:" + autoria, "markdown", "# Texto de " + autoria, autoria)).Id;
            using var leitura = EscoposBrainDeTeste.Criar(app, escopo.Acesso.IdUsuario, escopo.Acesso.IdEspacoDeConhecimento, escopo.Acesso.IdProjeto);
            var fontes = leitura.ServiceProvider.GetRequiredService<DocumentoFonteAppService>();
            Assert.Equal(autoria, (await fontes.ListarAsync(escopo.Acesso)).Single(x => x.Id == id).Autoria);
            var candidato = await fontes.GerarCandidatoAsync(escopo.Acesso, id, 1, 0);
            Assert.Equal(autoria == AutoriaDaFonte.Usuario ? NaturezaDoConteudo.FonteSelecionada : NaturezaDoConteudo.ConclusaoDoAgente, candidato.Natureza);
            Assert.Contains("autoria-da-fonte:" + autoria, candidato.Historico[0].Proveniencia.Origem);
            var captura = leitura.ServiceProvider.GetRequiredService<ICapturaDeConhecimentoAppService>();
            var consolidado = await captura.ConfirmarAsync(escopo.Acesso.IdEspacoDeConhecimento, escopo.Acesso.IdProjeto, candidato.Id, candidato.Revisao,
                new() { IdResponsavel = escopo.Acesso.IdUsuario, Origem = "confirmação", ReferenciaDaFonte = "mensagem:confirmar", TrechoDaFonte = "confirmar" });
            await using var db = banco.Contexto();
            Assert.Equal(autoria == AutoriaDaFonte.Usuario ? StatusDoConhecimento.Confirmado : StatusDoConhecimento.Inferido,
                (await db.Conhecimentos.SingleAsync(x => x.Id == consolidado)).Status);
        }
    }

    [PostgreSqlFact]
    public async Task MigrationPreservaDocumentoLegadoSemDeduzirAutoriaERecusaCandidatoAteReimportacao()
    {
        await using var banco = await Banco.CriarAsync(); var espaco = new EspacoDeConhecimento(Guid.NewGuid(), "Legado");
        var fonte = new DocumentoFonte(espaco.Id, null, "nota:legado", "markdown", "# Texto anterior", AutoriaDaFonte.Usuario,
            Sensibilidade.Pessoal, espaco.IdUsuario, DateTimeOffset.UtcNow);
        await using (var db = banco.Contexto())
        {
            db.AddRange(espaco, fonte); await db.SaveChangesAsync();
            var migrator = db.GetService<IMigrator>(); var anterior = db.Database.GetMigrations().Last(x => string.CompareOrdinal(x, "20261102150504_AutoriaDasFontes") < 0);
            await migrator.MigrateAsync(anterior); await migrator.MigrateAsync();
        }
        await using (var verificar = banco.Contexto())
        {
            var legado = await verificar.Set<DocumentoFonte>().SingleAsync();
            Assert.Equal(AutoriaDaFonte.Desconhecida, legado.Autoria);
            Assert.Equal(fonte.Conteudo, legado.Conteudo); Assert.Equal(fonte.Hash, legado.Hash); Assert.Equal(fonte.Revisao, legado.Revisao);
            Assert.Equal(fonte.IdResponsavel, legado.IdResponsavel); Assert.Equal(fonte.AtualizadoEm.ToUnixTimeMilliseconds(), legado.AtualizadoEm.ToUnixTimeMilliseconds());
        }
        using var app = BrainMcpTests.CriarApp(banco.ConnectionString);
        using var scope = EscoposBrainDeTeste.Criar(app, espaco.IdUsuario, espaco.Id);
        var acesso = new AcessoAoBrain(espaco.IdUsuario, espaco.Id, null); var fontes = scope.ServiceProvider.GetRequiredService<DocumentoFonteAppService>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fontes.GerarCandidatoAsync(acesso, fonte.Id, 1, 0));
        await Assert.ThrowsAsync<ArgumentException>(() => fontes.ImportarAsync(acesso, "nota:nova", "texto", "texto", AutoriaDaFonte.Desconhecida));
        var reimportada = await fontes.ImportarAsync(acesso, fonte.Origem, fonte.Formato, fonte.Conteudo, AutoriaDaFonte.Agente, revisaoEsperada: 1);
        Assert.Equal(2, reimportada.Revisao); Assert.Equal(fonte.Hash, reimportada.Hash);
        Assert.Equal(NaturezaDoConteudo.ConclusaoDoAgente, (await fontes.GerarCandidatoAsync(acesso, fonte.Id, 2, 0)).Natureza);
    }
}
