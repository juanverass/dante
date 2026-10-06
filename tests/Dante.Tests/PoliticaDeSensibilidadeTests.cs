using Dante.Application.SegurancaDoBrain;
using Dante.Domain.Conhecimentos;
using Dante.Domain.CapturaDeConhecimento;
using Dante.Infrastructure.Modulos;
using Dante.Infrastructure.Persistence;
using Dante.Domain.EspacosDeConhecimento;
namespace Dante.Tests;

public sealed class PoliticaDeSensibilidadeTests
{
    [Theory]
    [InlineData("senha=ab123456")]
    [InlineData("{\"api_key\":\"abc123456\"}")]
    [InlineData("-----BEGIN PRIVATE KEY-----")]
    [InlineData("postgres://user:private@localhost/db")]
    [InlineData("ghp_abcdefghijklmnopqrstuvwxyz")]
    public void CapturaRejeitaValoresSemEcoMesmoSeClassificadosComoSecret(string segredo)
    {
        var erro = Assert.Throws<ArgumentException>(() => ProtecaoDeSegredos.GarantirSeguro(segredo));
        Assert.DoesNotContain(segredo, erro.Message);
        Assert.Equal("[conteúdo protegido]", ProtecaoDeSegredos.Redigir(segredo));
    }
    [Theory]
    [InlineData(FinalidadeDeLeitura.ContextoAutomatico)]
    [InlineData(FinalidadeDeLeitura.Exportacao)]
    [InlineData(FinalidadeDeLeitura.Busca)]
    [InlineData(FinalidadeDeLeitura.IndexacaoExterna)]
    public void SecretNuncaSaiAutomaticamenteMesmoComPermissao(FinalidadeDeLeitura finalidade)
    {
        var item = Novo(Sensibilidade.Secreto); var politica = new PoliticaDeSensibilidade();
        var acesso = new AcessoAoBrain(Guid.NewGuid(), item.IdEspacoDeConhecimento, null, true, true);
        var saida = politica.Projetar(item, acesso, finalidade);
        Assert.True(saida.ConteudoProtegido); Assert.Null(saida.Conteudo); Assert.Null(saida.ReferenciaDaFonte); Assert.Empty(saida.Tags);
        Assert.Equal(item.Id, saida.Id);
        Assert.False(politica.Projetar(item, acesso, FinalidadeDeLeitura.Leitura).ConteudoProtegido);
    }
    [Fact]
    public void ConfidencialExigePermissaoEEscopoExato()
    {
        var item = Novo(Sensibilidade.Confidencial); var politica = new PoliticaDeSensibilidade();
        var acesso = new AcessoAoBrain(Guid.NewGuid(), item.IdEspacoDeConhecimento, null);
        Assert.True(politica.Projetar(item, acesso, FinalidadeDeLeitura.Exportacao).ConteudoProtegido);
        Assert.False(politica.Projetar(item, acesso with { PermitirConfidencial = true }, FinalidadeDeLeitura.Exportacao).ConteudoProtegido);
        Assert.False(politica.PermiteConteudo(item, acesso with { IdProjeto = Guid.NewGuid(), PermitirConfidencial = true }, FinalidadeDeLeitura.Leitura));
    }
    [Fact]
    public void ReferenciaNaoResolveSegredoECorrecaoDeClassificacaoPreservaAuditoria()
    {
        var item = Novo(Sensibilidade.Secreto);
        var p = item.Proveniencia;
        item.Corrigir(1, item.Tipo, item.Conteudo, null, null, Sensibilidade.Trabalho, null, null, [], p, DateTimeOffset.UtcNow);
        Assert.Equal(Sensibilidade.Secreto, item.Historico[0].Sensibilidade);
        Assert.Equal(Sensibilidade.Trabalho, item.Sensibilidade);
        Assert.False(ProtecaoDeSegredos.ContemSegredo("token=secret://host/API_TOKEN"));
    }
    [PostgreSqlFact]
    public async Task LeituraValidaProprietarioAntesDeProjetarConteudo()
    {
        await using var banco = await Banco.CriarAsync(); await using var c = banco.Contexto();
        var espaco = new EspacoDeConhecimento(Guid.NewGuid(), "Pessoal"); var item = Novo(Sensibilidade.Trabalho, espaco.Id);
        c.AddRange(espaco, item); await c.SaveChangesAsync();
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Dante.Application.DependencyInjection.AddApplication(services);
        using var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
        var captura = new Dante.Application.CapturaDeConhecimento.CapturaDeConhecimentoAppService(new CandidatoDeConhecimentoRepository(c),
            new ConhecimentoRepository(c), new EspacoDeConhecimentoRepository(c), new ProjetoRepository(c), new RelacaoDeConhecimentoRepository(c), new UnitOfWork(c),
            Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Dante.Application.Mapeamento.IMapsterTypeAdapter>(provider));
        await Assert.ThrowsAsync<ArgumentException>(() => captura.CapturarAsync(new() { IdEspacoDeConhecimento = espaco.Id,
            Conteudo = "senha=abc123456", Sensibilidade = Sensibilidade.Secreto }));
        Assert.Empty(c.ChangeTracker.Entries<CandidatoDeConhecimento>());
        var servico = new LeituraDoBrainAppService(new ConhecimentoRepository(c), new EspacoDeConhecimentoRepository(c), new ProjetoRepository(c), new());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => servico.LerAsync(item.Id, new(Guid.NewGuid(), espaco.Id, null), FinalidadeDeLeitura.Leitura));
        Assert.Equal(item.Conteudo, (await servico.LerAsync(item.Id, new(espaco.IdUsuario, espaco.Id, null), FinalidadeDeLeitura.Leitura))!.Conteudo);
    }
    private static Conhecimento Novo(Sensibilidade s, Guid? espaco = null) => new(espaco ?? Guid.NewGuid(), null, TipoDeConhecimento.Nota,
        "token=secret://host/API_TOKEN", null, StatusDoConhecimento.Inferido, null, s, null, null, [],
        new(Guid.NewGuid(), "teste", "fonte", null, "trecho"), DateTimeOffset.UtcNow);
}
