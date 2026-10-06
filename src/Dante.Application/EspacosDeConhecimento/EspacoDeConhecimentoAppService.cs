using Dante.Application.Comum;
using Dante.Application.Mapeamento;
using Dante.Domain.EspacosDeConhecimento;

namespace Dante.Application.EspacosDeConhecimento;

public sealed class EspacoDeConhecimentoAppService
    : CrudBasicoAppService<EspacoDeConhecimentoDto, EspacoDeConhecimentoSearchDto, EspacoDeConhecimento>,
      IEspacoDeConhecimentoAppService
{
    public const int LimiteMaximoDaPesquisa = 100;

    private readonly IEspacoDeConhecimentoRepository espacos;

    public EspacoDeConhecimentoAppService(IEspacoDeConhecimentoRepository espacos, IUnitOfWork unitOfWork,
        IMapsterTypeAdapter typeAdapter) : base(espacos, unitOfWork, typeAdapter) => this.espacos = espacos;

    public Task<bool> ArquivarAsync(Guid id, CancellationToken cancellationToken = default) =>
        AlterarEstadoAsync(id, espaco => espaco.Arquivar(), cancellationToken);

    public Task<bool> ReativarAsync(Guid id, CancellationToken cancellationToken = default) =>
        AlterarEstadoAsync(id, espaco => espaco.Reativar(), cancellationToken);

    protected override Task<IReadOnlyList<EspacoDeConhecimento>> ConsultarAsync(
        EspacoDeConhecimentoSearchDto filtro, CancellationToken cancellationToken)
    {
        EspacoDeConhecimentoValidator.ValidarPesquisa(filtro);
        var trecho = string.IsNullOrWhiteSpace(filtro.TrechoDoNome) ? null : filtro.TrechoDoNome.Trim();
        return espacos.ListarDoUsuarioAsync(filtro.IdUsuario, trecho, filtro.IncluirArquivados, filtro.Limite,
            cancellationToken);
    }

    protected override void AplicarAlteracoes(EspacoDeConhecimento entidade, EspacoDeConhecimentoDto dto) =>
        entidade.Atualizar(dto.Nome, dto.Descricao);

    private async Task<bool> AlterarEstadoAsync(Guid id, Action<EspacoDeConhecimento> alterar,
        CancellationToken cancellationToken)
    {
        var espaco = await espacos.ObterPorIdAsync(id, cancellationToken);
        if (espaco is null) return false;
        alterar(espaco);
        espacos.Atualizar(espaco);
        await UnitOfWork.SalvarAlteracoesAsync(cancellationToken);
        return true;
    }
}
