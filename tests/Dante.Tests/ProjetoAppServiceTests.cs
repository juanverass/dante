using Dante.Application;
using Dante.Application.EspacosDeConhecimento;
using Dante.Application.Mapeamento;
using Dante.Application.Projetos;
using Dante.Domain.EspacosDeConhecimento;
using Dante.Domain.Projetos;
using Microsoft.Extensions.DependencyInjection;

namespace Dante.Tests;

public sealed class ProjetoAppServiceTests : IDisposable
{
    private readonly ProjetoRepositoryEmMemoria projetos = new();
    private readonly EspacosEmMemoria espacos = new();
    private readonly CatalogoEmMemoria catalogo = new("@dante", "@Fitness");
    private readonly CrudDeExemplo.UnitOfWorkEmMemoria unitOfWork = new();
    private readonly ServiceProvider provider;
    private readonly IProjetoAppService appService;
    private readonly EspacoDeConhecimento trabalho = new(Guid.NewGuid(), "Trabalho");

    // Mappings reais da Application (AddApplication), sem banco: repositories e catálogo são fakes em memória.
    public ProjetoAppServiceTests()
    {
        espacos.Semear(trabalho);
        provider = new ServiceCollection().AddApplication().BuildServiceProvider();
        appService = new ProjetoAppService(projetos, espacos, catalogo, unitOfWork,
            provider.GetRequiredService<IMapsterTypeAdapter>());
    }

    [Fact]
    public async Task AddCreatesAnActiveProjectWithoutRepositoryIgnoringIdStateAndAliasFromInput()
    {
        var entrada = new ProjetoDto
        {
            Id = Guid.NewGuid(), IdEspacoDeConhecimento = trabalho.Id, Nome = " Dante ", Descricao = "Agentes",
            AliasDoRepositorio = "@dante", Arquivado = true
        };

        var criado = await appService.AdicionarAsync(entrada);

        Assert.NotEqual(entrada.Id, criado.Id);
        Assert.Equal(new ProjetoDto
        {
            Id = criado.Id, IdEspacoDeConhecimento = trabalho.Id, Nome = "Dante", Descricao = "Agentes",
            AliasDoRepositorio = null, Arquivado = false
        }, criado);
        Assert.Equal(criado.Id, Assert.Single(projetos.Adicionados).Id);
        Assert.Equal(1, unitOfWork.Salvamentos);
    }

    [Fact]
    public async Task AddRequiresAnExistingActiveSpaceAndWritesNothingOtherwise()
    {
        var arquivado = espacos.Semear(new EspacoDeConhecimento(Guid.NewGuid(), "Antigo"));
        arquivado.Arquivar();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            appService.AdicionarAsync(new ProjetoDto { IdEspacoDeConhecimento = Guid.NewGuid(), Nome = "Dante" }));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            appService.AdicionarAsync(new ProjetoDto { Nome = "Dante" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            appService.AdicionarAsync(new ProjetoDto { IdEspacoDeConhecimento = arquivado.Id, Nome = "Dante" }));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            appService.AdicionarAsync(new ProjetoDto { IdEspacoDeConhecimento = trabalho.Id, Nome = " " }));

        Assert.Empty(projetos.Adicionados);
        Assert.Equal(0, unitOfWork.Salvamentos);
    }

    [Fact]
    public async Task UpdateChangesOnlyPresentationKeepingSpaceRepositoryAndState()
    {
        var projeto = projetos.Semear(new Projeto(trabalho.Id, "Dante"));
        projeto.AssociarRepositorio("@dante");

        var atualizado = await appService.AtualizarAsync(projeto.Id, new ProjetoDto
        {
            Id = Guid.NewGuid(), IdEspacoDeConhecimento = Guid.NewGuid(), Nome = "D.A.N.T.E.", Descricao = "Brain",
            AliasDoRepositorio = null, Arquivado = true
        });

        Assert.Equal(new ProjetoDto
        {
            Id = projeto.Id, IdEspacoDeConhecimento = trabalho.Id, Nome = "D.A.N.T.E.", Descricao = "Brain",
            AliasDoRepositorio = "@dante", Arquivado = false
        }, atualizado);
        Assert.Equal(1, unitOfWork.Salvamentos);
    }

    [Fact]
    public async Task AssociatingUsesTheCatalogAliasAndRefusesUnknownOrInvalidRepositories()
    {
        var projeto = projetos.Semear(new Projeto(trabalho.Id, "Fitness"));

        Assert.True(await appService.AssociarRepositorioAsync(projeto.Id, "@fitness"));
        Assert.Equal("@Fitness", projeto.AliasDoRepositorio);
        Assert.Equal(1, unitOfWork.Salvamentos);

        await Assert.ThrowsAsync<ArgumentException>(() => appService.AssociarRepositorioAsync(projeto.Id, "@desconhecido"));
        await Assert.ThrowsAsync<ArgumentException>(() => appService.AssociarRepositorioAsync(projeto.Id, "sem-arroba"));
        Assert.Equal("@Fitness", projeto.AliasDoRepositorio);
        Assert.Equal(1, unitOfWork.Salvamentos);

        Assert.True(await appService.DesassociarRepositorioAsync(projeto.Id));
        Assert.Null(projeto.AliasDoRepositorio);
        Assert.Equal(2, unitOfWork.Salvamentos);
    }

    [Fact]
    public async Task SpecificOperationsReportMissingProjectsWithoutWriting()
    {
        var ausente = Guid.NewGuid();

        Assert.False(await appService.AssociarRepositorioAsync(ausente, "@dante"));
        Assert.False(await appService.DesassociarRepositorioAsync(ausente));
        Assert.False(await appService.ArquivarAsync(ausente));
        Assert.False(await appService.ReativarAsync(ausente));

        Assert.Empty(projetos.Atualizados);
        Assert.Equal(0, unitOfWork.Salvamentos);
    }

    [Fact]
    public async Task ArchivedProjectRefusesChangesUntilReactivated()
    {
        var projeto = projetos.Semear(new Projeto(trabalho.Id, "Dante"));

        Assert.True(await appService.ArquivarAsync(projeto.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => appService.AssociarRepositorioAsync(projeto.Id, "@dante"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            appService.AtualizarAsync(projeto.Id, new ProjetoDto { Nome = "Outro" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => appService.ArquivarAsync(projeto.Id));
        Assert.Equal(1, unitOfWork.Salvamentos);

        Assert.True(await appService.ReativarAsync(projeto.Id));
        Assert.False(projeto.Arquivado);
        Assert.Equal(2, unitOfWork.Salvamentos);
    }

    [Fact]
    public async Task SearchIsScopedToTheSpaceAndHidesArchivedProjectsByDefault()
    {
        var outroEspaco = Guid.NewGuid();
        projetos.Semear(new Projeto(trabalho.Id, "Dante Worker"));
        projetos.Semear(new Projeto(trabalho.Id, "Dante WebApi")).Arquivar();
        projetos.Semear(new Projeto(trabalho.Id, "Fitness"));
        projetos.Semear(new Projeto(outroEspaco, "Dante de outro espaço"));

        var ativos = await appService.PesquisarAsync(new ProjetoSearchDto(trabalho.Id, " dante "));
        var todos = await appService.PesquisarAsync(new ProjetoSearchDto(trabalho.Id, "DANTE", IncluirArquivados: true));

        Assert.Equal("Dante Worker", Assert.Single(ativos).Nome);
        Assert.Equal(["Dante WebApi", "Dante Worker"], todos.Select(projeto => projeto.Nome).Order());
        Assert.All(todos, projeto => Assert.Equal(trabalho.Id, projeto.IdEspacoDeConhecimento));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(ProjetoAppService.LimiteMaximoDaPesquisa + 1)]
    public async Task SearchRefusesLimitsOutOfRange(int limite) =>
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            appService.PesquisarAsync(new ProjetoSearchDto(trabalho.Id, Limite: limite)));

    [Fact]
    public async Task SearchWithoutSpaceIsRefusedInsteadOfBecomingGlobal()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => appService.PesquisarAsync(new ProjetoSearchDto(Guid.Empty)));
        Assert.Equal(0, projetos.Consultas);
    }

    public void Dispose() => provider.Dispose();

    private sealed class ProjetoRepositoryEmMemoria : IProjetoRepository
    {
        private readonly Dictionary<Guid, Projeto> itens = [];

        public List<Projeto> Adicionados { get; } = [];
        public List<Projeto> Atualizados { get; } = [];
        public int Consultas { get; private set; }

        public Projeto Semear(Projeto projeto) => itens[projeto.Id] = projeto;

        public Task<Projeto?> ObterPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(itens.GetValueOrDefault(id));

        public Task AdicionarAsync(Projeto entidade, CancellationToken cancellationToken = default)
        {
            Adicionados.Add(Semear(entidade));
            return Task.CompletedTask;
        }

        public void Atualizar(Projeto entidade) => Atualizados.Add(entidade);

        public void Remover(Projeto entidade) => itens.Remove(entidade.Id);

        public Task<IReadOnlyList<Projeto>> ListarDoEspacoAsync(Guid idEspacoDeConhecimento, string? trechoDoNome,
            bool incluirArquivados, int limite, CancellationToken cancellationToken = default)
        {
            Consultas++;
            return Task.FromResult<IReadOnlyList<Projeto>>(itens.Values
                .Where(projeto => projeto.IdEspacoDeConhecimento == idEspacoDeConhecimento &&
                    (incluirArquivados || !projeto.Arquivado))
                .Where(projeto => trechoDoNome is null ||
                    projeto.Nome.Contains(trechoDoNome, StringComparison.OrdinalIgnoreCase))
                .Take(limite).ToArray());
        }
    }

    private sealed class EspacosEmMemoria : IEspacoDeConhecimentoRepository
    {
        private readonly Dictionary<Guid, EspacoDeConhecimento> itens = [];

        public EspacoDeConhecimento Semear(EspacoDeConhecimento espaco) => itens[espaco.Id] = espaco;

        public Task<EspacoDeConhecimento?> ObterPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(itens.GetValueOrDefault(id));

        public Task AdicionarAsync(EspacoDeConhecimento entidade, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void Atualizar(EspacoDeConhecimento entidade) => throw new NotSupportedException();

        public void Remover(EspacoDeConhecimento entidade) => throw new NotSupportedException();

        public Task<IReadOnlyList<EspacoDeConhecimento>> ListarDoUsuarioAsync(Guid idUsuario, string? trechoDoNome,
            bool incluirArquivados, int limite, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    // Como o catálogo real: alias sem "@" é inválido, alias desconhecido é null e a busca ignora maiúsculas.
    private sealed class CatalogoEmMemoria(params string[] aliases) : ICatalogoDeRepositorios
    {
        public IReadOnlyList<RepositorioCadastrado> Listar() =>
            aliases.Select(alias => new RepositorioCadastrado(alias, "/repos/" + alias.TrimStart('@'))).ToArray();

        public RepositorioCadastrado? Obter(string alias)
        {
            if (!alias.StartsWith('@')) throw new ArgumentException("Alias inválido.", nameof(alias));
            return Listar().FirstOrDefault(repositorio =>
                string.Equals(repositorio.Alias, alias, StringComparison.OrdinalIgnoreCase));
        }

        public ResolvedRepositoryEnvironment ResolverAmbiente(string alias) => throw new NotSupportedException();
    }
}
