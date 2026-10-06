using System.Reflection;
using Dante.Application;
using Dante.Application.Comum;
using Dante.Application.ConversaDoBrain;
using Dante.Application.EspacosDeConhecimento;
using Dante.Application.SegurancaDoBrain;
using Dante.Domain.EspacosDeConhecimento;
using Dante.Infrastructure;
using Dante.Infrastructure.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dante.Tests;

public sealed class ApplicationCompositionTests
{
    [Fact]
    public async Task ApplicationComFakesResolveEExecutaCasoDeUsoSemInfrastructure()
    {
        var repository = new EspacosEmMemoria();
        var uow = new CrudDeExemplo.UnitOfWorkEmMemoria();
        using var provider = new ServiceCollection().AddApplication()
            .AddSingleton<IEspacoDeConhecimentoRepository>(repository).AddSingleton<IUnitOfWork>(uow)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IEspacoDeConhecimentoAppService>();
        var usuario = Guid.NewGuid();
        var dto = await service.AdicionarAsync(new() { IdUsuario = usuario, Nome = "Trabalho" });
        Assert.NotEqual(Guid.Empty, dto.Id);
        Assert.Equal(usuario, Assert.Single(repository.Itens.Values).IdUsuario);
        Assert.Equal(1, uow.Salvamentos);
    }

    [Fact]
    public void ApplicationRegistraUmaVezEResolveTodosOsServicosComAdaptersConfigurados()
    {
        var services = new ServiceCollection().AddApplication().AddApplication();
        var proprios = services.Select(s => s.ServiceType).ToArray();
        services.AddInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Dante"] = "Host=localhost;Database=nao_conectar"
        }).Build());
        Assert.All(services.GroupBy(s => s.ServiceType), grupo => Assert.Single(grupo));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true, ValidateOnBuild = true
        });
        using var primeiro = provider.CreateScope();
        using var segundo = provider.CreateScope();
        var implementacoes = new HashSet<Type>();
        foreach (var tipo in proprios)
        {
            var instancia = primeiro.ServiceProvider.GetRequiredService(tipo);
            implementacoes.Add(instancia.GetType());
            Assert.Same(instancia, primeiro.ServiceProvider.GetRequiredService(tipo));
            var descriptor = Assert.Single(services, s => s.ServiceType == tipo);
            if (descriptor.Lifetime == ServiceLifetime.Scoped)
                Assert.NotSame(instancia, segundo.ServiceProvider.GetRequiredService(tipo));
            else Assert.Same(instancia, segundo.ServiceProvider.GetRequiredService(tipo));
        }
        var casos = typeof(AutorizacaoDoBrain).Assembly.GetTypes().Where(t =>
            t.IsPublic && !t.IsAbstract && !t.IsInterface &&
            (t.Name.EndsWith("AppService", StringComparison.Ordinal) || t.Name.EndsWith("DaConversa", StringComparison.Ordinal)));
        Assert.All(casos, tipo => Assert.Contains(tipo, implementacoes));
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<ConversaDoBrainAppService>());
    }

    // #209: com um fake para cada porta, todo serviço próprio resolve sem Infrastructure, banco ou host.
    [Fact]
    public void ApplicationResolveTodosOsServicosComFakesDosPorts()
    {
        var services = new ServiceCollection().AddApplication();
        var proprios = services.Select(s => s.ServiceType).ToArray();
        var portas = typeof(AutorizacaoDoBrain).Assembly.GetTypes().Where(t =>
            t is { IsInterface: true, IsPublic: true, IsGenericTypeDefinition: false } && !proprios.Contains(t)).ToArray();
        Assert.Contains(typeof(IUnitOfWork), portas);
        foreach (var porta in portas) services.AddScoped(porta, _ => PortaFalsa.Criar(porta));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        using var scope = provider.CreateScope();
        Assert.All(proprios, tipo => Assert.NotNull(scope.ServiceProvider.GetRequiredService(tipo)));
    }

    [Fact]
    public void AddApplicationEIdempotenteEPreservaRegistrosAnteriores()
    {
        var uma = new ServiceCollection().AddApplication();
        var duas = new ServiceCollection().AddApplication().AddApplication();
        Assert.Equal(uma.Select(s => (s.ServiceType, s.Lifetime)), duas.Select(s => (s.ServiceType, s.Lifetime)));

        var fake = new PoliticaDeSensibilidade();
        var services = new ServiceCollection().AddSingleton(fake).AddApplication();
        Assert.Same(fake, Assert.Single(services, s => s.ServiceType == typeof(PoliticaDeSensibilidade)).ImplementationInstance);
    }

    // Mapping e policy são singletons sem estado de requisição; autorização, AppServices e casos da conversa são
    // scoped, porque dependem de repositories/UoW scoped. Nenhum serviço próprio é transient.
    [Fact]
    public void LifetimesDaApplicationSaoCoerentes()
    {
        var services = new ServiceCollection().AddApplication();
        Type[] singletons = [typeof(Dante.Application.Mapeamento.IMapsterTypeAdapter), typeof(PoliticaDeSensibilidade)];
        Assert.All(services, s => Assert.Equal(singletons.Contains(s.ServiceType) ? ServiceLifetime.Singleton : ServiceLifetime.Scoped, s.Lifetime));

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        Assert.All(singletons, tipo => Assert.NotNull(provider.GetRequiredService(tipo)));
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<AutorizacaoDoBrain>());
    }

    [Fact]
    public void InfrastructureNaoRegistraImplementacoesDaApplication()
    {
        var application = typeof(AutorizacaoDoBrain).Assembly;
        var services = new ServiceCollection().AddInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["ConnectionStrings:Dante"] = "Host=localhost;Database=nao_conectar" }).Build());
        Assert.DoesNotContain(services, s => s.ImplementationType?.Assembly == application);
        Assert.DoesNotContain(services, s => s.ImplementationFactory?.Method.DeclaringType?.Assembly == application);
        using var provider = services.BuildServiceProvider();
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<DanteDbContext>());
    }

    // Fake genérico de porta: basta para compor; qualquer chamada falha explicitamente.
    public class PortaFalsa : DispatchProxy
    {
        internal static object Criar(Type porta) => DispatchProxy.Create(porta, typeof(PortaFalsa));
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new NotSupportedException($"Porta falsa: {targetMethod?.Name}");
    }

    private sealed class EspacosEmMemoria : IEspacoDeConhecimentoRepository
    {
        internal Dictionary<Guid, EspacoDeConhecimento> Itens { get; } = [];
        public Task<EspacoDeConhecimento?> ObterPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Itens.GetValueOrDefault(id));
        public Task AdicionarAsync(EspacoDeConhecimento entidade, CancellationToken cancellationToken = default)
        {
            Itens.Add(entidade.Id, entidade);
            return Task.CompletedTask;
        }
        public void Atualizar(EspacoDeConhecimento entidade) => Itens[entidade.Id] = entidade;
        public void Remover(EspacoDeConhecimento entidade) => Itens.Remove(entidade.Id);
        public Task<IReadOnlyList<EspacoDeConhecimento>> ListarDoUsuarioAsync(Guid idUsuario, string? trechoDoNome,
            bool incluirArquivados, int limite, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<EspacoDeConhecimento>>(Itens.Values.Where(e => e.IdUsuario == idUsuario).Take(limite).ToArray());
    }
}
