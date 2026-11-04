using Dante.Domain.CapturaDeConhecimento;
using Dante.Application;
using Dante.Application.BuscaDoBrain;
using Dante.Application.Conhecimentos;
using Dante.Application.SegurancaDoBrain;
using Dante.Domain.Conhecimentos;
using Dante.Domain.EspacosDeConhecimento;
using Dante.Infrastructure;
using Dante.Infrastructure.BuscaDoBrain;
using Dante.Infrastructure.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
namespace Dante.Tests;

public sealed class BuscaDoBrainTests
{
    [Theory]
    [InlineData(0f,0f)]
    [InlineData(float.NaN,1f)]
    public void VetorInvalidoNaoGeraIndice(float x, float y) => Assert.Throws<ArgumentException>(() => BuscaDoBrainAppService.Normalizar([x,y],2));
    [PostgreSqlFact]
    public async Task BuscaLexicalFuncionaSemExtensaoComFiltrosPaginacaoEMetadadosProtegidos()
    {
        await using var banco = await Banco.CriarAsync(); var e = new EspacoDeConhecimento(Guid.NewGuid(), "Trabalho");
        var outro = new EspacoDeConhecimento(Guid.NewGuid(), "Outro");
        var a = Novo(e.Id,"Guid em agrupamentos",tags:["csharp"]); var b = Novo(e.Id,"Guid para entidades",tags:["csharp"]);
        var secreto = Novo(e.Id,"Guid segredo",Sensibilidade.Secreto); var conf = Novo(e.Id,"Guid confidencial",Sensibilidade.Confidencial);
        var invalido = Novo(e.Id,"Guid obsoleto"); invalido.Invalidar(1,invalido.Proveniencia,DateTimeOffset.UtcNow);
        var expirado = Novo(e.Id,"Guid expirado",ate:DateTimeOffset.UtcNow.AddHours(-1));
        await using(var c = banco.Contexto()) { c.AddRange(e,outro,a,b,secreto,conf,invalido,expirado,Novo(outro.Id,"Guid externo")); await c.SaveChangesAsync(); }
        using var provider = Provider(banco.ConnectionString,new Gerador()); using var scope = EscoposBrainDeTeste.Criar(provider, e.IdUsuario, e.Id);
        var busca = scope.ServiceProvider.GetRequiredService<BuscaDoBrainAppService>(); var acesso = new AcessoAoBrain(e.IdUsuario,e.Id,null);
        var resultado = await busca.BuscarAsync(acesso,new() { Texto="Guid",Tags=["CSHARP"],Limite=1 });
        Assert.Equal("lexical",resultado.Modo); Assert.True(resultado.TemMais); Assert.Single(resultado.Resultados);
        Assert.Equal(OrigemDoResultado.Conhecimento,resultado.Resultados[0].Origem); Assert.True(resultado.Resultados[0].ScoreLexical>0);
        var pagina2=await busca.BuscarAsync(acesso,new() { Texto="Guid",Tags=["csharp"],Limite=1,Deslocamento=1 });
        Assert.NotEqual(resultado.Resultados[0].Item.Id,pagina2.Resultados[0].Item.Id);
        Assert.Equal(2,(await busca.BuscarAsync(acesso,new() { Texto="Guid" })).Resultados.Count);
        Assert.Equal(3,(await busca.BuscarAsync(acesso with { PermitirConfidencial=true },new() { Texto="Guid" })).Resultados.Count);
        var metadata = Assert.Single((await busca.BuscarAsync(acesso,new() { IdConhecimento=secreto.Id })).Resultados);
        Assert.True(metadata.Item.ConteudoProtegido); Assert.Null(metadata.Item.Conteudo); Assert.Null(metadata.Item.ReferenciaDaFonte);
        Assert.Empty((await busca.BuscarAsync(acesso,new() { Texto="Guid",Tipo=TipoDeConhecimento.Decisao })).Resultados);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>busca.BuscarAsync(acesso with { IdUsuario=outro.IdUsuario },new() { Texto="Guid" }));
        await Assert.ThrowsAsync<ArgumentException>(()=>busca.BuscarAsync(acesso,new() { Texto="x",Limite=101 }));
    }
    [PostgreSqlFact]
    public async Task ConhecimentoPromovidoPersisteInstantesDistintosEFiltraPelaDataDaCaptura()
    {
        await using var banco = await Banco.CriarAsync(); var e = new EspacoDeConhecimento(Guid.NewGuid(), "Trabalho");
        var agora = DateTimeOffset.UtcNow; var capturadoEm = agora.AddDays(-2);
        var candidato = new Dante.Domain.CapturaDeConhecimento.CandidatoDeConhecimento(e.Id, null, TipoDeConhecimento.Fato, "Brain integrado ao PostgreSQL",
            Sensibilidade.Trabalho, Dante.Domain.CapturaDeConhecimento.NaturezaDoConteudo.DitoPeloUsuario, Dante.Domain.CapturaDeConhecimento.ModoDeCaptura.Explicita,
            "pedido do usuário", BrainEfTests.Origem(), capturadoEm);
        var promovido = candidato.Promover(1, BrainEfTests.Origem(), agora);
        await using(var c = banco.Contexto()) { c.AddRange(e, candidato, promovido); await c.SaveChangesAsync(); }
        await using(var c = banco.Contexto())
        {
            var lido = await c.Conhecimentos.SingleAsync(x => x.Id == promovido.Id);
            Assert.Equal(2, lido.Historico.Count); Assert.True(lido.Historico[0].RegistradaEm < lido.Historico[1].RegistradaEm);
            Assert.True(Math.Abs((lido.Historico[0].RegistradaEm - capturadoEm).TotalMilliseconds) < 1);
            Assert.True(Math.Abs((lido.CriadoEm - capturadoEm).TotalMilliseconds) < 1); Assert.True(Math.Abs((lido.AtualizadoEm - agora).TotalMilliseconds) < 1);
        }
        using var provider = Provider(banco.ConnectionString,new Gerador()); using var scope = EscoposBrainDeTeste.Criar(provider, e.IdUsuario, e.Id);
        var busca = scope.ServiceProvider.GetRequiredService<BuscaDoBrainAppService>(); var acesso = new AcessoAoBrain(e.IdUsuario,e.Id,null);
        // CriadoEm é a data da captura: filtros por criação não confundem com a data da confirmação.
        Assert.Single((await busca.BuscarAsync(acesso,new() { Texto="PostgreSQL",CriadoAte=agora.AddDays(-1) })).Resultados);
        Assert.Empty((await busca.BuscarAsync(acesso,new() { Texto="PostgreSQL",CriadoDesde=agora.AddHours(-1) })).Resultados);
    }
    [PostgreSqlFact]
    public async Task SemanticaReindexacaoMudancaDeModeloEFalhaPreservamCanonico()
    {
        await using var banco = await Banco.CriarAsync(); var e = new EspacoDeConhecimento(Guid.NewGuid(),"Trabalho");
        var a = Novo(e.Id,"relação opcional não existe"); var b=Novo(e.Id,"imagem e fotografia"); var segredo=Novo(e.Id,"relação privada",Sensibilidade.Secreto);
        await using(var c=banco.Contexto()) { c.AddRange(e,a,b,segredo); await c.SaveChangesAsync(); }
        var gerador=new Gerador(); using var provider=Provider(banco.ConnectionString,gerador); var acesso=new AcessoAoBrain(e.IdUsuario,e.Id,null);
        using(var scope=EscoposBrainDeTeste.Criar(provider, e.IdUsuario, e.Id))
        {
            var indice=scope.ServiceProvider.GetRequiredService<IndiceDeBuscaPostgreSql>(); await indice.PrepararVetoresAsync();
            var busca=scope.ServiceProvider.GetRequiredService<BuscaDoBrainAppService>();
            Assert.Equal(2,await busca.ReindexarAsync(acesso)); Assert.Equal(0,await busca.ReindexarAsync(acesso));
            Assert.DoesNotContain(gerador.Textos,x=>x.Contains("privada"));
            var semantico=await busca.BuscarAsync(acesso,new() { Texto="associação ausente" });
            var item=Assert.Single(semantico.Resultados); Assert.Equal(a.Id,item.Item.Id); Assert.Equal(0,item.ScoreLexical); Assert.True(item.ScoreSemantico>0.99);
            Assert.Equal("hibrido",semantico.Modo);
            Assert.Single((await busca.BuscarAsync(acesso,new() { Texto="associação ausente", NaturezaDeOrigem=NaturezaDoConteudo.DitoPeloUsuario })).Resultados);
            Assert.Empty((await busca.BuscarAsync(acesso,new() { Texto="associação ausente", NaturezaDeOrigem=NaturezaDoConteudo.ConclusaoDoAgente })).Resultados);
            var misto=Assert.Single((await busca.BuscarAsync(acesso,new() { Texto="relação opcional" })).Resultados);
            Assert.True(misto.ScoreLexical>0); Assert.True(misto.ScoreSemantico>0);
            gerador.Versao="2"; Assert.Empty((await busca.BuscarAsync(acesso,new() { Texto="associação ausente" })).Resultados);
            Assert.Equal(2,await busca.ReindexarAsync(acesso)); Assert.Equal(2,await busca.ReindexarAsync(acesso,reconstruir:true));
            gerador.Indisponivel=true; Assert.Equal("lexical",(await busca.BuscarAsync(acesso,new() { Texto="relação" })).Modo);
        }
        await using(var c=banco.Contexto())
        {
            var salvo=(await c.Conhecimentos.SingleAsync(x=>x.Id==a.Id)); Assert.Equal(1,salvo.Revisao);
            salvo.Corrigir(1,salvo.Tipo,"fotografia",null,null,salvo.Sensibilidade,null,null,[],salvo.Proveniencia,DateTimeOffset.UtcNow, NaturezaDoConteudo.DitoPeloUsuario); await c.SaveChangesAsync();
        }
        gerador.Indisponivel=false;
        using(var scope=EscoposBrainDeTeste.Criar(provider, e.IdUsuario, e.Id))
        {
            var busca=scope.ServiceProvider.GetRequiredService<BuscaDoBrainAppService>();
            Assert.Empty((await busca.BuscarAsync(acesso,new() { Texto="associação ausente" })).Resultados);
            Assert.Equal(1,await busca.ReindexarAsync(acesso));
        }
    }
    [PostgreSqlFact]
    public async Task SecretNaoPermaneceNosIndicesAposCriacaoReclassificacaoERebuild()
    {
        await using var banco = await Banco.CriarAsync();
        var espaco = new EspacoDeConhecimento(Guid.NewGuid(), "Reservado");
        var secreto = Novo(espaco.Id, "sentinelaconfidencial", Sensibilidade.Secreto,
            tags: ["etiquetareservada"]);
        var publico = Novo(espaco.Id, "conteudopermitido", Sensibilidade.Publico);
        await using var c = banco.Contexto();
        c.AddRange(espaco, secreto, publico); await c.SaveChangesAsync();
        Assert.Equal(new[] { publico.Id }, await IdsLexicaisAsync(c));
        var indice = new IndiceDeBuscaPostgreSql(c);
        await indice.PrepararVetoresAsync();
        var modelo = new ModeloEmbedding("teste", "controlado", "1", 3);
        Assert.True(await indice.GravarAsync(publico.Id, publico.Revisao, modelo, [1, 0, 0]));
        publico.Corrigir(1, publico.Tipo, publico.Conteudo, null, null, Sensibilidade.Secreto,
            null, null, [], publico.Proveniencia, DateTimeOffset.UtcNow, NaturezaDoConteudo.DitoPeloUsuario);
        await c.SaveChangesAsync();
        Assert.Empty(await IdsLexicaisAsync(c));
        Assert.Empty(await c.Database.SqlQuery<Guid>($"SELECT id_conhecimento AS \"Value\" FROM brain_index.representacoes").ToListAsync());
        Assert.False(await indice.GravarAsync(publico.Id, 1, modelo, [1, 0, 0]));
        Assert.False(await indice.GravarAsync(publico.Id, publico.Revisao, modelo, [1, 0, 0]));
        await indice.ReconstruirLexicalAsync();
        Assert.Empty(await IdsLexicaisAsync(c));
        Assert.Equal("sentinelaconfidencial", secreto.Conteudo);
        Assert.Equal("conteudopermitido", publico.Conteudo);
        // Ao reclassificar de volta para permitido, apenas a revisão atual volta ao índice.
        publico.Corrigir(2, publico.Tipo, "revisaopermitida", null, null, Sensibilidade.Publico,
            null, null, [], publico.Proveniencia, DateTimeOffset.UtcNow, NaturezaDoConteudo.DitoPeloUsuario);
        await c.SaveChangesAsync();
        Assert.Equal(new[] { publico.Id }, await IdsLexicaisAsync(c));
        await indice.ReconstruirLexicalAsync();
        Assert.Equal(new[] { publico.Id }, await IdsLexicaisAsync(c));
        Assert.False(await c.Database.SqlQuery<bool>($"SELECT EXISTS(SELECT 1 FROM brain_index.trabalhos WHERE documento @@ to_tsquery('portuguese', 'sentinelaconfidencial | etiquetareservada | conteudopermitido')) AS \"Value\"").SingleAsync());
    }
    [PostgreSqlFact]
    public async Task MigrationCorrigeIndiceExistenteSemAlterarConhecimentoCanonico()
    {
        await using var banco = await Banco.CriarAsync(migrar: false);
        await using var c = banco.Contexto();
        var migrator = c.GetService<IMigrator>();
        await migrator.MigrateAsync("20261005211631_IndicesDerivadosDeBusca");
        var espaco = new EspacoDeConhecimento(Guid.NewGuid(), "Legado");
        var secreto = Novo(espaco.Id, "reservadolegadoteste", Sensibilidade.Secreto);
        var publico = Novo(espaco.Id, "permitido", Sensibilidade.Publico);
        // Esquema anterior ao tenant: seed compatível com a versão física, sem usar o modelo atual do espaço.
        await c.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO brain_data.espacos_de_conhecimento(id,id_usuario,nome,estado) VALUES({espaco.Id},{espaco.IdUsuario},{espaco.Nome},0)");
        await ConhecimentoLegadoDeTeste.InserirAsync(c, secreto); await ConhecimentoLegadoDeTeste.InserirAsync(c, publico);
        Assert.Contains(secreto.Id, await IdsLexicaisAsync(c));
        await c.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO brain_index.representacoes(id_conhecimento,revisao,modelo,provedor,nome,versao,dimensao,gerado_em) VALUES({secreto.Id},1,'legado','teste','teste','1',3,now())");
        await migrator.MigrateAsync();
        Assert.Equal(new[] { publico.Id }, await IdsLexicaisAsync(c));
        Assert.Empty(await c.Database.SqlQuery<Guid>($"SELECT id_conhecimento AS \"Value\" FROM brain_index.representacoes").ToListAsync());
        c.ChangeTracker.Clear();
        var canonico = await c.Conhecimentos.SingleAsync(x => x.Id == secreto.Id);
        Assert.Equal("reservadolegadoteste", canonico.Conteudo); Assert.Equal(1, canonico.Revisao);
        var indice = new IndiceDeBuscaPostgreSql(c);
        // Simula resíduo derivado do banco antigo; rebuild também faz a limpeza defensiva.
        await c.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO brain_index.trabalhos(id_conhecimento,revisao,documento) VALUES({secreto.Id},1,to_tsvector('portuguese','reservadolegadoteste'))");
        await indice.ReconstruirLexicalAsync();
        Assert.Equal(new[] { publico.Id }, await IdsLexicaisAsync(c));
    }
    [PostgreSqlFact]
    public async Task IndexacaoConcorrenteNaoRecriaEntradaDepoisDeVirarSecret()
    {
        await using var banco = await Banco.CriarAsync();
        var espaco = new EspacoDeConhecimento(Guid.NewGuid(), "Concorrência");
        var item = Novo(espaco.Id, "conteudoantesdaclassificacao", Sensibilidade.Publico);
        await using (var c = banco.Contexto()) { c.AddRange(espaco, item); await c.SaveChangesAsync(); await new IndiceDeBuscaPostgreSql(c).PrepararVetoresAsync(); }
        var modelo = new ModeloEmbedding("teste", "controlado", "1", 3);
        var nome = "brain_indice_" + Guid.NewGuid().ToString("N");
        var connection = new NpgsqlConnectionStringBuilder(banco.ConnectionString) { ApplicationName = nome }.ConnectionString;
        // Testa escritor de embedding e rebuild contra a mesma troca de classificação.
        foreach (var rebuild in new[] { false, true })
        {
            await using var classificacao = banco.Contexto();
            var atual = await classificacao.Conhecimentos.SingleAsync(x => x.Id == item.Id);
            if (atual.Sensibilidade == Sensibilidade.Secreto)
            {
                atual.Corrigir(atual.Revisao, atual.Tipo, atual.Conteudo, null, null, Sensibilidade.Publico,
                    null, null, [], atual.Proveniencia, DateTimeOffset.UtcNow, NaturezaDoConteudo.DitoPeloUsuario);
                await classificacao.SaveChangesAsync();
            }
            var revisaoAnterior = atual.Revisao;
            await using var transacao = await classificacao.Database.BeginTransactionAsync();
            atual.Corrigir(revisaoAnterior, atual.Tipo, atual.Conteudo, null, null, Sensibilidade.Secreto,
                null, null, [], atual.Proveniencia, DateTimeOffset.UtcNow, NaturezaDoConteudo.DitoPeloUsuario);
            await classificacao.SaveChangesAsync();
            await using var escrita = Banco.Contexto(connection);
            var indice = new IndiceDeBuscaPostgreSql(escrita);
            using var prazo = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            Task operacao = rebuild ? indice.ReconstruirLexicalAsync(prazo.Token) : indice.GravarAsync(item.Id, revisaoAnterior, modelo, [1, 0, 0], prazo.Token);
            try
            {
                await AguardarBloqueioAsync(connection, nome, prazo.Token);
                await transacao.CommitAsync(prazo.Token);
                await operacao;
                if (!rebuild) Assert.False(await (Task<bool>)operacao);
            }
            finally
            {
                prazo.Cancel();
                if (!operacao.IsCompleted)
                {
                    try { await operacao; } catch (OperationCanceledException) { }
                }
            }
            await using var verificar = banco.Contexto();
            Assert.Empty(await IdsLexicaisAsync(verificar));
            Assert.Empty(await verificar.Database.SqlQuery<Guid>($"SELECT id_conhecimento AS \"Value\" FROM brain_index.representacoes").ToListAsync());
        }
    }
    private static async Task AguardarBloqueioAsync(string connection, string nome, CancellationToken ct)
    {
        await using var observador = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connection) { ApplicationName = "brain_observador" }.ConnectionString);
        await observador.OpenAsync(ct);
        await using var comando = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE application_name = @nome AND wait_event_type = 'Lock')", observador);
        comando.Parameters.AddWithValue("nome", nome);
        while (!(bool)(await comando.ExecuteScalarAsync(ct))!) await Task.Delay(20, ct);
    }
    private static Task<List<Guid>> IdsLexicaisAsync(DanteDbContext c) =>
        c.Database.SqlQuery<Guid>($"SELECT id_conhecimento AS \"Value\" FROM brain_index.trabalhos ORDER BY id_conhecimento").ToListAsync();
    [Fact]
    public async Task AdapterHttpTrataFalhaEValidaModeloSemEnviarSemConfiguracao()
    {
        var handler=new Handler(); using var http=new HttpClient(handler);
        using var gerador=new GeradorDeEmbeddingHttp(http,new ConfigurationBuilder().Build());
        Assert.Null(await gerador.GerarAsync("texto")); Assert.Equal(0,handler.Chamadas);
        var cfg=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
            ["DANTE_BRAIN_EMBEDDING_ENDPOINT"]="http://localhost/embeddings",["DANTE_BRAIN_EMBEDDING_MODEL"]="test",
            ["DANTE_BRAIN_EMBEDDING_VERSION"]="1",["DANTE_BRAIN_EMBEDDING_DIMENSION"]="2" }).Build();
        using var ativo=new GeradorDeEmbeddingHttp(new HttpClient(handler),cfg);
        Assert.Null(await ativo.GerarAsync("texto")); handler.Json="{\"data\":[{\"embedding\":[3,4]}]}";
        var v=await ativo.GerarAsync("texto"); Assert.Equal(0.6,v![0],5); Assert.Equal(0.8,v[1],5);
    }
    // #258: regressões do fallback lexical e da garantia de não enviar conteúdo para fora do host.
    [Fact]
    public void NormalizacaoProduzVetorUnitarioERecusaDimensaoDivergente()
    {
        var v=BuscaDoBrainAppService.Normalizar([3,0,4],3);
        Assert.Equal(1.0,Math.Sqrt(v.Sum(x=>(double)x*x)),5); Assert.Equal(0.6,v[0],5); Assert.Equal(0.8,v[2],5);
        Assert.Throws<ArgumentException>(()=>BuscaDoBrainAppService.Normalizar([1,2],3));
        Assert.Throws<ArgumentException>(()=>BuscaDoBrainAppService.Normalizar([1,2,3,4],3));
        Assert.Throws<ArgumentException>(()=>BuscaDoBrainAppService.Normalizar([float.PositiveInfinity,0,0],3));
    }
    [Theory]
    [InlineData("http://127.0.0.1:11434/v1/embeddings",false)]
    [InlineData("http://localhost:11434/v1/embeddings",false)]
    [InlineData("http://[::1]:11434/v1/embeddings",false)]
    [InlineData("http://192.168.0.10:11434/v1/embeddings",true)]
    [InlineData("http://homelab:11434/v1/embeddings",true)]
    [InlineData("https://api.example.com/v1/embeddings",true)]
    public async Task GeradorSoEnviaParaLoopbackSemAutorizacaoExterna(string endpoint,bool externo)
    {
        var handler=new Handler{ Json="{\"data\":[{\"embedding\":[1,0,0]}]}" };
        using var gerador=new GeradorDeEmbeddingHttp(new HttpClient(handler),Configuracao(endpoint,3));
        Assert.Equal(externo,gerador.Externo); Assert.NotNull(gerador.Modelo);
        var v=await gerador.GerarAsync("conteúdo de trabalho");
        // Sem HTTPS + DANTE_BRAIN_EMBEDDING_ALLOW_EXTERNAL=true, nada sai do host: nenhuma requisição é feita.
        Assert.Equal(externo?0:1,handler.Chamadas); Assert.Equal(externo,v is null);
    }
    [Fact]
    public async Task GeradorDescartaRespostaComDimensaoDivergente()
    {
        var handler=new Handler{ Json="{\"data\":[{\"embedding\":[1,0]}]}" };
        using var gerador=new GeradorDeEmbeddingHttp(new HttpClient(handler),Configuracao("http://127.0.0.1:11434/v1/embeddings",3));
        Assert.Null(await gerador.GerarAsync("texto")); Assert.Equal(1,handler.Chamadas);
    }
    [PostgreSqlFact]
    public async Task SemModeloSemColunaVetorOuComDimensaoDivergenteBuscaContinuaLexical()
    {
        await using var banco=await Banco.CriarAsync(); var e=new EspacoDeConhecimento(Guid.NewGuid(),"Fallback");
        var a=Novo(e.Id,"incidente de rede no homelab");
        await using(var c=banco.Contexto()) { c.AddRange(e,a); await c.SaveChangesAsync(); }
        var acesso=new AcessoAoBrain(e.IdUsuario,e.Id,null);
        // 1. Modelo não configurado: o gerador nem é chamado.
        var semModelo=new Gerador{ SemModelo=true };
        using(var provider=Provider(banco.ConnectionString,semModelo)) using(var scope=EscoposBrainDeTeste.Criar(provider,e.IdUsuario,e.Id))
        {
            var busca=scope.ServiceProvider.GetRequiredService<BuscaDoBrainAppService>();
            var r=await busca.BuscarAsync(acesso,new() { Texto="rede" });
            Assert.Equal("lexical",r.Modo); Assert.Null(r.ModeloEmbedding); Assert.Equal(a.Id,Assert.Single(r.Resultados).Item.Id);
            Assert.Equal(0,await busca.ReindexarAsync(acesso)); Assert.Empty(semModelo.Textos);
        }
        // 2. Modelo configurado, mas a coluna vetor ainda não existe (search-enable-vector não executado).
        var comModelo=new Gerador();
        using(var provider=Provider(banco.ConnectionString,comModelo)) using(var scope=EscoposBrainDeTeste.Criar(provider,e.IdUsuario,e.Id))
        {
            var busca=scope.ServiceProvider.GetRequiredService<BuscaDoBrainAppService>();
            Assert.False(await scope.ServiceProvider.GetRequiredService<IndiceDeBuscaPostgreSql>().VetoresDisponiveisAsync());
            var r=await busca.BuscarAsync(acesso,new() { Texto="rede" });
            Assert.Equal("lexical",r.Modo); Assert.Single(r.Resultados);
            Assert.Equal(0,await busca.ReindexarAsync(acesso)); Assert.Empty(comModelo.Textos);
        }
        // 3. Coluna existe, mas o provedor devolve dimensão diferente da configurada: nada é gravado e a busca degrada.
        var handler=new Handler{ Json="{\"data\":[{\"embedding\":[1,0]}]}" };
        using(var divergente=new GeradorDeEmbeddingHttp(new HttpClient(handler),Configuracao("http://127.0.0.1:11434/v1/embeddings",3)))
        using(var provider=Provider(banco.ConnectionString,divergente)) using(var scope=EscoposBrainDeTeste.Criar(provider,e.IdUsuario,e.Id))
        {
            await scope.ServiceProvider.GetRequiredService<IndiceDeBuscaPostgreSql>().PrepararVetoresAsync();
            var busca=scope.ServiceProvider.GetRequiredService<BuscaDoBrainAppService>();
            Assert.Equal(0,await busca.ReindexarAsync(acesso)); Assert.True(handler.Chamadas>0);
            var r=await busca.BuscarAsync(acesso,new() { Texto="rede" });
            Assert.Equal("lexical",r.Modo); Assert.Equal(a.Id,Assert.Single(r.Resultados).Item.Id);
        }
        await using(var c=banco.Contexto())
            Assert.Empty(await c.Database.SqlQuery<Guid>($"SELECT id_conhecimento AS \"Value\" FROM brain_index.representacoes").ToListAsync());
    }
    [PostgreSqlFact]
    public async Task ReindexacaoRespeitaSensibilidadeEProtecaoDeSegredos()
    {
        await using var banco=await Banco.CriarAsync(); var e=new EspacoDeConhecimento(Guid.NewGuid(),"Reindexação");
        var trabalho=Novo(e.Id,"relação de trabalho"); var pessoal=Novo(e.Id,"relação pessoal",Sensibilidade.Pessoal);
        var confidencial=Novo(e.Id,"relação confidencial",Sensibilidade.Confidencial); var secreto=Novo(e.Id,"relação secreta",Sensibilidade.Secreto);
        // Semeado direto no banco, contornando a captura: a reindexação ainda não pode enviar a credencial ao gerador.
        var credencial=Novo(e.Id,"relação com senha=abcd1234efgh");
        await using(var c=banco.Contexto()) { c.AddRange(e,trabalho,pessoal,confidencial,secreto,credencial); await c.SaveChangesAsync(); }
        foreach(var externo in new[]{true,false})
        {
            var gerador=new Gerador{ EhExterno=externo, Versao=externo?"externo":"local" };
            using var provider=Provider(banco.ConnectionString,gerador); using var scope=EscoposBrainDeTeste.Criar(provider,e.IdUsuario,e.Id);
            await scope.ServiceProvider.GetRequiredService<IndiceDeBuscaPostgreSql>().PrepararVetoresAsync();
            // Mesmo com Confidencial autorizado no acesso, gerador externo não recebe Confidencial.
            var gravados=await scope.ServiceProvider.GetRequiredService<BuscaDoBrainAppService>().ReindexarAsync(new AcessoAoBrain(e.IdUsuario,e.Id,null,true));
            Assert.Equal(externo?2:3,gravados);
            Assert.DoesNotContain(gerador.Textos,x=>x.Contains("secreta")||x.Contains("senha"));
            Assert.Equal(!externo,gerador.Textos.Any(x=>x.Contains("confidencial")));
            Assert.Contains(gerador.Textos,x=>x.Contains("pessoal"));
        }
    }
    private static IConfiguration Configuracao(string endpoint,int dimensao)=>new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
        ["DANTE_BRAIN_EMBEDDING_ENDPOINT"]=endpoint,["DANTE_BRAIN_EMBEDDING_MODEL"]="bge-m3",
        ["DANTE_BRAIN_EMBEDDING_VERSION"]="1",["DANTE_BRAIN_EMBEDDING_DIMENSION"]=dimensao.ToString(System.Globalization.CultureInfo.InvariantCulture) }).Build();
    private sealed class Handler : HttpMessageHandler
    {
        public int Chamadas; public string Json="{}";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        { Chamadas++; return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content=new StringContent(Json) }); }
    }
    private sealed class Gerador : IGeradorDeEmbedding
    {
        public string Versao="1"; public bool Indisponivel, SemModelo, EhExterno; public List<string> Textos=[]; public double? SimilaridadeMinima { get; set; } = 0.5;
        public ModeloEmbedding? Modelo=>SemModelo?null:new("teste","conceitos-controlados",Versao,3); public bool Externo=>EhExterno;
        public Task<float[]?> GerarAsync(string texto,CancellationToken ct=default)
        { Textos.Add(texto); return Task.FromResult<float[]?>(Indisponivel?null:texto.Contains("relação")||texto.Contains("associação")?[1,0,0]:[0,1,0]); }
    }
    private static Conhecimento Novo(Guid e,string texto,Sensibilidade s=Sensibilidade.Trabalho,IReadOnlyList<string>? tags=null,DateTimeOffset? ate=null)=>new(e,null,
        TipoDeConhecimento.Fato,texto,null,StatusDoConhecimento.Inferido,null,s,null,ate,tags??[],new(Guid.NewGuid(),"teste","fonte",null,"trecho"),DateTimeOffset.UtcNow, NaturezaDoConteudo.DitoPeloUsuario);
    private static ServiceProvider Provider(string connection,IGeradorDeEmbedding gerador)
    {
        var services=new ServiceCollection().AddApplication().AddInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["ConnectionStrings:Dante"]=connection }).Build());
        services.AddSingleton(gerador); return services.BuildServiceProvider();
    }
}
