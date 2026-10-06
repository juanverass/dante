using Dante.Application.CapturaDeConhecimento;
using Dante.Domain.CapturaDeConhecimento;
using Dante.Domain.Conhecimentos;
using Dante.Domain.EspacosDeConhecimento;
using Dante.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
namespace Dante.Tests;

public sealed class CapturaDeConhecimentoTests
{
    [Theory]
    [InlineData(TipoDeConhecimento.Decisao)]
    [InlineData(TipoDeConhecimento.Preferencia)]
    [InlineData(TipoDeConhecimento.Incidente)]
    [InlineData(TipoDeConhecimento.Solucao)]
    [InlineData(TipoDeConhecimento.Aprendizado)]
    [InlineData(TipoDeConhecimento.Procedimento)]
    [InlineData(TipoDeConhecimento.Fato)]
    [InlineData(TipoDeConhecimento.Referencia)]
    public void CapturaSelecionadaExigePromocaoExplicitaEAtorAuditavel(TipoDeConhecimento tipo)
    {
        var candidato = Novo(tipo: tipo);
        Assert.Equal(EstadoDoCandidato.Pendente, candidato.Estado); Assert.Null(candidato.IdConhecimento);
        var responsavel = BrainEfTests.Origem();
        var conhecimento = candidato.Promover(1, responsavel, DateTimeOffset.UtcNow);
        Assert.Equal(StatusDoConhecimento.Confirmado, conhecimento.Status);
        Assert.Equal(conhecimento.Id, candidato.IdConhecimento);
        Assert.Equal(responsavel.IdResponsavel, candidato.Historico[1].Proveniencia.IdResponsavel);
        Assert.Equal("capturado", candidato.Historico[0].Acao);
        Assert.Throws<InvalidOperationException>(() => candidato.Promover(2, responsavel, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void InferenciasDoAgenteNaoViramFatosPorConfirmacao()
    {
        var candidato = Novo(natureza: NaturezaDoConteudo.ConclusaoDoAgente);
        Assert.Equal(TipoDeConhecimento.Inferencia, candidato.Tipo);
        var conhecimento = candidato.Promover(1, BrainEfTests.Origem(), DateTimeOffset.UtcNow);
        Assert.Equal(TipoDeConhecimento.Inferencia, conhecimento.Tipo); Assert.Equal(StatusDoConhecimento.Inferido, conhecimento.Status);
        Assert.Throws<InvalidOperationException>(() => conhecimento.Confirmar(1, BrainEfTests.Origem(), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void CorrecaoRejeicaoDescarteEPromocaoExigemRevisaoEEvidencia()
    {
        var candidato = Novo(); var p = BrainEfTests.Origem();
        Assert.Throws<InvalidOperationException>(() => candidato.Corrigir(0, TipoDeConhecimento.Decisao, "novo", Sensibilidade.Pessoal, "porque", p, DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => candidato.Corrigir(1, TipoDeConhecimento.Decisao, "novo", Sensibilidade.Pessoal, "porque", new(Guid.NewGuid(), "sem fonte"), DateTimeOffset.UtcNow));
        candidato.Corrigir(1, TipoDeConhecimento.Decisao, "novo", Sensibilidade.Confidencial, "porque", p, DateTimeOffset.UtcNow);
        Assert.Equal("conteúdo", candidato.Historico[0].Conteudo); Assert.Equal("novo", candidato.Conteudo);
        candidato.Rejeitar(2, p, DateTimeOffset.UtcNow);
        Assert.Null(candidato.IdConhecimento); Assert.Equal("rejeitado", candidato.Historico[2].Acao);
        var descartado = Novo(); descartado.Descartar(1, p, DateTimeOffset.UtcNow);
        Assert.Equal(EstadoDoCandidato.Descartado, descartado.Estado); Assert.Null(descartado.IdConhecimento);
        Assert.Throws<InvalidOperationException>(() => descartado.Promover(2, p, DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => new CandidatoDeConhecimento(Guid.NewGuid(), null, TipoDeConhecimento.Fato, "conteúdo",
            Sensibilidade.Pessoal, NaturezaDoConteudo.DitoPeloUsuario, ModoDeCaptura.Explicita, "porque", new(Guid.NewGuid(), "sem fonte"), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void EquivalenciaNaoConfundeNaturezaClassificacaoSensibilidadeNemCodigo()
    {
        Assert.Equal(Novo().Impressao, Novo(conteudo: " conteúdo ").Impressao);
        Assert.NotEqual(Novo().Impressao, Novo(natureza: NaturezaDoConteudo.ConclusaoDoAgente).Impressao);
        Assert.NotEqual(Novo().Impressao, Novo(tipo: TipoDeConhecimento.Decisao).Impressao);
        Assert.NotEqual(Novo(conteudo: "Foo").Impressao, Novo(conteudo: "foo").Impressao);
        Assert.NotEqual(Novo(conteudo: "a  b").Impressao, Novo(conteudo: "a b").Impressao);
    }

    [PostgreSqlFact]
    public async Task ConversaSelecionadaESugestaoDeduplicamSemPersistirConhecimentoAteConfirmar()
    {
        await using var banco = await Banco.CriarAsync(); var espaco = new EspacoDeConhecimento(Guid.NewGuid(), "A");
        await using (var c = banco.Contexto()) { c.Add(espaco); await c.SaveChangesAsync(); }
        using var provider = RelacoesDeConhecimentoTests.Provider(banco.ConnectionString); using var scope = EscoposBrainDeTeste.Criar(provider, espaco.IdUsuario, espaco.Id);
        var service = scope.ServiceProvider.GetRequiredService<ICapturaDeConhecimentoAppService>();
        var captura = Captura(espaco.Id);
        var primeiro = await service.CapturarAsync(captura);
        var mesmo = await service.CapturarAsync(captura with { Conteudo = " conteúdo ", Modo = ModoDeCaptura.SugestaoAutomatica,
            Proveniencia = RelacoesDeConhecimentoTests.Proveniencia() with { ReferenciaDaFonte = "conversa:2" } });
        Assert.Equal(primeiro.Id, mesmo.Id); Assert.Equal(2, mesmo.Revisao);
        await using (var verificar = banco.Contexto()) { Assert.Empty(await verificar.Conhecimentos.ToListAsync()); Assert.Single(await verificar.CandidatosDeConhecimento.ToListAsync()); }
        var p = RelacoesDeConhecimentoTests.Proveniencia();
        var corrigido = await service.CorrigirAsync(espaco.Id, null, mesmo.Id, mesmo.Revisao,
            captura with { Conteudo = "corrigido", Tipo = TipoDeConhecimento.Decisao, Proveniencia = p });
        var id = await service.ConfirmarAsync(espaco.Id, null, corrigido.Id, corrigido.Revisao, p);
        await using (var verificar = banco.Contexto())
        {
            var conhecimento = (await verificar.Conhecimentos.FindAsync(id))!;
            Assert.Equal(StatusDoConhecimento.Confirmado, conhecimento.Status);
            var candidato = (await verificar.CandidatosDeConhecimento.FindAsync(mesmo.Id))!;
            Assert.Equal(4, candidato.Revisao); Assert.Equal("conteúdo", candidato.Historico[0].Conteudo);
            Assert.Equal(p.IdResponsavel, candidato.Historico[^1].Proveniencia.IdResponsavel);
        }
        Assert.Empty(await service.ListarPendentesAsync(espaco.Id, null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ConfirmarAsync(espaco.Id, null, mesmo.Id, 4, p));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ConfirmarAsync(Guid.NewGuid(), null, mesmo.Id, 4, p));
    }

    [PostgreSqlFact]
    public async Task RejeitarDescartarEInferenciaNaoPoluemCanonico()
    {
        await using var banco = await Banco.CriarAsync(); var espaco = new EspacoDeConhecimento(Guid.NewGuid(), "A");
        await using (var c = banco.Contexto()) { c.Add(espaco); await c.SaveChangesAsync(); }
        using var provider = RelacoesDeConhecimentoTests.Provider(banco.ConnectionString); using var scope = EscoposBrainDeTeste.Criar(provider, espaco.IdUsuario, espaco.Id);
        var service = scope.ServiceProvider.GetRequiredService<ICapturaDeConhecimentoAppService>(); var p = RelacoesDeConhecimentoTests.Proveniencia();
        var rejeitado = await service.CapturarAsync(Captura(espaco.Id));
        await service.RejeitarAsync(espaco.Id, null, rejeitado.Id, 1, p);
        var descartado = await service.CapturarAsync(Captura(espaco.Id) with { Conteudo = "descartar" });
        await service.DescartarAsync(espaco.Id, null, descartado.Id, 1, p);
        await using (var verificar = banco.Contexto()) { Assert.Empty(await verificar.Conhecimentos.ToListAsync()); }
        var inferencia = await service.CapturarAsync(Captura(espaco.Id) with { Natureza = NaturezaDoConteudo.ConclusaoDoAgente, Modo = ModoDeCaptura.SugestaoAutomatica });
        var id = await service.ConfirmarAsync(espaco.Id, null, inferencia.Id, 1, p);
        await using (var verificar = banco.Contexto())
        { var k = (await verificar.Conhecimentos.FindAsync(id))!; Assert.Equal(TipoDeConhecimento.Inferencia, k.Tipo); Assert.Equal(StatusDoConhecimento.Inferido, k.Status); }
        var equivalente = await service.CapturarAsync(Captura(espaco.Id));
        Assert.Equal(rejeitado.Id, equivalente.Id); Assert.Equal(EstadoDoCandidato.Rejeitado, equivalente.Estado);
    }

    [PostgreSqlFact]
    public async Task ConsolidacaoPromoveAprendizadoECadeiaNaMesmaTransacao()
    {
        await using var banco = await Banco.CriarAsync(); var espaco = new EspacoDeConhecimento(Guid.NewGuid(), "A");
        var incidente = Item(espaco.Id, TipoDeConhecimento.Incidente); var solucao = Item(espaco.Id, TipoDeConhecimento.Solucao);
        await using (var c = banco.Contexto()) { c.AddRange(espaco, incidente, solucao); await c.SaveChangesAsync(); }
        using var provider = RelacoesDeConhecimentoTests.Provider(banco.ConnectionString); using var scope = EscoposBrainDeTeste.Criar(provider, espaco.IdUsuario, espaco.Id);
        var service = scope.ServiceProvider.GetRequiredService<ICapturaDeConhecimentoAppService>();
        var candidato = await service.CapturarAsync(Captura(espaco.Id) with { Tipo = TipoDeConhecimento.Aprendizado, IdIncidente = incidente.Id, IdSolucao = solucao.Id });
        var id = await service.ConfirmarAsync(espaco.Id, null, candidato.Id, 1, RelacoesDeConhecimentoTests.Proveniencia());
        await using var verificar = banco.Contexto();
        Assert.Equal(3, await verificar.Conhecimentos.CountAsync()); Assert.Equal(2, await verificar.RelacoesDeConhecimento.CountAsync());
        Assert.Equal(TipoDeConhecimento.Aprendizado, (await verificar.Conhecimentos.FindAsync(id))!.Tipo);
    }

    [PostgreSqlFact]
    public async Task ConcorrenciaNaoPromoveDuasVezesERollbackNaoDeixaConhecimentoOrfao()
    {
        await using var banco = await Banco.CriarAsync(); var espaco = new EspacoDeConhecimento(Guid.NewGuid(), "A");
        await using (var c = banco.Contexto()) { c.Add(espaco); await c.SaveChangesAsync(); }
        using var provider = RelacoesDeConhecimentoTests.Provider(banco.ConnectionString); using var scope = EscoposBrainDeTeste.Criar(provider, espaco.IdUsuario, espaco.Id);
        var service = scope.ServiceProvider.GetRequiredService<ICapturaDeConhecimentoAppService>();
        var dto = await service.CapturarAsync(Captura(espaco.Id));
        await using var a = banco.Contexto(); await using var b = banco.Contexto();
        var ca = (await a.CandidatosDeConhecimento.FindAsync(dto.Id))!; var cb = (await b.CandidatosDeConhecimento.FindAsync(dto.Id))!;
        a.Add(ca.Promover(1, BrainEfTests.Origem(), DateTimeOffset.UtcNow)); await new UnitOfWork(a).SalvarAlteracoesAsync();
        b.Add(cb.Promover(1, BrainEfTests.Origem(), DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<Dante.Application.Comum.ConflitoDeConcorrenciaException>(() => new UnitOfWork(b).SalvarAlteracoesAsync());
        await using var verificar = banco.Contexto(); Assert.Single(await verificar.Conhecimentos.ToListAsync());
    }

    [PostgreSqlFact]
    public async Task UniqueSemProjetoNaoPermiteCapturasConcorrentesEquivalentes()
    {
        await using var banco = await Banco.CriarAsync(); var espaco = new EspacoDeConhecimento(Guid.NewGuid(), "A");
        await using (var c = banco.Contexto()) { c.Add(espaco); await c.SaveChangesAsync(); }
        CandidatoDeConhecimento Criar() => new(espaco.Id, null, TipoDeConhecimento.Fato, "conteúdo", Sensibilidade.Pessoal,
            NaturezaDoConteudo.DitoPeloUsuario, ModoDeCaptura.Explicita, "porque", BrainEfTests.Origem(), DateTimeOffset.UtcNow);
        await using var a = banco.Contexto(); await using var b = banco.Contexto();
        a.Add(Criar()); b.Add(Criar());
        await new UnitOfWork(a).SalvarAlteracoesAsync();
        var conflito = await Assert.ThrowsAsync<Dante.Application.Comum.ConflitoDeConcorrenciaException>(() => new UnitOfWork(b).SalvarAlteracoesAsync());
        Assert.Null(conflito.InnerException);
        await using var verificar = banco.Contexto(); Assert.Single(await verificar.CandidatosDeConhecimento.ToListAsync());
    }

    private static CandidatoDeConhecimento Novo(TipoDeConhecimento tipo = TipoDeConhecimento.Fato, string conteudo = "conteúdo", NaturezaDoConteudo natureza = NaturezaDoConteudo.DitoPeloUsuario) =>
        new(Guid.NewGuid(), null, tipo, conteudo, Sensibilidade.Pessoal, natureza, ModoDeCaptura.Explicita, "porque", BrainEfTests.Origem(), DateTimeOffset.UtcNow);
    internal static CapturaDeConhecimentoDto Captura(Guid espaco) => new() { IdEspacoDeConhecimento = espaco, Tipo = TipoDeConhecimento.Fato, Conteudo = "conteúdo",
        Natureza = NaturezaDoConteudo.DitoPeloUsuario, Modo = ModoDeCaptura.Explicita, Justificativa = "pedido de captura selecionada", Proveniencia = RelacoesDeConhecimentoTests.Proveniencia() };
    private static Conhecimento Item(Guid espaco, TipoDeConhecimento tipo) => new(espaco, null, tipo, "evidência", null, StatusDoConhecimento.Inferido, null, Sensibilidade.Pessoal, null, null, [], BrainEfTests.Origem(), DateTimeOffset.UtcNow);
}
