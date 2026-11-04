using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dante.Application.BuscaDoBrain;
using Dante.Application.SegurancaDoBrain;
using Dante.Domain.Agentes;
using Dante.Domain.CapturaDeConhecimento;
using Dante.Domain.Conhecimentos;
using Dante.Infrastructure.BuscaDoBrain;
using Dante.Worker.Brain;
using Dante.Worker.Jobs;
using Dante.Worker.Telegram;
using Microsoft.Extensions.DependencyInjection;

namespace Dante.Tests;

public sealed class SemanticaDaBuscaTests
{
    [Fact]
    public void SchemaExplicitaDiferencaSemIntroduzirModoOuMudarLimites()
    {
        var ferramenta = ServidorMcpDoBrain.Ferramentas.Single(x => x.Nome == "brain_buscar_conhecimento");
        Assert.Equal(["texto"], ferramenta.Obrigatorios);
        Assert.Equal(new[] { "texto", "natureza", "tipo", "tags", "limite", "deslocamento" }.Order(), ferramenta.Propriedades.Select(x => x.Key).Order());
        var texto = ferramenta.Propriedades["texto"]!;
        Assert.Equal("string", texto["type"]!.GetValue<string>());
        Assert.Equal(1, texto["minLength"]!.GetValue<int>()); Assert.Equal(2000, texto["maxLength"]!.GetValue<int>());
        var descricao = texto["description"]!.GetValue<string>();
        Assert.Contains("AND implícito", descricao); Assert.Contains("or para alternativas", descricao);
        Assert.Contains("aspas", descricao); Assert.Contains("não garante exclusão", descricao);
        Assert.Contains("até 16 termos", descricao); Assert.Contains("combina por OR", descricao);
        Assert.Contains("texto é preservado", ferramenta.Descricao);
        Assert.DoesNotContain("modo", ferramenta.Propriedades.Select(x => x.Key));
    }

    [PostgreSqlFact]
    public async Task Caso147MantemAndNoMcpERecuperaPorOrNoContextoAutomatico()
    {
        await using var c = await Cenario.CriarAsync();
        var referencia = await c.AdicionarAsync("O Brain do D.A.N.T.E. foi integrado com sucesso ao PostgreSQL 17.11 no homelab em 01/11/2026.", ["brain", "postgres"]);
        const string pergunta = "quando o Brain passou a usar Postgres?";
        Assert.Empty((await c.BuscarAsync(pergunta)).Resultados);
        Assert.Equal(pergunta, c.Consultas.Last()); // O MCP não aplica o extrator automático.
        Assert.Equal(referencia.Id, Assert.Single((await c.BuscarAsync("Brain Postgres")).Resultados).Item.Id);
        Assert.Equal(referencia.Id, Assert.Single((await c.BuscarAsync("Brain or passou or usar or Postgres")).Resultados).Item.Id);
        var contexto = await c.Brain.PrepararAsync(c.Mensagem, "contexto-147", pergunta);
        Assert.NotNull(contexto);
        Assert.Equal("brain or passou or usar or postgres", c.Consultas.Last());
        Assert.Equal(referencia.Id, Assert.Single(contexto.Pacote.Itens).Id);
        Assert.Contains(referencia.Conteudo!, contexto.Texto);
    }

    [PostgreSqlFact]
    public async Task AndOrFraseEHifenMantemResultadosReaisEIsolamento()
    {
        await using var c = await Cenario.CriarAsync();
        var ambos = await c.AdicionarAsync("Brain Postgres");
        var brain = await c.AdicionarAsync("Brain SQLite");
        var postgres = await c.AdicionarAsync("Postgres manutenção");
        await c.AdicionarAsync("Brain Postgres reservado", classe: Sensibilidade.Confidencial);
        await c.AdicionarAsync("Brain Postgres secreto", classe: Sensibilidade.Secreto);
        Assert.Equal(ambos.Id, Assert.Single((await c.BuscarAsync("Brain Postgres")).Resultados).Item.Id);
        var alternativas = await c.BuscarAsync("Brain or Postgres");
        Assert.Equal(new[] { ambos.Id, brain.Id, postgres.Id }.Order(), alternativas.Resultados.Select(x => x.Item.Id).Order());
        Assert.Equal("lexical", alternativas.Modo); Assert.All(alternativas.Resultados, x => Assert.True(x.ScoreLexical > 0));
        Assert.Equal(ambos.Id, Assert.Single((await c.BuscarAsync("\"Brain Postgres\"")).Resultados).Item.Id);
        // ts_rank_cd > 0 não impõe a negação reconhecida pelo parser. A #254 documenta
        // a limitação atual, sem alterar SQL/ranking para prometer exclusão.
        Assert.Equal(new[] { ambos.Id, brain.Id }.Order(), (await c.BuscarAsync("Brain -Postgres")).Resultados.Select(x => x.Item.Id).Order());
        // O Context Builder recebe o OR preparado pelo Telegram; nenhum dos dois termos pode ser obrigatório.
        // Evita repetir exatamente o conteúdo de um item no prompt: a deduplicação do
        // Context Builder o descartaria corretamente por já estar presente.
        var contexto = await c.Brain.PrepararAsync(c.Mensagem, "contexto-or", "explique Brain Postgres");
        Assert.NotNull(contexto);
        Assert.Equal(new[] { ambos.Id, brain.Id, postgres.Id }.Order(), contexto.Pacote.Itens.Select(x => x.Id).Order());
        Assert.DoesNotContain("reservado", contexto.Texto); Assert.DoesNotContain("secreto", contexto.Texto);
    }

    [PostgreSqlFact]
    public async Task OperadoresEEntradaIncompletaNaoExecutamSqlNemCausamErro()
    {
        await using var c = await Cenario.CriarAsync();
        var referencia = await c.AdicionarAsync("Brain Postgres");
        foreach (var texto in new[] { "\"", "-", "or", "\"Brain Postgres", "Brain or -Postgres", "Brain'); DROP TABLE brain_data.conhecimentos; --" })
        {
            var resultado = await c.BuscarAsync(texto);
            Assert.Equal("lexical", resultado.Modo);
            Assert.All(resultado.Resultados, x => Assert.Equal(referencia.Id, x.Item.Id));
            Assert.Equal(texto, c.Consultas.Last());
            // A tentativa de SQL não remove tabela/conteúdo: a busca posterior continua íntegra.
            Assert.Equal(referencia.Id, Assert.Single((await c.BuscarAsync("Brain Postgres")).Resultados).Item.Id);
            var contexto = await c.Brain.PrepararAsync(c.Mensagem, "operadores", texto);
            Assert.NotNull(contexto);
        }
    }

    [PostgreSqlFact]
    public async Task PreparoAutomaticoMantemPalavrasVaziasDeduplicacaoELimite16SemLimitarMcp()
    {
        await using var c = await Cenario.CriarAsync();
        var referencia = await c.AdicionarAsync("termo17");
        var termos = Enumerable.Range(1, 17).Select(i => $"termo{i:00}").ToArray();
        var mensagem = "quando PARA como the com TERMO01 termo01 " + string.Join(' ', termos.Skip(1));
        var contexto = await c.Brain.PrepararAsync(c.Mensagem, "limite-termos", mensagem);
        Assert.NotNull(contexto); Assert.Empty(contexto.Pacote.Itens);
        Assert.Equal(string.Join(" or ", termos.Take(16)), c.Consultas.Last());
        // O 17º termo continua disponível em consulta explícita: não existe corte compartilhado.
        var consulta = string.Join(" or ", termos);
        Assert.Equal(referencia.Id, Assert.Single((await c.BuscarAsync(consulta)).Resultados).Item.Id);
        Assert.Equal(consulta, c.Consultas.Last());
    }

    [PostgreSqlFact]
    public async Task PipeMcpPreservaTextoELimite2000ERecusaParametroModo()
    {
        await using var c = await Cenario.CriarAsync(); var referencia = await c.AdicionarAsync("Brain Postgres");
        using var ferramentas = BrainMcpTests.Provedor(c.App);
        var servidores = await ferramentas.ForSessionAsync("pipe-254", new(42, AgentKind.Codex, JobExecutionContext.General(AppContext.BaseDirectory),
            BrainConversation: c.Mensagem.ParaFerramentas()));
        await using var cliente = await BrainMcpTests.Cliente.ConectarAsync(Assert.Single(servidores));
        var longo = "Brain" + new string(' ', 1995); // 2000 caracteres, sem corte nem trim pelo adapter.
        var resposta = await cliente.ChamarAsync("brain_buscar_conhecimento", new { texto = longo });
        Assert.False(resposta["isError"]!.GetValue<bool>()); Assert.Equal(longo, c.Consultas.Last());
        var pagina = JsonNode.Parse(resposta["content"]![0]!["text"]!.GetValue<string>())!;
        Assert.Equal(referencia.Id.ToString(), pagina["Resultados"]![0]!["Item"]!["Id"]!.GetValue<string>());
        foreach (var args in new object[] { new { texto = longo + "x" }, new { texto = "Brain", modo = "qualquer" } })
        {
            var erro = await cliente.ChamarAsync("brain_buscar_conhecimento", args);
            Assert.True(erro["isError"]!.GetValue<bool>());
            Assert.Equal("entrada_invalida", JsonNode.Parse(erro["content"]![0]!["text"]!.GetValue<string>())!["erro"]!.GetValue<string>());
        }
    }

    // Observa a entrada efetiva, delegando ao índice real. Nada de reproduzir o normalizador nos testes.
    private sealed class IndiceObservado(IndiceDeBuscaPostgreSql indice, ConcurrentQueue<string> consultas) : IIndiceDeBusca
    {
        public Task<bool> VetoresDisponiveisAsync(CancellationToken ct = default) => indice.VetoresDisponiveisAsync(ct);
        public Task<IReadOnlyList<MatchDaBusca>> BuscarAsync(AcessoAoBrain acesso, BuscaDoBrainSearchDto filtro, ModeloEmbedding? modelo, float[]? vetor, double similaridadeMinima = 1, CancellationToken ct = default)
        { consultas.Enqueue(filtro.Texto); return indice.BuscarAsync(acesso, filtro, modelo, vetor, similaridadeMinima, ct); }
        public Task<IReadOnlyList<Guid>> ListarPendentesAsync(AcessoAoBrain acesso, ModeloEmbedding modelo, int limite, CancellationToken ct = default) => indice.ListarPendentesAsync(acesso, modelo, limite, ct);
        public Task<bool> GravarAsync(Guid id, int revisao, ModeloEmbedding modelo, float[] vetor, CancellationToken ct = default) => indice.GravarAsync(id, revisao, modelo, vetor, ct);
        public Task LimparModeloAsync(AcessoAoBrain acesso, ModeloEmbedding modelo, CancellationToken ct = default) => indice.LimparModeloAsync(acesso, modelo, ct);
    }

    private sealed class Cenario(Banco banco, ServiceProvider app, TelegramBrain brain, TelegramBrain.EscopoBrainDaSessao escopo,
        ConcurrentQueue<string> consultas) : IAsyncDisposable
    {
        public ServiceProvider App => app;
        public TelegramBrain Brain => brain;
        public TelegramMessage Mensagem { get; } = BrainMcpTests.Mensagem("busca #254");
        public ConcurrentQueue<string> Consultas => consultas;
        public static async Task<Cenario> CriarAsync()
        {
            var banco = await Banco.CriarAsync(); var consultas = new ConcurrentQueue<string>();
            var app = BrainMcpTests.CriarApp(banco.ConnectionString, s => s.AddScoped<IIndiceDeBusca>(p => new IndiceObservado(p.GetRequiredService<IndiceDeBuscaPostgreSql>(), consultas)));
            var brain = app.GetRequiredService<TelegramBrain>(); await BrainMcpTests.SelecionarAsync(brain);
            var mensagem = BrainMcpTests.Mensagem("busca #254");
            app.GetRequiredService<RegistroDeOperacoesBrain>().RegistrarSessao("S254", mensagem);
            return new(banco, app, brain, (await brain.ResolverEscopoMcpAsync(mensagem))!, consultas);
        }
        public async Task<Conhecimento> AdicionarAsync(string texto, string[]? tags = null, Sensibilidade classe = Sensibilidade.Trabalho)
        {
            var acesso = escopo.Acesso;
            var k = new Conhecimento(acesso.IdEspacoDeConhecimento, acesso.IdProjeto, TipoDeConhecimento.Fato, texto, null, StatusDoConhecimento.Inferido,
                null, classe, null, null, tags ?? [], new(acesso.IdUsuario, "teste-254", "fonte:254", trechoDaFonte: texto), DateTimeOffset.UtcNow, NaturezaDoConteudo.DitoPeloUsuario);
            await using var db = banco.Contexto(); db.Add(k); await db.SaveChangesAsync(); return k;
        }
        public async Task<BuscaDoBrainDto> BuscarAsync(string texto) => (BuscaDoBrainDto)(await app.GetRequiredService<OperacoesMcpDoBrain>()
            .ExecutarAsync(escopo, Mensagem, "S254", "codex", "brain_buscar_conhecimento", JsonSerializer.SerializeToElement(new { texto })))!;
        public async ValueTask DisposeAsync() { await app.DisposeAsync(); await banco.DisposeAsync(); }
    }
}
