using Dante.Application;
using Dante.Application.Mapeamento;
using Microsoft.Extensions.DependencyInjection;

namespace Dante.Tests;

public sealed class MapeamentoTests
{
    [Fact]
    public void OutputOnlyContainsExplicitFieldsEvenWhenNamesMatch()
    {
        using var provider = Criar(config => config.Registrar<ExemploEntidade, ExemploDto>(origem =>
            new ExemploDto { Id = origem.Id, Nome = origem.Nome }));
        var entidade = new ExemploEntidade("nome", "segredo interno");
        var dto = provider.GetRequiredService<IMapsterTypeAdapter>().Mapear<ExemploEntidade, ExemploDto>(entidade);
        Assert.Equal(entidade.Id, dto.Id);
        Assert.Equal("nome", dto.Nome);
        Assert.Null(dto.Segredo);
        Assert.Equal(Guid.Empty, dto.IdTenant);
    }

    [Fact]
    public void CreationUsesDomainConstructorAndDoesNotCopyInputIdentityOrSecrets()
    {
        using var provider = Criar(config => config.Registrar<ExemploDto, ExemploEntidade>(origem =>
            new ExemploEntidade(origem.Nome, "definido pelo domínio")));
        var mapper = provider.GetRequiredService<IMapsterTypeAdapter>();
        var dto = new ExemploDto { Id = Guid.NewGuid(), IdTenant = Guid.NewGuid(), Nome = "válido", Segredo = "entrada maliciosa" };
        var entidade = mapper.Mapear<ExemploDto, ExemploEntidade>(dto);
        Assert.NotEqual(dto.Id, entidade.Id);
        Assert.Equal("definido pelo domínio", entidade.Segredo);
        Assert.Throws<ArgumentException>(() => mapper.Mapear<ExemploDto, ExemploEntidade>(new ExemploDto { Nome = "" }));
    }

    [Fact]
    public void UnregisteredPairsReverseDirectionsAndSearchContractsAreRejected()
    {
        using var provider = Criar(config => config.Registrar<ExemploEntidade, ExemploDto>(origem =>
            new ExemploDto { Nome = origem.Nome }));
        var mapper = provider.GetRequiredService<IMapsterTypeAdapter>();
        Assert.Throws<InvalidOperationException>(() => mapper.Mapear<ExemploDto, ExemploEntidade>(new ExemploDto()));
        Assert.Throws<InvalidOperationException>(() => mapper.Mapear<ExemploSearchDto, ExemploDto>(new ExemploSearchDto("arbitrário")));
        Assert.Throws<InvalidOperationException>(() => mapper.Mapear<ExemploDto, ExemploDto>(new ExemploDto()));
        Assert.Throws<ArgumentNullException>(() => mapper.Mapear<ExemploEntidade, ExemploDto>(null!));
    }

    [Fact]
    public void ConfigurationIsImmutableAfterResolutionAndPairsCannotBeOverwritten()
    {
        ConfiguracaoMapeamento? captured = null;
        using var provider = Criar(config =>
        {
            captured = config;
            config.Registrar<ExemploDto, string>(origem => origem.Nome);
        });
        var mapper = provider.GetRequiredService<IMapsterTypeAdapter>();
        Assert.Equal("nome", mapper.Mapear<ExemploDto, string>(new ExemploDto { Nome = "nome" }));
        Assert.Throws<InvalidOperationException>(() => captured!.Registrar<ExemploDto, string>(origem => origem.Segredo!));
        using var duplicate = Criar(config =>
        {
            config.Registrar<ExemploDto, string>(origem => origem.Nome);
            config.Registrar<ExemploDto, string>(origem => origem.Segredo!);
        });
        Assert.Throws<InvalidOperationException>(() => duplicate.GetRequiredService<IMapsterTypeAdapter>());
    }

    [Fact]
    public async Task ProvidersAreIsolatedAndTheSharedMapperIsSafeForConcurrentReads()
    {
        using var a = Criar(config => config.Registrar<ExemploDto, string>(origem => "A:" + origem.Nome));
        using var b = Criar(config => config.Registrar<ExemploDto, string>(origem => "B:" + origem.Nome));
        var mapper = a.GetRequiredService<IMapsterTypeAdapter>();
        var outputs = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(() =>
            mapper.Mapear<ExemploDto, string>(new ExemploDto { Nome = i.ToString() }))));
        Assert.Equal(Enumerable.Range(0, 20).Select(i => "A:" + i), outputs);
        Assert.Equal("B:nome", b.GetRequiredService<IMapsterTypeAdapter>().Mapear<ExemploDto, string>(new ExemploDto { Nome = "nome" }));
    }

    [Fact]
    public void CompositionIsIdempotentAndModulesShareTheSameAdapter()
    {
        var services = new ServiceCollection();
        services.AddApplication().AddApplication();
        services.AddMapeamentos(config => config.Registrar<ExemploDto, string>(origem => origem.Nome));
        services.AddMapeamentos(config => config.Registrar<ExemploSearchDto, string>(origem => origem.Termo));
        services.AddApplication();
        using var provider = services.BuildServiceProvider();
        var mapper = Assert.Single(provider.GetServices<IMapsterTypeAdapter>());
        Assert.Same(mapper, provider.GetRequiredService<IMapsterTypeAdapter>());
        Assert.Equal("termo", mapper.Mapear<ExemploSearchDto, string>(new ExemploSearchDto("termo")));
        var appService = ActivatorUtilities.CreateInstance<ExemploAppService>(provider);
        Assert.Equal("nome", appService.Consultar(new ExemploDto { Nome = "nome" }));
    }

    // #204: cada feature registra seus pares; AddApplication continua compondo todos eles.
    [Fact]
    public void AddApplicationRegistraOsMappingsDeCadaFeature()
    {
        AssertRegistrado<Dante.Domain.EspacosDeConhecimento.EspacoDeConhecimento, Dante.Application.EspacosDeConhecimento.EspacoDeConhecimentoDto>();
        AssertRegistrado<Dante.Application.EspacosDeConhecimento.EspacoDeConhecimentoDto, Dante.Domain.EspacosDeConhecimento.EspacoDeConhecimento>();
        AssertRegistrado<Dante.Domain.Projetos.Projeto, Dante.Application.Projetos.ProjetoDto>();
        AssertRegistrado<Dante.Application.Projetos.ProjetoDto, Dante.Domain.Projetos.Projeto>();
        AssertRegistrado<Dante.Domain.Conhecimentos.Conhecimento, Dante.Application.Conhecimentos.ConhecimentoDto>();
        AssertRegistrado<Dante.Application.Conhecimentos.ConhecimentoDto, Dante.Domain.Conhecimentos.Conhecimento>();
        AssertRegistrado<Dante.Domain.ContextosDeTrabalho.ContextoDeTrabalho, Dante.Application.ContextosDeTrabalho.ContextoDeTrabalhoDto>();
        AssertRegistrado<Dante.Domain.CapturaDeConhecimento.CandidatoDeConhecimento, Dante.Application.CapturaDeConhecimento.CandidatoDeConhecimentoDto>();
        AssertRegistrado<Dante.Domain.RelacoesDeConhecimento.RelacaoDeConhecimento, Dante.Application.RelacoesDeConhecimento.RelacaoDeConhecimentoDto>();
        AssertRegistrado<Dante.Domain.DocumentosFonte.DocumentoFonte, Dante.Application.DocumentosFonte.DocumentoFonteDto>();
    }

    [Fact]
    public void MappingsFicamNaFeatureEAComposicaoNaoConheceDetalhes()
    {
        var mappings = typeof(ConfiguracaoMapeamento).Assembly.GetTypes()
            .Where(t => t.Name.EndsWith("Mapping", StringComparison.Ordinal)).ToArray();
        Assert.Equal(["CandidatoDeConhecimento", "Conhecimento", "ContextoDeTrabalho", "DocumentoFonte",
            "EspacoDeConhecimento", "Projeto", "RelacaoDeConhecimento"], mappings.Select(t => t.Name[..^"Mapping".Length]).Order());
        Assert.All(mappings, t =>
        {
            Assert.True(t.IsAbstract && t.IsSealed && !t.IsPublic, $"{t.Name} deve ser internal static.");
            Assert.NotEqual("Dante.Application.Mapeamento", t.Namespace);
        });
        var raiz = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(raiz, "Dante.sln"))) raiz = Path.GetDirectoryName(raiz)!;
        var composicao = File.ReadAllText(Path.Combine(raiz, "src", "Dante.Application", "Mapeamento", "MapeamentosDaApplication.cs"));
        Assert.DoesNotContain("Dante.Domain", composicao);
        Assert.DoesNotContain("Registrar<", composicao);
    }

    private static void AssertRegistrado<TOrigem, TDestino>()
    {
        using var provider = Criar(config => config.Registrar<TOrigem, TDestino>(_ => default!));
        var erro = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IMapsterTypeAdapter>());
        Assert.Contains("duplicado", erro.Message);
    }

    private static ServiceProvider Criar(Action<ConfiguracaoMapeamento> registrar) =>
        new ServiceCollection().AddApplication().AddMapeamentos(registrar).BuildServiceProvider();

    private sealed class ExemploAppService(IMapsterTypeAdapter mapper)
    {
        public string Consultar(ExemploDto dto) => mapper.Mapear<ExemploDto, string>(dto);
    }
    public sealed class ExemploEntidade
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string Nome { get; }
        public string Segredo { get; }
        public ExemploEntidade(string nome, string segredo)
        {
            if (string.IsNullOrWhiteSpace(nome)) throw new ArgumentException("Nome inválido.");
            Nome = nome; Segredo = segredo;
        }
    }
    public sealed class ExemploDto
    {
        public Guid Id { get; init; }
        public Guid IdTenant { get; init; }
        public string Nome { get; init; } = "";
        public string? Segredo { get; init; }
    }
    public sealed record ExemploSearchDto(string Termo);
}
