using Dante.Application;
using Dante.Application.ContextosDeTrabalho;
using Dante.Application.SegurancaDoBrain;
using Dante.Domain.ContextosDeTrabalho;
using Dante.Domain.Conhecimentos;
using Dante.Domain.EspacosDeConhecimento;
using Dante.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
namespace Dante.Tests;

public sealed class ContextoDeTrabalhoTests
{
    private static DadosDoContexto Dados(string progresso = "feito") => new("entregar", "issue", progresso, [], ["src/app.cs"], ["validar"], ["testar"], "build aprovado");
    [Fact]
    public void SnapshotSubstituiConteudoSemAcumularHistorico()
    {
        var lista = new List<string> { "a" }; var agora = DateTimeOffset.UtcNow;
        var c = new ContextoDeTrabalho(Guid.NewGuid(), null, Dados() with { Pendencias = lista }, Sensibilidade.Trabalho, Guid.NewGuid(), "usuário", agora, null);
        lista.Add("b"); Assert.Single(c.Dados.Pendencias);
        c.Substituir(1, Dados("novo"), Sensibilidade.Trabalho, Guid.NewGuid(), "outro", agora, null);
        c.Substituir(2, Dados("final"), Sensibilidade.Trabalho, Guid.NewGuid(), "terceiro", agora, null);
        Assert.Equal(3, c.Revisao); Assert.Equal(2, c.AuditoriaAnterior!.Revisao); Assert.Equal("final", c.Dados.Progresso);
        Assert.Throws<InvalidOperationException>(() => c.Substituir(1, Dados(), Sensibilidade.Trabalho, Guid.NewGuid(), "origem", agora, null));
    }
    [Fact]
    public void LimitesEExpiracaoSaoExplicitos()
    {
        var agora = DateTimeOffset.UtcNow;
        var c = new ContextoDeTrabalho(Guid.NewGuid(), null, Dados(), Sensibilidade.Trabalho, Guid.NewGuid(), "usuário", agora, agora.AddMinutes(1));
        Assert.False(c.EstaAtivoEm(agora.AddMinutes(1)));
        Assert.Throws<ArgumentException>(() => c.Substituir(1, Dados() with { Pendencias = Enumerable.Repeat(new string('x',500),20).ToArray() }, Sensibilidade.Trabalho, Guid.NewGuid(), "origem", agora, null));
        Assert.Equal(1, c.Revisao);
    }
    [PostgreSqlFact]
    public async Task NovaSessaoRetomaSnapshotSemConhecimentoPermanenteEConcorrenciaPreservaVersao()
    {
        await using var banco = await Banco.CriarAsync(); var espaco = new EspacoDeConhecimento(Guid.NewGuid(), "Trabalho");
        await using (var c = banco.Contexto()) { c.Add(espaco); await c.SaveChangesAsync(); }
        using var provider = Provider(banco.ConnectionString); var acesso = new AcessoAoBrain(espaco.IdUsuario, espaco.Id, null);
        using (var scope = provider.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ContextoDeTrabalhoAppService>();
            await service.SubstituirAsync(acesso, 0, Dados(), Sensibilidade.Trabalho, "seleção do usuário");
            await Assert.ThrowsAsync<ArgumentException>(() => service.SubstituirAsync(acesso, 1, Dados("<thinking> segredo"), Sensibilidade.Trabalho, "agente"));
            await Assert.ThrowsAsync<ArgumentException>(() => service.SubstituirAsync(acesso, 1, Dados("senha=ab123456"), Sensibilidade.Secreto, "agente"));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RetomarAsync(acesso with { IdUsuario = Guid.NewGuid() }));
        }
        using var a = provider.CreateScope(); using var b = provider.CreateScope();
        var sa = a.ServiceProvider.GetRequiredService<ContextoDeTrabalhoAppService>(); var sb = b.ServiceProvider.GetRequiredService<ContextoDeTrabalhoAppService>();
        Assert.Equal("feito", (await sa.RetomarAsync(acesso))!.Dados.Progresso); await sb.RetomarAsync(acesso);
        await sa.SubstituirAsync(acesso, 1, Dados("novo"), Sensibilidade.Trabalho, "revisão");
        await Assert.ThrowsAsync<Dante.Application.Comum.ConflitoDeConcorrenciaException>(() => sb.SubstituirAsync(acesso, 1, Dados("perdido"), Sensibilidade.Trabalho, "revisão concorrente"));
        await using var verificar = banco.Contexto();
        Assert.Empty(await verificar.Set<Conhecimento>().ToListAsync());
        Assert.Equal("novo", (await verificar.Set<ContextoDeTrabalho>().SingleAsync()).Dados.Progresso);
        using var novaSessao = provider.CreateScope(); Assert.Equal(2, (await novaSessao.ServiceProvider.GetRequiredService<ContextoDeTrabalhoAppService>().RetomarAsync(acesso))!.Revisao);
    }
    [PostgreSqlFact]
    public async Task ContextoSecretNaoRetomaAutomaticamenteNemDepoisDePermissao()
    {
        await using var banco = await Banco.CriarAsync(); var espaco = new EspacoDeConhecimento(Guid.NewGuid(), "Pessoal");
        await using (var c = banco.Contexto()) { c.Add(espaco); await c.SaveChangesAsync(); }
        using var provider = Provider(banco.ConnectionString); using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ContextoDeTrabalhoAppService>(); var acesso = new AcessoAoBrain(espaco.IdUsuario, espaco.Id, null, true, true);
        await service.SubstituirAsync(acesso, 0, Dados(), Sensibilidade.Secreto, "usuário");
        Assert.Null(await service.RetomarAsync(acesso));
        var expirado = new ContextoDeTrabalho(Guid.NewGuid(), null, Dados(), Sensibilidade.Trabalho, Guid.NewGuid(), "origem", DateTimeOffset.UtcNow.AddHours(-2), DateTimeOffset.UtcNow.AddHours(-1));
        Assert.False(expirado.EstaAtivoEm(DateTimeOffset.UtcNow));
    }
    private static ServiceProvider Provider(string connection) => new ServiceCollection().AddApplication().AddInfrastructure(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["ConnectionStrings:Dante"] = connection }).Build()).BuildServiceProvider();
}
