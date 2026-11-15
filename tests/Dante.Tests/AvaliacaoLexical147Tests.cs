using System.Diagnostics;
using System.Text.Json;
using Dante.Application;
using Dante.Application.BuscaDoBrain;
using Dante.Application.SegurancaDoBrain;
using Dante.Domain.CapturaDeConhecimento;
using Dante.Domain.Conhecimentos;
using Dante.Domain.EspacosDeConhecimento;
using Dante.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;
using Dante.Worker.Telegram;
using Npgsql;

namespace Dante.Tests;

// Corpus/rótulos congelados antes da implementação. Qualidade medida não muda o aceite global da #147.
public sealed class AvaliacaoLexical147Tests(ITestOutputHelper output)
{
    [PostgreSqlTheory]
    [InlineData("issue147-lexical.json",0.46875,0.59375)]
    [InlineData("issue147-verificacao.json",0,1)]
    public async Task CompararBaselineEExpansaoSemAjustarConsultas(string arquivo,double recallBase,double recallEsperado)
    {
        await using var banco=await Banco.CriarAsync();
        using var json=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,"ConsultasDeReferencia",arquivo)));
        var e=new EspacoDeConhecimento(Guid.NewGuid(),"Avaliação #147");var keys=new Dictionary<Guid,string>();
        await using(var db=banco.Contexto())
        {
            db.Add(e);
            foreach(var c in json.RootElement.GetProperty("corpus").EnumerateArray())
            {
                var k=new Conhecimento(e.Id,null,(TipoDeConhecimento)c.GetProperty("type").GetInt32(),c.GetProperty("text").GetString(),null,
                    StatusDoConhecimento.Inferido,null,Sensibilidade.Trabalho,null,null,c.GetProperty("tags").EnumerateArray().Select(x=>x.GetString()!),
                    new(e.IdUsuario,"fixture-147",arquivo),DateTimeOffset.UtcNow,NaturezaDoConteudo.ConclusaoDoAgente);
                db.Add(k);keys[k.Id]=c.GetProperty("key").GetString()!;
            }
            await db.SaveChangesAsync();
        }
        var cfg=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["ConnectionStrings:Dante"]=banco.ConnectionString}).Build();
        using var app=new ServiceCollection().AddApplication().AddInfrastructure(cfg).BuildServiceProvider();
        var acesso=new AcessoAoBrain(e.IdUsuario,e.Id,null);var rows=new List<Linha>();
        using(var warm=EscoposBrainDeTeste.Criar(app,e.IdUsuario,e.Id))
            await warm.ServiceProvider.GetRequiredService<BuscaDoBrainAppService>().BuscarAsync(acesso,new(){Texto="aquecimento"});
        foreach(var q in json.RootElement.GetProperty("queries").EnumerateArray())
        {
            var texto=q.GetProperty("text").GetString()!;var relevantes=q.GetProperty("relevant").EnumerateArray().Select(x=>x.GetString()!).ToArray();
            foreach(var modo in new[]{"baseline","or","and-normalizado","or-cobertura","expansao"})
            {
                var ms=new List<double>();BuscaDoBrainDto? last=null;
                for(var i=0;i<5;i++)
                {
                    using var scope=EscoposBrainDeTeste.Criar(app,e.IdUsuario,e.Id);
                    var sw=Stopwatch.StartNew();
                    // TextoIntegral não muda a consulta SQL no modo lexical; impede somente o passo novo.
                    var busca=scope.ServiceProvider.GetRequiredService<BuscaDoBrainAppService>();
                    var termos=TelegramBrain.Termos(texto);
                    last=await busca.BuscarAsync(acesso,new(){Texto=modo is "or" or "or-cobertura"?termos:texto,TextoIntegral=modo=="expansao"?null:texto,Limite=10});
                    if(modo=="and-normalizado" && last.Resultados.Count==0)
                        last=await busca.BuscarAsync(acesso,new(){Texto=termos.Replace(" or "," "),TextoIntegral=texto,Limite=10});
                    if(modo=="or-cobertura")
                    {
                        var filtrados=new List<ResultadoDaBuscaDto>();
                        await using var connection=new NpgsqlConnection(banco.ConnectionString);await connection.OpenAsync();
                        foreach(var r in last.Resultados)
                        {
                            await using var cmd=new NpgsqlCommand("SELECT cardinality(ARRAY(SELECT unnest(tsvector_to_array(to_tsvector('portuguese',@texto))) INTERSECT SELECT unnest(tsvector_to_array(to_tsvector('portuguese',@doc)))))::double precision / nullif(cardinality(tsvector_to_array(to_tsvector('portuguese',@texto))),0)",connection);
                            cmd.Parameters.AddWithValue("texto",termos.Replace(" or "," "));cmd.Parameters.AddWithValue("doc",r.Item.Conteudo!);
                            if(await cmd.ExecuteScalarAsync() is double cobertura && cobertura>=2.0/3)filtrados.Add(r);
                        }
                        last=last with{Resultados=filtrados};
                    }
                    ms.Add(sw.Elapsed.TotalMilliseconds);Assert.Equal("lexical",last.Modo);Assert.Null(last.ModeloEmbedding);
                }
                rows.Add(new(q.GetProperty("id").GetString()!,q.GetProperty("category").GetString()!,texto,modo,relevantes,
                    last!.Resultados.Select(x=>keys[x.Item.Id]).ToArray(),ms.ToArray()));
            }
        }
        object Resumo(string modo)
        {
            var r=rows.Where(x=>x.Modo==modo).ToArray();var positive=r.Where(x=>x.Relevantes.Length>0).ToArray();var tempos=r.SelectMany(x=>x.Ms).Order().ToArray();
            var tp=r.Sum(x=>x.Recuperados.Intersect(x.Relevantes).Count());var fp=r.Sum(x=>x.Recuperados.Except(x.Relevantes).Count());
            var recall=positive.Average(x=>x.Recuperados.Take(5).Intersect(x.Relevantes).Count()/(double)x.Relevantes.Length);
            if(modo is "baseline" or "expansao")
            {
                Assert.Equal(modo=="baseline"?recallBase:recallEsperado,recall,6);Assert.Equal(0,fp);
                Assert.All(r.Where(x=>x.Relevantes.Length==0),x=>Assert.Empty(x.Recuperados));
            }
            return new{modo,recall5=recall,precisao=tp+fp==0?(double?)null:tp/(double)(tp+fp),tp,fp,fn=r.Sum(x=>x.Relevantes.Except(x.Recuperados).Count()),
                semCorrespondencia=r.Count(x=>x.Relevantes.Length==0),negativasComResultado=r.Count(x=>x.Relevantes.Length==0&&x.Recuperados.Length>0),
                p50Ms=tempos[(tempos.Length-1)/2],p95Ms=tempos[(int)Math.Ceiling(.95*tempos.Length)-1],amostras=tempos.Length};
        }
        foreach(var r in rows.Where(x=>x.Modo=="expansao"))
        {
            var original=rows.Single(x=>x.Id==r.Id&&x.Modo=="baseline");
            if(original.Recuperados.Length>0)Assert.Equal(original.Recuperados,r.Recuperados);
        }
        var report=new{arquivo,baseline=Resumo("baseline"),expansao=Resumo("expansao"),alternativas=new[]{Resumo("or"),Resumo("and-normalizado"),Resumo("or-cobertura")},rows};
        var serialized=JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true});output.WriteLine(serialized);
        if(Environment.GetEnvironmentVariable("DANTE_147_LEXICAL_EVIDENCIAS") is {Length:>0} dir)
        {Directory.CreateDirectory(dir);await File.WriteAllTextAsync(Path.Combine(dir,Path.GetFileNameWithoutExtension(arquivo)+"-resultado.json"),serialized);}
    }
    private sealed record Linha(string Id,string Categoria,string Consulta,string Modo,string[] Relevantes,string[] Recuperados,double[] Ms);
}
