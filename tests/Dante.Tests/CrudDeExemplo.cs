using Dante.Application.Comum;
using Dante.Application.Mapeamento;
using Dante.Domain.Comum;

namespace Dante.Tests.CrudDeExemplo;

// Consumidores fictícios do padrão, só para os testes: as entidades reais nascem nas issues do Brain.
public sealed class Projeto : EntidadeBase
{
    public Projeto(string nome) => Renomear(nome);

    public string Nome { get; private set; } = string.Empty;
    public bool Arquivado { get; private set; }

    public void Renomear(string nome)
    {
        if (string.IsNullOrWhiteSpace(nome)) throw new ArgumentException("Nome do projeto é obrigatório.", nameof(nome));
        Nome = nome.Trim();
    }

    public void Arquivar() => Arquivado = true;
}

public sealed record ProjetoDto
{
    public Guid Id { get; init; }
    public string Nome { get; init; } = string.Empty;
}

public sealed record ProjetoSearchDto(string? TrechoDoNome, int Limite = 50);

public interface IProjetoRepository : IRepository<Projeto>
{
    Task<IReadOnlyList<Projeto>> ListarPorTrechoDoNomeAsync(string? trecho, int limite,
        CancellationToken cancellationToken = default);
}

public interface IProjetoAppService : ICrudBasicoAppService<ProjetoDto, ProjetoSearchDto, Projeto>
{
    Task<bool> ArquivarAsync(Guid id, CancellationToken cancellationToken = default);
}

public sealed class ProjetoAppService
    : CrudBasicoAppService<ProjetoDto, ProjetoSearchDto, Projeto>,
      IProjetoAppService
{
    private readonly IProjetoRepository projetos;

    public ProjetoAppService(IProjetoRepository projetos, IUnitOfWork unitOfWork, IMapsterTypeAdapter typeAdapter)
        : base(projetos, unitOfWork, typeAdapter) => this.projetos = projetos;

    public async Task<bool> ArquivarAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var projeto = await projetos.ObterPorIdAsync(id, cancellationToken);
        if (projeto is null) return false;
        projeto.Arquivar();
        projetos.Atualizar(projeto);
        await UnitOfWork.SalvarAlteracoesAsync(cancellationToken);
        return true;
    }

    protected override Task<IReadOnlyList<Projeto>> ConsultarAsync(ProjetoSearchDto filtro,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(filtro.Limite, 1, nameof(filtro.Limite));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(filtro.Limite, 100, nameof(filtro.Limite));
        return projetos.ListarPorTrechoDoNomeAsync(filtro.TrechoDoNome, filtro.Limite, cancellationToken);
    }

    protected override void AplicarAlteracoes(Projeto entidade, ProjetoDto dto) => entidade.Renomear(dto.Nome);
}

internal sealed class ProjetoRepositoryEmMemoria : IProjetoRepository
{
    private readonly Dictionary<Guid, Projeto> projetos = [];

    public List<Projeto> Adicionados { get; } = [];
    public List<Projeto> Atualizados { get; } = [];
    public List<Projeto> Removidos { get; } = [];

    public Projeto Semear(Projeto projeto) => projetos[projeto.Id] = projeto;

    public Task<Projeto?> ObterPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(projetos.GetValueOrDefault(id));

    public Task AdicionarAsync(Projeto entidade, CancellationToken cancellationToken = default)
    {
        Adicionados.Add(Semear(entidade));
        return Task.CompletedTask;
    }

    public void Atualizar(Projeto entidade) => Atualizados.Add(entidade);

    public void Remover(Projeto entidade)
    {
        projetos.Remove(entidade.Id);
        Removidos.Add(entidade);
    }

    public Task<IReadOnlyList<Projeto>> ListarPorTrechoDoNomeAsync(string? trecho, int limite,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Projeto>>(projetos.Values
            .Where(projeto => trecho is null || projeto.Nome.Contains(trecho, StringComparison.OrdinalIgnoreCase))
            .Take(limite).ToArray());
}

internal sealed class UnitOfWorkEmMemoria : IUnitOfWork
{
    public int Salvamentos { get; private set; }

    public Task SalvarAlteracoesAsync(CancellationToken cancellationToken = default)
    {
        Salvamentos++;
        return Task.CompletedTask;
    }
}
