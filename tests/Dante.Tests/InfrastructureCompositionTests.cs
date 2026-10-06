using Dante.Application;
using Dante.Application.Conhecimentos;
using Dante.Application.ConversaDoBrain;
using Dante.Application.SegurancaDoBrain;
using Dante.Infrastructure;
using Dante.Infrastructure.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dante.Tests;

public sealed class InfrastructureCompositionTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SemConexaoMantemAdaptersLocaisSemBrain(string? conexao)
    {
        var services = new ServiceCollection().AddApplication().AddInfrastructure(Configuracao(conexao));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true, ValidateOnBuild = true
        });
        Assert.Null(provider.GetService<DanteDbContext>());
        Assert.Null(provider.GetService<IConhecimentoRepository>());
        Assert.Null(provider.GetService<AutorizacaoDoBrain>());
        Assert.Null(provider.GetService<IEstadoDeConversaDoBrain>());
        Assert.Contains(services, s => s.ServiceType == typeof(Dante.Application.Contextos.IWorkspaceGeral));
        Assert.Contains(services, s => s.ServiceType == typeof(Dante.Application.Agentes.ICodexRunner));
    }

    [Fact]
    public void ComConexaoPreservaLifetimesSemDuplicarRegistros()
    {
        var services = new ServiceCollection().AddApplication().AddInfrastructure(
            Configuracao("Host=localhost;Database=nao_conectar"));
        Assert.All(services.GroupBy(s => s.ServiceType), grupo => Assert.Single(grupo));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true, ValidateOnBuild = true
        });
        using var primeiro = provider.CreateScope();
        using var segundo = provider.CreateScope();
        foreach (var tipo in new[] { typeof(DanteDbContext), typeof(IConhecimentoRepository), typeof(AutorizacaoDoBrain) })
        {
            var instancia = primeiro.ServiceProvider.GetRequiredService(tipo);
            Assert.Same(instancia, primeiro.ServiceProvider.GetRequiredService(tipo));
            Assert.NotSame(instancia, segundo.ServiceProvider.GetRequiredService(tipo));
            Assert.Equal(ServiceLifetime.Scoped, Assert.Single(services, s => s.ServiceType == tipo).Lifetime);
        }
        Assert.Same(primeiro.ServiceProvider.GetRequiredService<IEstadoDeConversaDoBrain>(),
            segundo.ServiceProvider.GetRequiredService<IEstadoDeConversaDoBrain>());
        Assert.Equal(ServiceLifetime.Singleton,
            Assert.Single(services, s => s.ServiceType == typeof(IEstadoDeConversaDoBrain)).Lifetime);
    }

    private static IConfiguration Configuracao(string? conexao) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Dante"] = conexao }).Build();
}
