using Dante.Application.Comum;
using Dante.Application.Contextos;
using Dante.Application.EspacosDeConhecimento;
using Dante.Application.Mapeamento;
using Dante.Domain.Projetos;

namespace Dante.Application.Projetos;

public sealed class ProjetoAppService
    : CrudBasicoAppService<ProjetoDto, ProjetoSearchDto, Projeto>,
      IProjetoAppService
{
    public const int LimiteMaximoDaPesquisa = 100;

    private readonly IProjetoRepository projetos;
    private readonly IEspacoDeConhecimentoRepository espacos;
    private readonly ICatalogoDeRepositorios repositorios;

    public ProjetoAppService(IProjetoRepository projetos, IEspacoDeConhecimentoRepository espacos,
        ICatalogoDeRepositorios repositorios, IUnitOfWork unitOfWork, IMapsterTypeAdapter typeAdapter)
        : base(projetos, unitOfWork, typeAdapter)
    {
        ArgumentNullException.ThrowIfNull(espacos);
        ArgumentNullException.ThrowIfNull(repositorios);
        this.projetos = projetos;
        this.espacos = espacos;
        this.repositorios = repositorios;
    }

    // Projeto só nasce em espaço existente e ativo: espaço arquivado é somente leitura (AD-40).
    public override async Task<ProjetoDto> AdicionarAsync(ProjetoDto dto, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dto);
        var espaco = await espacos.ObterPorIdAsync(dto.IdEspacoDeConhecimento, cancellationToken) ??
            throw new ArgumentException("Espaço de conhecimento do projeto não encontrado.", nameof(dto));
        if (espaco.Arquivado)
            throw new InvalidOperationException("Espaço de conhecimento arquivado não recebe projetos novos.");
        return await base.AdicionarAsync(dto, cancellationToken);
    }

    // O alias gravado é o do catálogo; o repositório cadastrado não passa a identificar o projeto.
    public Task<bool> AssociarRepositorioAsync(Guid id, string aliasDoRepositorio,
        CancellationToken cancellationToken = default) =>
        AlterarAsync(id, projeto =>
        {
            var cadastrado = repositorios.Obter(aliasDoRepositorio) ??
                throw new ArgumentException("Repositório não cadastrado.", nameof(aliasDoRepositorio));
            projeto.AssociarRepositorio(cadastrado.Alias);
        }, cancellationToken);

    public Task<bool> DesassociarRepositorioAsync(Guid id, CancellationToken cancellationToken = default) =>
        AlterarAsync(id, projeto => projeto.DesassociarRepositorio(), cancellationToken);

    public Task<bool> ArquivarAsync(Guid id, CancellationToken cancellationToken = default) =>
        AlterarAsync(id, projeto => projeto.Arquivar(), cancellationToken);

    public Task<bool> ReativarAsync(Guid id, CancellationToken cancellationToken = default) =>
        AlterarAsync(id, projeto => projeto.Reativar(), cancellationToken);

    protected override Task<IReadOnlyList<Projeto>> ConsultarAsync(ProjetoSearchDto filtro,
        CancellationToken cancellationToken)
    {
        ProjetoValidator.ValidarPesquisa(filtro);
        var trecho = string.IsNullOrWhiteSpace(filtro.TrechoDoNome) ? null : filtro.TrechoDoNome.Trim();
        return projetos.ListarDoEspacoAsync(filtro.IdEspacoDeConhecimento, trecho, filtro.IncluirArquivados,
            filtro.Limite, cancellationToken);
    }

    protected override void AplicarAlteracoes(Projeto entidade, ProjetoDto dto) =>
        entidade.Atualizar(dto.Nome, dto.Descricao);

    private async Task<bool> AlterarAsync(Guid id, Action<Projeto> alterar, CancellationToken cancellationToken)
    {
        var projeto = await projetos.ObterPorIdAsync(id, cancellationToken);
        if (projeto is null) return false;
        alterar(projeto);
        projetos.Atualizar(projeto);
        await UnitOfWork.SalvarAlteracoesAsync(cancellationToken);
        return true;
    }
}
