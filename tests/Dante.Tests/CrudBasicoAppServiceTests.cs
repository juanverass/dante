using Dante.Application;
using Dante.Application.Comum;
using Dante.Application.EspacosDeConhecimento;
using Dante.Application.Mapeamento;
using Dante.Domain.Comum;
using Dante.Tests.CrudDeExemplo;
using Microsoft.Extensions.DependencyInjection;

namespace Dante.Tests;

public sealed class CrudBasicoAppServiceTests : IDisposable
{
    private readonly ProjetoRepositoryEmMemoria repository = new();
    private readonly UnitOfWorkEmMemoria unitOfWork = new();
    private readonly ServiceProvider provider;
    private readonly IProjetoAppService appService;

    public CrudBasicoAppServiceTests()
    {
        provider = new ServiceCollection().AddApplication().AddMapeamentos(configuracao =>
        {
            configuracao.Registrar<Projeto, ProjetoDto>(projeto => new ProjetoDto { Id = projeto.Id, Nome = projeto.Nome });
            configuracao.Registrar<ProjetoDto, Projeto>(dto => new Projeto(dto.Nome));
        }).BuildServiceProvider();
        appService = new ProjetoAppService(repository, unitOfWork, provider.GetRequiredService<IMapsterTypeAdapter>());
    }

    [Fact]
    public async Task AddCreatesThroughTheDomainWithNewIdentityAndSavesOnce()
    {
        var entrada = new ProjetoDto { Id = Guid.NewGuid(), Nome = "  Dante  " };

        var criado = await appService.AdicionarAsync(entrada);

        Assert.NotEqual(Guid.Empty, criado.Id);
        Assert.NotEqual(entrada.Id, criado.Id);
        Assert.Equal("Dante", criado.Nome);
        Assert.Equal(criado.Id, Assert.Single(repository.Adicionados).Id);
        Assert.Equal(1, unitOfWork.Salvamentos);
    }

    [Fact]
    public async Task AddRefusesInvalidInputBeforeTouchingRepositoryOrUnitOfWork()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => appService.AdicionarAsync(new ProjetoDto { Nome = " " }));
        await Assert.ThrowsAsync<ArgumentNullException>(() => appService.AdicionarAsync(null!));

        Assert.Empty(repository.Adicionados);
        Assert.Equal(0, unitOfWork.Salvamentos);
    }

    [Fact]
    public async Task GetByIdMapsTheEntityOrReturnsNull()
    {
        var projeto = repository.Semear(new Projeto("Dante"));

        Assert.Equal(new ProjetoDto { Id = projeto.Id, Nome = "Dante" }, await appService.ObterPorIdAsync(projeto.Id));
        Assert.Null(await appService.ObterPorIdAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task UpdateCallsDomainMethodsAndKeepsTheIdentityFromTheParameter()
    {
        var projeto = repository.Semear(new Projeto("Antigo"));

        var atualizado = await appService.AtualizarAsync(projeto.Id, new ProjetoDto { Id = Guid.NewGuid(), Nome = "Novo" });

        Assert.Equal(new ProjetoDto { Id = projeto.Id, Nome = "Novo" }, atualizado);
        Assert.Equal("Novo", projeto.Nome);
        Assert.Same(projeto, Assert.Single(repository.Atualizados));
        Assert.Equal(1, unitOfWork.Salvamentos);
    }

    [Fact]
    public async Task UpdateOfMissingOrInvalidEntityDoesNotSave()
    {
        var projeto = repository.Semear(new Projeto("Dante"));

        Assert.Null(await appService.AtualizarAsync(Guid.NewGuid(), new ProjetoDto { Nome = "Novo" }));
        await Assert.ThrowsAsync<ArgumentException>(() => appService.AtualizarAsync(projeto.Id, new ProjetoDto { Nome = "" }));

        Assert.Equal("Dante", projeto.Nome);
        Assert.Empty(repository.Atualizados);
        Assert.Equal(0, unitOfWork.Salvamentos);
    }

    [Fact]
    public async Task RemoveDeletesExistingEntityAndReportsMissingOnes()
    {
        var projeto = repository.Semear(new Projeto("Dante"));

        Assert.False(await appService.RemoverAsync(Guid.NewGuid()));
        Assert.Equal(0, unitOfWork.Salvamentos);
        Assert.True(await appService.RemoverAsync(projeto.Id));

        Assert.Same(projeto, Assert.Single(repository.Removidos));
        Assert.Equal(1, unitOfWork.Salvamentos);
    }

    [Fact]
    public async Task SearchUsesTheSpecificRepositoryQueryWithValidatedFilters()
    {
        repository.Semear(new Projeto("Dante Worker"));
        repository.Semear(new Projeto("Dante WebApi"));
        repository.Semear(new Projeto("Fitness"));

        var encontrados = await appService.PesquisarAsync(new ProjetoSearchDto("dante", 1));

        Assert.Equal("Dante Worker", Assert.Single(encontrados).Nome);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => appService.PesquisarAsync(new ProjetoSearchDto(null, 0)));
        await Assert.ThrowsAsync<ArgumentNullException>(() => appService.PesquisarAsync(null!));
    }

    [Fact]
    public async Task SpecificAppServiceAddsItsOwnOperationOnTopOfTheBase()
    {
        var projeto = repository.Semear(new Projeto("Dante"));

        Assert.True(await appService.ArquivarAsync(projeto.Id));

        Assert.True(projeto.Arquivado);
        Assert.Equal(1, unitOfWork.Salvamentos);
    }

    [Fact]
    public void EntidadeBaseHasProtectedGuidIdentityGeneratedByTheDomain()
    {
        var tipo = typeof(EntidadeBase);
        var id = tipo.GetProperty(nameof(EntidadeBase.Id))!;

        Assert.True(tipo.IsAbstract);
        Assert.False(tipo.IsGenericType);
        Assert.Equal(typeof(Guid), id.PropertyType);
        Assert.True(id.SetMethod!.IsFamily);
        var (a, b) = (new Projeto("a"), new Projeto("b"));
        Assert.NotEqual(Guid.Empty, a.Id);
        Assert.NotEqual(a.Id, b.Id);
    }

    [Fact]
    public void BaseContractsAreGenericOnlyOnTheEntityAndHaveNoTId()
    {
        Type[] bases = [typeof(IRepository<>), typeof(ICrudBasicoAppService<,,>), typeof(CrudBasicoAppService<,,>)];

        Assert.Equal([1, 3, 3], bases.Select(tipo => tipo.GetGenericArguments().Length));
        Assert.All(bases, tipo =>
        {
            Assert.Equal("Dante.Application", tipo.Assembly.GetName().Name);
            var entidade = tipo.GetGenericArguments().Last();
            Assert.Equal("TEntity", entidade.Name);
            Assert.Contains(typeof(EntidadeBase), entidade.GetGenericParameterConstraints());
            Assert.DoesNotContain(tipo.GetGenericArguments(), argumento => argumento.Name == "TId");
        });
        Assert.DoesNotContain(typeof(EntidadeBase).Assembly.GetReferencedAssemblies(),
            assembly => assembly.Name!.StartsWith("Mapster", StringComparison.OrdinalIgnoreCase));
    }

    // Convenção de nomes dos consumidores (AD-36, AD-39): I{Entidade}Repository, I{Entidade}AppService,
    // {Entidade}AppService, {Entidade}Dto e {Entidade}SearchDto. Vale para a Application e para os exemplos.
    [Fact]
    public void ConsumersFollowThePtBrNamingConvention()
    {
        var tipos = new[] { typeof(CrudBasicoAppService<,,>).Assembly, typeof(ProjetoAppService).Assembly }
            .SelectMany(assembly => assembly.GetTypes()).Where(tipo => !tipo.IsGenericTypeDefinition).ToArray();
        var verificados = new List<Type>();
        foreach (var tipo in tipos)
        {
            if (tipo.IsInterface && Fechado(tipo, typeof(IRepository<>)) is { } repositorio)
            {
                Assert.Equal($"I{repositorio[0].Name}Repository", tipo.Name);
                verificados.Add(tipo);
            }
            var crud = tipo.IsInterface ? Fechado(tipo, typeof(ICrudBasicoAppService<,,>))
                : !tipo.IsAbstract ? Fechado(tipo, typeof(CrudBasicoAppService<,,>)) : null;
            if (crud is [var dto, var search, var entidade])
            {
                Assert.Equal(tipo.IsInterface ? $"I{entidade.Name}AppService" : $"{entidade.Name}AppService", tipo.Name);
                Assert.Equal($"{entidade.Name}Dto", dto.Name);
                Assert.Equal($"{entidade.Name}SearchDto", search.Name);
                verificados.Add(tipo);
            }
        }
        Assert.Equal(new[] { typeof(Application.Conhecimentos.ConhecimentoAppService), typeof(EspacoDeConhecimentoAppService), typeof(Application.Conhecimentos.IConhecimentoAppService),
                typeof(Application.CapturaDeConhecimento.ICandidatoDeConhecimentoRepository), typeof(Application.Conhecimentos.IConhecimentoRepository), typeof(IEspacoDeConhecimentoAppService),
                typeof(IEspacoDeConhecimentoRepository), typeof(Application.Projetos.IProjetoAppService),
                typeof(IProjetoAppService), typeof(Application.Projetos.IProjetoRepository), typeof(IProjetoRepository),
                typeof(Application.RelacoesDeConhecimento.IRelacaoDeConhecimentoRepository),
                typeof(Application.Projetos.ProjetoAppService), typeof(ProjetoAppService) }.OrderBy(tipo => tipo.Name, StringComparer.Ordinal).ThenBy(tipo => tipo.Namespace, StringComparer.Ordinal),
            verificados.OrderBy(tipo => tipo.Name, StringComparer.Ordinal)
                .ThenBy(tipo => tipo.Namespace, StringComparer.Ordinal));
    }

    private static Type[]? Fechado(Type tipo, Type generico)
    {
        var candidatos = generico.IsInterface ? tipo.GetInterfaces() : Ancestrais(tipo);
        return candidatos.FirstOrDefault(candidato => candidato.IsGenericType &&
            candidato.GetGenericTypeDefinition() == generico)?.GetGenericArguments();

        static IEnumerable<Type> Ancestrais(Type tipo)
        {
            for (var atual = tipo.BaseType; atual is not null; atual = atual.BaseType) yield return atual;
        }
    }

    public void Dispose() => provider.Dispose();
}
