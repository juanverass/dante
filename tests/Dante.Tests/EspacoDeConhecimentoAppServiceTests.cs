using Dante.Application;
using Dante.Application.EspacosDeConhecimento;
using Dante.Application.Mapeamento;
using Dante.Domain.EspacosDeConhecimento;
using Microsoft.Extensions.DependencyInjection;

namespace Dante.Tests;

public sealed class EspacoDeConhecimentoAppServiceTests : IDisposable
{
    private static readonly Guid Proprietario = Guid.NewGuid();

    private readonly EspacoDeConhecimentoRepositoryEmMemoria repository = new();
    private readonly CrudDeExemplo.UnitOfWorkEmMemoria unitOfWork = new();
    private readonly ServiceProvider provider;
    private readonly IEspacoDeConhecimentoAppService appService;

    // Mappings reais da Application (AddApplication), sem banco: o repository é um fake em memória.
    public EspacoDeConhecimentoAppServiceTests()
    {
        provider = new ServiceCollection().AddApplication().BuildServiceProvider();
        appService = new EspacoDeConhecimentoAppService(repository, unitOfWork,
            provider.GetRequiredService<IMapsterTypeAdapter>());
    }

    [Fact]
    public async Task AddCreatesAnActiveSpaceThroughTheDomainIgnoringIdAndStateFromInput()
    {
        var entrada = new EspacoDeConhecimentoDto
        {
            Id = Guid.NewGuid(), IdUsuario = Proprietario, Nome = " Trabalho ", Descricao = "Empresa", Arquivado = true
        };

        var criado = await appService.AdicionarAsync(entrada);

        Assert.NotEqual(Guid.Empty, criado.Id);
        Assert.NotEqual(entrada.Id, criado.Id);
        Assert.Equal(new EspacoDeConhecimentoDto
        {
            Id = criado.Id, IdUsuario = Proprietario, Nome = "Trabalho", Descricao = "Empresa", Arquivado = false
        }, criado);
        Assert.Equal(criado.Id, Assert.Single(repository.Adicionados).Id);
        Assert.Equal(1, unitOfWork.Salvamentos);
    }

    [Fact]
    public async Task AddRefusesMissingOwnerOrNameWithoutWriting()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            appService.AdicionarAsync(new EspacoDeConhecimentoDto { Nome = "Pessoal" }));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            appService.AdicionarAsync(new EspacoDeConhecimentoDto { IdUsuario = Proprietario, Nome = " " }));

        Assert.Empty(repository.Adicionados);
        Assert.Equal(0, unitOfWork.Salvamentos);
    }

    [Fact]
    public async Task UpdateChangesOnlyPresentationKeepingIdentityOwnerAndState()
    {
        var espaco = repository.Semear(new EspacoDeConhecimento(Proprietario, "Estudos"));

        var atualizado = await appService.AtualizarAsync(espaco.Id, new EspacoDeConhecimentoDto
        {
            Id = Guid.NewGuid(), IdUsuario = Guid.NewGuid(), Nome = "Faculdade", Descricao = "Disciplinas",
            Arquivado = true
        });

        Assert.Equal(new EspacoDeConhecimentoDto
        {
            Id = espaco.Id, IdUsuario = Proprietario, Nome = "Faculdade", Descricao = "Disciplinas", Arquivado = false
        }, atualizado);
        Assert.Same(espaco, Assert.Single(repository.Atualizados));
        Assert.Equal(1, unitOfWork.Salvamentos);
    }

    [Fact]
    public async Task ArchivedSpaceRefusesUpdateWithoutWriting()
    {
        var espaco = new EspacoDeConhecimento(Proprietario, "Estudos");
        espaco.Arquivar();
        repository.Semear(espaco);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            appService.AtualizarAsync(espaco.Id, new EspacoDeConhecimentoDto { Nome = "Outro" }));

        Assert.Equal("Estudos", espaco.Nome);
        Assert.Empty(repository.Atualizados);
        Assert.Equal(0, unitOfWork.Salvamentos);
    }

    [Fact]
    public async Task ArchiveAndReactivateGoThroughTheDomainAndSaveOnce()
    {
        var espaco = repository.Semear(new EspacoDeConhecimento(Proprietario, "Trabalho"));

        Assert.True(await appService.ArquivarAsync(espaco.Id));
        Assert.True(espaco.Arquivado);
        Assert.True((await appService.ObterPorIdAsync(espaco.Id))!.Arquivado);
        Assert.True(await appService.ReativarAsync(espaco.Id));

        Assert.False(espaco.Arquivado);
        Assert.Equal(2, repository.Atualizados.Count);
        Assert.Equal(2, unitOfWork.Salvamentos);
    }

    [Fact]
    public async Task ArchiveAndReactivateReportMissingSpacesAndRefuseRepeatedTransitions()
    {
        var espaco = repository.Semear(new EspacoDeConhecimento(Proprietario, "Trabalho"));

        Assert.False(await appService.ArquivarAsync(Guid.NewGuid()));
        Assert.False(await appService.ReativarAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => appService.ReativarAsync(espaco.Id));

        Assert.Empty(repository.Atualizados);
        Assert.Equal(0, unitOfWork.Salvamentos);
    }

    [Fact]
    public async Task SearchIsScopedToTheOwnerAndHidesArchivedSpacesByDefault()
    {
        repository.Semear(new EspacoDeConhecimento(Proprietario, "Pessoal"));
        var arquivado = repository.Semear(new EspacoDeConhecimento(Proprietario, "Trabalho antigo"));
        arquivado.Arquivar();
        repository.Semear(new EspacoDeConhecimento(Proprietario, "Trabalho atual"));
        repository.Semear(new EspacoDeConhecimento(Guid.NewGuid(), "Trabalho de outro usuário"));

        var ativos = await appService.PesquisarAsync(new EspacoDeConhecimentoSearchDto(Proprietario, " trabalho "));
        var todos = await appService.PesquisarAsync(
            new EspacoDeConhecimentoSearchDto(Proprietario, "TRABALHO", IncluirArquivados: true));

        Assert.Equal("Trabalho atual", Assert.Single(ativos).Nome);
        Assert.Equal(["Trabalho antigo", "Trabalho atual"], todos.Select(espaco => espaco.Nome).Order());
        Assert.All(todos, espaco => Assert.Equal(Proprietario, espaco.IdUsuario));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(EspacoDeConhecimentoAppService.LimiteMaximoDaPesquisa + 1)]
    public async Task SearchRefusesLimitsOutOfRange(int limite) =>
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            appService.PesquisarAsync(new EspacoDeConhecimentoSearchDto(Proprietario, Limite: limite)));

    [Fact]
    public async Task SearchWithoutOwnerIsRefusedInsteadOfBecomingGlobal()
    {
        repository.Semear(new EspacoDeConhecimento(Proprietario, "Pessoal"));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            appService.PesquisarAsync(new EspacoDeConhecimentoSearchDto(Guid.Empty)));
        Assert.Equal(0, repository.Consultas);
    }

    public void Dispose() => provider.Dispose();

    private sealed class EspacoDeConhecimentoRepositoryEmMemoria : IEspacoDeConhecimentoRepository
    {
        private readonly Dictionary<Guid, EspacoDeConhecimento> espacos = [];

        public List<EspacoDeConhecimento> Adicionados { get; } = [];
        public List<EspacoDeConhecimento> Atualizados { get; } = [];
        public int Consultas { get; private set; }

        public EspacoDeConhecimento Semear(EspacoDeConhecimento espaco) => espacos[espaco.Id] = espaco;

        public Task<EspacoDeConhecimento?> ObterPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(espacos.GetValueOrDefault(id));

        public Task AdicionarAsync(EspacoDeConhecimento entidade, CancellationToken cancellationToken = default)
        {
            Adicionados.Add(Semear(entidade));
            return Task.CompletedTask;
        }

        public void Atualizar(EspacoDeConhecimento entidade) => Atualizados.Add(entidade);

        public void Remover(EspacoDeConhecimento entidade) => espacos.Remove(entidade.Id);

        public Task<IReadOnlyList<EspacoDeConhecimento>> ListarDoUsuarioAsync(Guid idUsuario, string? trechoDoNome,
            bool incluirArquivados, int limite, CancellationToken cancellationToken = default)
        {
            Consultas++;
            return Task.FromResult<IReadOnlyList<EspacoDeConhecimento>>(espacos.Values
                .Where(espaco => espaco.IdUsuario == idUsuario && (incluirArquivados || !espaco.Arquivado))
                .Where(espaco => trechoDoNome is null ||
                    espaco.Nome.Contains(trechoDoNome, StringComparison.OrdinalIgnoreCase))
                .Take(limite).ToArray());
        }
    }
}
