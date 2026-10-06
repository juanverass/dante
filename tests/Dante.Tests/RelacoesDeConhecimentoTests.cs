using Dante.Application;
using Dante.Application.Conhecimentos;
using Dante.Application.RelacoesDeConhecimento;
using Dante.Domain.Conhecimentos;
using Dante.Domain.EspacosDeConhecimento;
using Dante.Domain.RelacoesDeConhecimento;
using Dante.Infrastructure;
using Dante.Infrastructure.Persistencia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
namespace Dante.Tests;

public sealed class RelacoesDeConhecimentoTests
{
    [Theory]
    [InlineData(TipoDeRelacao.RelacionadoA)]
    [InlineData(TipoDeRelacao.PertenceA)]
    [InlineData(TipoDeRelacao.DerivadoDe)]
    [InlineData(TipoDeRelacao.DependeDe)]
    [InlineData(TipoDeRelacao.Referencia)]
    [InlineData(TipoDeRelacao.Contradiz)]
    public void RelacoesPreservamOrigemESemantica(TipoDeRelacao tipo)
    {
        var espaco = Guid.NewGuid(); var a = BrainEfTests.Novo(espaco); var b = BrainEfTests.Novo(espaco);
        var origem = BrainEfTests.Origem();
        var r = new RelacaoDeConhecimento(a, b, tipo, origem, DateTimeOffset.UtcNow);
        Assert.Same(origem, r.Proveniencia);
        var inversa = new RelacaoDeConhecimento(b, a, tipo, origem, DateTimeOffset.UtcNow);
        if (tipo is TipoDeRelacao.RelacionadoA or TipoDeRelacao.Contradiz)
        { Assert.Equal(r.IdOrigem, inversa.IdOrigem); Assert.Equal(r.IdDestino, inversa.IdDestino); }
        else { Assert.Equal(a.Id, r.IdOrigem); Assert.Equal(b.Id, r.IdDestino); }
    }

    [Fact]
    public void RelacoesRecusamEscopoAutoRelacaoSemanticaEOrigemInvalidos()
    {
        var a = BrainEfTests.Novo(Guid.NewGuid()); var b = BrainEfTests.Novo(a.IdEspacoDeConhecimento);
        Assert.Throws<ArgumentException>(() => R(a, a));
        Assert.Throws<ArgumentException>(() => R(a, BrainEfTests.Novo(Guid.NewGuid())));
        Assert.Throws<ArgumentException>(() => R(a, BrainEfTests.Novo(a.IdEspacoDeConhecimento, Guid.NewGuid())));
        Assert.Throws<ArgumentException>(() => R(a, b, TipoDeRelacao.ResolvidoPor));
        Assert.Throws<ArgumentException>(() => R(a, b, TipoDeRelacao.ProduziuAprendizado));
        Assert.Throws<ArgumentException>(() => R(a, b, TipoDeRelacao.Substitui));
        Assert.Throws<ArgumentException>(() => R(a, b, (TipoDeRelacao)999));
        Assert.Throws<ArgumentNullException>(() => new RelacaoDeConhecimento(a, b, TipoDeRelacao.Referencia, null!, DateTimeOffset.UtcNow));
    }

    [PostgreSqlFact]
    public async Task CadeiaCiclosDeduplicacaoLimitesEIsolamentoSaoPersistidos()
    {
        await using var banco = await Banco.CriarAsync();
        var espaco = new EspacoDeConhecimento(Guid.NewGuid(), "A");
        var itens = new[] { Item(espaco.Id, TipoDeConhecimento.Incidente), Item(espaco.Id, TipoDeConhecimento.Solucao), Item(espaco.Id, TipoDeConhecimento.Aprendizado) };
        await using (var c = banco.Contexto()) { c.Add(espaco); c.AddRange(itens); await c.SaveChangesAsync(); }
        using var provider = Provider(banco.ConnectionString); using var scope = EscoposBrainDeTeste.Criar(provider, espaco.IdUsuario, espaco.Id);
        var service = scope.ServiceProvider.GetRequiredService<IRelacaoDeConhecimentoAppService>();
        var p = Proveniencia();
        await service.RelacionarAsync(espaco.Id, null, itens[0].Id, itens[1].Id, TipoDeRelacao.ResolvidoPor, p);
        await service.RelacionarAsync(espaco.Id, null, itens[1].Id, itens[2].Id, TipoDeRelacao.ProduziuAprendizado, p);
        var primeira = await service.RelacionarAsync(espaco.Id, null, itens[2].Id, itens[0].Id, TipoDeRelacao.RelacionadoA, p);
        var mesma = await service.RelacionarAsync(espaco.Id, null, itens[0].Id, itens[2].Id, TipoDeRelacao.RelacionadoA, p);
        Assert.Equal(primeira.Id, mesma.Id);
        var grafo = await service.ConsultarVizinhancaAsync(espaco.Id, null, itens[0].Id, 3);
        Assert.Equal(3, grafo.Relacoes.Count); Assert.False(grafo.LimiteAtingido);
        Assert.Equal(2, (await service.ConsultarVizinhancaAsync(espaco.Id, null, itens[0].Id, 1)).Relacoes.Count);
        var limitado = await service.ConsultarVizinhancaAsync(espaco.Id, null, itens[0].Id, 3, 1);
        Assert.Single(limitado.Relacoes); Assert.True(limitado.LimiteAtingido);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.ConsultarVizinhancaAsync(espaco.Id, null, itens[0].Id, 4));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ConsultarVizinhancaAsync(Guid.NewGuid(), null, itens[0].Id));
        await using var verificar = banco.Contexto();
        Assert.Equal(3, await verificar.RelacoesDeConhecimento.CountAsync());
        Assert.All(await verificar.RelacoesDeConhecimento.ToListAsync(), r => Assert.Equal(p.IdResponsavel, r.Proveniencia.IdResponsavel));
        verificar.ChangeTracker.Clear(); // Exercitar FK no banco sem dependentes já rastreados pelo EF.
        verificar.Remove((await verificar.Conhecimentos.FindAsync(itens[0].Id))!);
        await Assert.ThrowsAsync<DbUpdateException>(() => verificar.SaveChangesAsync());
    }

    [PostgreSqlFact]
    public async Task SubstituicaoCriaRelacaoNaMesmaTransacaoDoHistorico()
    {
        await using var banco = await Banco.CriarAsync(); var espaco = new EspacoDeConhecimento(Guid.NewGuid(), "A");
        var a = BrainEfTests.Novo(espaco.Id); var b = BrainEfTests.Novo(espaco.Id);
        await using (var c = banco.Contexto()) { c.AddRange(espaco, a, b); await c.SaveChangesAsync(); }
        using var provider = Provider(banco.ConnectionString); using var scope = EscoposBrainDeTeste.Criar(provider, espaco.IdUsuario, espaco.Id);
        Assert.True(await scope.ServiceProvider.GetRequiredService<IConhecimentoAppService>().SubstituirAsync(a.Id, b.Id, 1, Proveniencia()));
        await using var verificar = banco.Contexto();
        var salvo = (await verificar.Conhecimentos.FindAsync(a.Id))!;
        Assert.Equal(StatusDoConhecimento.Substituido, salvo.Status); Assert.Equal(2, salvo.Revisao);
        var relacao = Assert.Single(await verificar.RelacoesDeConhecimento.ToListAsync());
        Assert.Equal(b.Id, relacao.IdOrigem); Assert.Equal(a.Id, relacao.IdDestino); Assert.Equal(TipoDeRelacao.Substitui, relacao.Tipo);
    }

    internal static ServiceProvider Provider(string connection) => new ServiceCollection().AddApplication()
        .AddInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["ConnectionStrings:Dante"] = connection }).Build())
        .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    internal static ProvenienciaDto Proveniencia() => new() { IdResponsavel = Guid.NewGuid(), Origem = "usuário", ReferenciaDaFonte = "conversa:1", TrechoDaFonte = "evidência" };
    private static RelacaoDeConhecimento R(Conhecimento a, Conhecimento b, TipoDeRelacao tipo = TipoDeRelacao.Referencia) => new(a,b,tipo,BrainEfTests.Origem(),DateTimeOffset.UtcNow);
    private static Conhecimento Item(Guid espaco, TipoDeConhecimento tipo) => new(espaco,null,tipo,"conteúdo",null,StatusDoConhecimento.Inferido,null,Sensibilidade.Pessoal,null,null,[],BrainEfTests.Origem(),DateTimeOffset.UtcNow);
}
