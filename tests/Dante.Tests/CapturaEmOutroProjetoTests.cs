using System.Text.Json;
using Dante.Application.EspacosDeConhecimento;
using Dante.Application.Projetos;
using Dante.Application.SegurancaDoBrain;
using Dante.Domain.CapturaDeConhecimento;
using Dante.Domain.Conhecimentos;
using Dante.Worker.Brain;
using Dante.Worker.Telegram;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dante.Tests;

// #223: destino do conhecimento diferente do contexto ativo, sem trocar o contexto ativo.
public sealed class CapturaEmOutroProjetoTests
{
    private const string Pedido = "registre isso como melhoria do Dante: detectar automaticamente qual etapa do projeto está sendo trabalhada e recuperar o contexto relacionado";
    private const string Melhoria = "detectar automaticamente qual etapa do projeto está sendo trabalhada e recuperar o contexto relacionado";

    [PostgreSqlFact]
    public async Task ComFitnessAtivoRegistraNoDanteSemTrocarOContextoAtivo()
    {
        await using var c = await Cenario.FitnessAtivoAsync();
        var antes = c.Escopo;
        Assert.Equal("Fitness App", antes.Projeto);

        using var resposta = await c.OperarAsync("brain_capturar_em_outro_projeto", new
        {
            projeto = "Dante", tipo = "Nota", tags = new[] { "melhoria" }, conteudo = Melhoria, natureza = "DitoPeloUsuario",
            justificativa = "Usuário pediu para registrar como melhoria do D.A.N.T.E."
        });
        var r = resposta.RootElement;
        Assert.True(r.GetProperty("destinoResolvido").GetBoolean());
        Assert.Equal("D.A.N.T.E.", r.GetProperty("destino").GetProperty("projeto").GetString());
        Assert.Equal("Fitness App", r.GetProperty("contextoAtivo").GetProperty("projeto").GetString());
        Assert.True(r.GetProperty("aguardandoConfirmacao").GetBoolean());
        Assert.Contains("D.A.N.T.E.", r.GetProperty("instrucao").GetString());
        Assert.Contains("contexto ativo continua Desenvolvimento / Fitness App", r.GetProperty("instrucao").GetString());

        var dante = await c.ProjetoAsync("D.A.N.T.E."); var fitness = await c.ProjetoAsync("Fitness App");
        await using (var db = c.Banco.Contexto())
        {
            var candidato = await db.CandidatosDeConhecimento.SingleAsync();
            Assert.Equal(dante.Id, candidato.IdProjeto); Assert.Equal(dante.IdEspacoDeConhecimento, candidato.IdEspacoDeConhecimento);
            Assert.Equal(EstadoDoCandidato.Pendente, candidato.Estado);
            // Proveniência preserva o contexto em que a observação surgiu.
            Assert.Contains($"percebido-em:{fitness.IdEspacoDeConhecimento}/{fitness.Id}", candidato.Historico[0].Proveniencia.Origem);
            Assert.Empty(await db.Conhecimentos.ToListAsync());
        }

        // A próxima interação continua no Fitness App: escopo resolvido, ferramenta de escopo e isolamento da listagem.
        await c.AssegurarContextoAtivoAsync(antes);
        using (var listagem = await c.OperarAsync("brain_listar_candidatos", new { }))
            Assert.Equal(0, listagem.RootElement.GetProperty("candidatos").GetArrayLength());

        var confirmacao = await c.Brain.AtenderAsync(BrainMcpTests.Mensagem("confirmar", 124), "confirmar", "S1");
        Assert.Contains("consolidado em Desenvolvimento / D.A.N.T.E.", confirmacao);
        Assert.Contains("contexto ativo continua Desenvolvimento / Fitness App", confirmacao);
        await using (var db = c.Banco.Contexto())
        {
            var conhecimento = await db.Conhecimentos.SingleAsync();
            Assert.Equal(dante.Id, conhecimento.IdProjeto); Assert.Equal(StatusDoConhecimento.Confirmado, conhecimento.Status);
        }
        await c.AssegurarContextoAtivoAsync(antes);
    }

    [PostgreSqlFact]
    public async Task ConclusaoDoAgenteNoOutroProjetoPedeAutorizacaoEContinuaInferida()
    {
        await using var c = await Cenario.FitnessAtivoAsync();
        using var resposta = await c.OperarAsync("brain_capturar_em_outro_projeto", new
        {
            projeto = "d.a.n.t.e", tipo = "Aprendizado", conteudo = "A troca de projeto ativo causa perda de contexto no Fitness.",
            natureza = "ConclusaoDoAgente", justificativa = "Observação do agente durante o trabalho no Fitness"
        });
        var r = resposta.RootElement;
        Assert.True(r.GetProperty("aguardandoAutorizacaoDeRegistro").GetBoolean());
        Assert.False(r.GetProperty("aguardandoConfirmacao").GetBoolean());
        Assert.Contains("Peça autorização para registrar", r.GetProperty("instrucao").GetString());
        Assert.Contains("registrado como inferido", await c.Brain.AtenderAsync(BrainMcpTests.Mensagem("confirmar", 124), "confirmar", "S1"));
        await using var db = c.Banco.Contexto();
        Assert.Equal(StatusDoConhecimento.Inferido, (await db.Conhecimentos.SingleAsync()).Status);
    }

    [PostgreSqlFact]
    public async Task DestinoAmbiguoParcialOuInexistenteNaoGravaNadaEPedeEscolha()
    {
        await using var c = await Cenario.FitnessAtivoAsync();
        // Segundo espaço com projeto homônimo; a criação pela conversa troca a seleção, então ela é restaurada.
        await c.DizerAsync("criar espaço Pessoal"); await c.DizerAsync("criar projeto Dante");
        await c.DizerAsync("usar espaço Desenvolvimento"); await c.DizerAsync("usar projeto Fitness App");
        await c.AssegurarContextoAtivoAsync(c.Escopo);

        async Task<JsonElement> Tentar(object args)
        {
            using var doc = await c.OperarAsync("brain_capturar_em_outro_projeto", args);
            return doc.RootElement.Clone();
        }
        var ambiguo = await Tentar(new { projeto = "Dante", conteudo = Melhoria, natureza = "DitoPeloUsuario", justificativa = "pedido do usuário" });
        Assert.False(ambiguo.GetProperty("destinoResolvido").GetBoolean()); Assert.Equal("ambiguo", ambiguo.GetProperty("motivo").GetString());
        Assert.Equal(2, ambiguo.GetProperty("opcoes").GetArrayLength());
        var parcial = await Tentar(new { projeto = "Fit", conteudo = Melhoria, natureza = "DitoPeloUsuario", justificativa = "pedido do usuário" });
        Assert.Equal("confirmacao_necessaria", parcial.GetProperty("motivo").GetString());
        Assert.Equal("Fitness App", Assert.Single(parcial.GetProperty("opcoes").EnumerateArray()).GetProperty("projeto").GetString());
        var inexistente = await Tentar(new { projeto = "Inexistente", conteudo = Melhoria, natureza = "DitoPeloUsuario", justificativa = "pedido do usuário" });
        Assert.Equal("nao_encontrado", inexistente.GetProperty("motivo").GetString());
        await using (var db = c.Banco.Contexto()) Assert.Empty(await db.CandidatosDeConhecimento.ToListAsync());

        // Com o espaço informado, o mesmo nome deixa de ser ambíguo.
        var escolhido = await Tentar(new { espaco = "pessoal", projeto = "Dante", conteudo = Melhoria, natureza = "DitoPeloUsuario", justificativa = "pedido do usuário" });
        Assert.Equal("Pessoal", escolhido.GetProperty("destino").GetProperty("espaco").GetString());
        var pessoal = await c.ProjetoAsync("Dante");
        await using (var db = c.Banco.Contexto()) Assert.Equal(pessoal.Id, (await db.CandidatosDeConhecimento.SingleAsync()).IdProjeto);
        await c.AssegurarContextoAtivoAsync(c.Escopo);
    }

    [PostgreSqlFact]
    public async Task ProjetoDeOutroUsuarioEDestinoAtivoSaoRecusadosSemGravar()
    {
        await using var c = await Cenario.FitnessAtivoAsync();
        // Projeto "Segredo" de outro usuário: nunca é candidato a destino.
        var outro = new IdentidadeDoBrain(AutorizacaoDoBrain.TenantLocal, Guid.NewGuid());
        EspacoDeConhecimentoDto espacoAlheio;
        using (var s = c.App.CreateScope())
        {
            s.ServiceProvider.GetRequiredService<AutorizacaoDoBrain>().Estabelecer(outro);
            espacoAlheio = await s.ServiceProvider.GetRequiredService<IEspacoDeConhecimentoAppService>().AdicionarAsync(new() { IdUsuario = outro.IdUsuario, Nome = "Desenvolvimento" });
        }
        using (var s = c.App.CreateScope())
        {
            s.ServiceProvider.GetRequiredService<AutorizacaoDoBrain>().Estabelecer(outro, new AcessoAoBrain(outro.IdUsuario, espacoAlheio.Id, null));
            await s.ServiceProvider.GetRequiredService<IProjetoAppService>().AdicionarAsync(new() { IdEspacoDeConhecimento = espacoAlheio.Id, Nome = "Segredo" });
        }
        using (var alheio = await c.OperarAsync("brain_capturar_em_outro_projeto", new { projeto = "Segredo", conteudo = Melhoria, natureza = "DitoPeloUsuario", justificativa = "pedido" }))
        {
            Assert.Equal("nao_encontrado", alheio.RootElement.GetProperty("motivo").GetString());
            Assert.Equal(0, alheio.RootElement.GetProperty("opcoes").GetArrayLength());
        }

        var ativo = await Assert.ThrowsAsync<ErroDoBrainMcp>(() => c.OperarAsync("brain_capturar_em_outro_projeto",
            new { projeto = "Fitness App", conteudo = Melhoria, natureza = "DitoPeloUsuario", justificativa = "pedido" }));
        Assert.Equal(CategoriaDeErroMcp.EntradaInvalida, ativo.Categoria); Assert.Equal(ErrosDoBrainMcp.DetalheDestinoAtivo, ativo.Detalhe);
        // Classificação acima do teto do agente continua recusada também no destino.
        var secreto = await Assert.ThrowsAsync<ErroDoBrainMcp>(() => c.OperarAsync("brain_capturar_em_outro_projeto",
            new { projeto = "Dante", conteudo = Melhoria, natureza = "DitoPeloUsuario", justificativa = "pedido", sensibilidade = "Secreto" }));
        Assert.Equal(CategoriaDeErroMcp.EntradaInvalida, secreto.Categoria);
        // Texto que não está na mensagem do usuário não vira DitoPeloUsuario no destino.
        var inventado = await Assert.ThrowsAsync<ErroDoBrainMcp>(() => c.OperarAsync("brain_capturar_em_outro_projeto",
            new { projeto = "Dante", conteudo = "conteúdo que o usuário não escreveu", natureza = "DitoPeloUsuario", justificativa = "pedido" }));
        Assert.Equal(ErrosDoBrainMcp.DetalheOrigemLiteral, inventado.Detalhe);
        await using (var db = c.Banco.Contexto()) Assert.Empty(await db.CandidatosDeConhecimento.ToListAsync());
        await c.AssegurarContextoAtivoAsync(c.Escopo);
    }

    private sealed class Cenario(Banco banco, ServiceProvider app, TelegramBrain brain, TelegramBrain.EscopoBrainDaSessao escopo) : IAsyncDisposable
    {
        public Banco Banco => banco; public ServiceProvider App => app; public TelegramBrain Brain => brain;
        public TelegramBrain.EscopoBrainDaSessao Escopo => escopo;
        public static async Task<Cenario> FitnessAtivoAsync()
        {
            var banco = await Dante.Tests.Banco.CriarAsync(); ServiceProvider? app = null;
            try
            {
                app = BrainMcpTests.CriarApp(banco.ConnectionString); var brain = app.GetRequiredService<TelegramBrain>();
                await BrainMcpTests.SelecionarAsync(brain); // Desenvolvimento / D.A.N.T.E.
                Assert.Contains("selecionado", await brain.AtenderAsync(BrainMcpTests.Mensagem("criar projeto Fitness App"), "criar projeto Fitness App"));
                var escopo = (await brain.ResolverEscopoMcpAsync(BrainMcpTests.Mensagem(Pedido)))!;
                app.GetRequiredService<RegistroDeOperacoesBrain>().RegistrarSessao("S1", BrainMcpTests.Mensagem(Pedido));
                return new(banco, app, brain, escopo);
            }
            catch { app?.Dispose(); await banco.DisposeAsync(); throw; }
        }
        public async Task<JsonDocument> OperarAsync(string nome, object args) => JsonDocument.Parse(JsonSerializer.Serialize(
            await app.GetRequiredService<OperacoesMcpDoBrain>().ExecutarAsync(escopo, BrainMcpTests.Mensagem(Pedido), "S1", "claude", nome, JsonSerializer.SerializeToElement(args))));
        public async Task DizerAsync(string texto) => Assert.NotNull(await brain.AtenderAsync(BrainMcpTests.Mensagem(texto), texto));
        public async Task<Dante.Domain.Projetos.Projeto> ProjetoAsync(string nome)
        { await using var db = banco.Contexto(); return await db.Projetos.SingleAsync(p => p.Nome == nome && db.EspacosDeConhecimento.Any(e => e.Id == p.IdEspacoDeConhecimento && e.IdUsuario == escopo.Acesso.IdUsuario)); }
        public async Task AssegurarContextoAtivoAsync(TelegramBrain.EscopoBrainDaSessao esperado)
        {
            var atual = (await brain.ResolverEscopoMcpAsync(BrainMcpTests.Mensagem("próxima interação", 300)))!;
            Assert.Equal(esperado.Acesso, atual.Acesso); Assert.Equal("Fitness App", atual.Projeto);
            using var doc = await OperarAsync("brain_obter_escopo", new { });
            Assert.Equal("Fitness App", doc.RootElement.GetProperty("projeto").GetString());
        }
        public async ValueTask DisposeAsync() { app.Dispose(); await banco.DisposeAsync(); }
    }
}
