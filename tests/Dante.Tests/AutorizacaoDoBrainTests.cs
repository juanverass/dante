using Dante.Application.SegurancaDoBrain;
using Dante.Domain.EspacosDeConhecimento;
using Dante.Domain.Conhecimentos;
using Dante.Infrastructure.SegurancaDoBrain;
using Dante.Infrastructure.Data;
using Dante.Infrastructure.Modulos.Conhecimentos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Dante.Application;
using Dante.Infrastructure;
using Dante.Application.Conhecimentos;
namespace Dante.Tests;

public sealed class AutorizacaoDoBrainTests
{
    [Fact]
    public void IdentidadeTelegramEstavelEAllowlistFalhaFechado()
    {
        IdentidadeTelegramDoBrain Criar(string? ids) => new(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string,string?> { ["Telegram:AllowedUserIds"] = ids }).Build());
        Assert.Equal(Criar("123").Resolver(123), Criar("123").Resolver(123));
        Assert.NotEqual(Criar("123,456").Resolver(123), Criar("123,456").Resolver(456));
        foreach (var ids in new string?[] { null, "", "123,ruim", "456" })
            Assert.Throws<UnauthorizedAccessException>(() => Criar(ids).Resolver(123));
    }
    [Fact]
    public void EscopoIdentidadeEPermissoesNaoPodemSerForjados()
    {
        var auth = new AutorizacaoDoBrain(); var acesso = new AcessoAoBrain(Guid.NewGuid(), Guid.NewGuid(), null);
        Assert.Throws<UnauthorizedAccessException>(() => auth.Exigir(acesso));
        auth.Estabelecer(new(AutorizacaoDoBrain.TenantLocal, acesso.IdUsuario), acesso);
        auth.Exigir(acesso);
        Assert.Throws<UnauthorizedAccessException>(() => auth.Exigir(acesso with { IdUsuario = Guid.NewGuid() }));
        Assert.Throws<UnauthorizedAccessException>(() => auth.Exigir(acesso with { IdProjeto = Guid.NewGuid() }));
        Assert.Throws<UnauthorizedAccessException>(() => auth.Exigir(acesso with { PermitirSecreto = true }));
        Assert.Throws<InvalidOperationException>(() => auth.Estabelecer(new(Guid.NewGuid(), Guid.NewGuid())));
    }
    [PostgreSqlFact]
    public async Task RepositoriesBloqueiamAcessoPorIdBuscaEEscritaCruzados()
    {
        await using var banco = await Banco.CriarAsync();
        var usuario = Guid.NewGuid(); var espaco = new EspacoDeConhecimento(usuario, "Meu");
        var outro = new EspacoDeConhecimento(Guid.NewGuid(), "Outro");
        var outroTenant = new EspacoDeConhecimento(usuario, "Outro tenant", idTenant: Guid.NewGuid());
        Conhecimento Novo(EspacoDeConhecimento e, Sensibilidade classe = Sensibilidade.Pessoal) => new(e.Id, null,
            TipoDeConhecimento.Fato, "evidência", null, StatusDoConhecimento.Inferido, null, classe,
            null, null, [], new(usuario, "teste"), DateTimeOffset.UtcNow);
        var meu = Novo(espaco); var alheio = Novo(outro); var secreto = Novo(espaco, Sensibilidade.Secreto);
        await using (var admin = banco.Contexto()) { admin.AddRange(espaco, outro, outroTenant, meu, alheio, secreto); await admin.SaveChangesAsync(); }
        var options = new DbContextOptionsBuilder<DanteDbContext>().UseNpgsql(banco.ConnectionString).Options;
        await using (var sem = new DanteDbContext(options, new AutorizacaoDoBrain()))
        {
            Assert.Empty(await sem.EspacosDeConhecimento.ToListAsync());
            Assert.Null(await new ConhecimentoRepository(sem).ObterPorIdAsync(meu.Id));
            sem.Add(Novo(espaco)); await Assert.ThrowsAsync<UnauthorizedAccessException>(() => sem.SaveChangesAsync());
        }
        var auth = new AutorizacaoDoBrain(); auth.Estabelecer(new(AutorizacaoDoBrain.TenantLocal, usuario), new(usuario, espaco.Id, null));
        await using var c = new DanteDbContext(options, auth);
        Assert.Single(await c.EspacosDeConhecimento.ToListAsync());
        Assert.Single(await c.Conhecimentos.ToListAsync());
        Assert.NotNull(await new ConhecimentoRepository(c).ObterPorIdAsync(meu.Id));
        Assert.Null(await new ConhecimentoRepository(c).ObterPorIdAsync(alheio.Id));
        Assert.Null(await new ConhecimentoRepository(c).ObterPorIdAsync(secreto.Id));
        Assert.Null(await c.EspacosDeConhecimento.SingleOrDefaultAsync(x => x.Id == outroTenant.Id));
        c.Add(Novo(outro)); await Assert.ThrowsAsync<UnauthorizedAccessException>(() => c.SaveChangesAsync());
    }
    [PostgreSqlFact]
    public async Task HistoricoNaoRevelaClassificacaoAnteriorMaisRestrita()
    {
        await using var banco = await Banco.CriarAsync(); var usuario = Guid.NewGuid();
        var espaco = new EspacoDeConhecimento(usuario, "Trabalho");
        var item = new Conhecimento(espaco.Id, null, TipoDeConhecimento.Fato, "reservado", null,
            StatusDoConhecimento.Inferido, null, Sensibilidade.Secreto, null, null, [], new(usuario, "manual"), DateTimeOffset.UtcNow);
        item.Corrigir(1, item.Tipo, "público", null, null, Sensibilidade.Publico, null, null, [], item.Proveniencia, DateTimeOffset.UtcNow);
        await using (var c = banco.Contexto()) { c.AddRange(espaco, item); await c.SaveChangesAsync(); }
        using var provider = new ServiceCollection().AddApplication().AddInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string,string?> { ["ConnectionStrings:Dante"] = banco.ConnectionString }).Build()).BuildServiceProvider();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<AutorizacaoDoBrain>().Estabelecer(new(AutorizacaoDoBrain.TenantLocal, usuario), new(usuario, espaco.Id, null));
        var dto = await scope.ServiceProvider.GetRequiredService<IConhecimentoAppService>().ObterPorIdAsync(item.Id);
        Assert.Equal("público", dto!.Conteudo); Assert.Equal(2, dto.Revisao);
        Assert.Equal(2, Assert.Single(dto.Historico).Numero);
    }

}
