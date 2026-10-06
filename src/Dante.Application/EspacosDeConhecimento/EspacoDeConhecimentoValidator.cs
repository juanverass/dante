using Dante.Application.Comum;

namespace Dante.Application.EspacosDeConhecimento;

internal static class EspacoDeConhecimentoValidator
{
    internal static void ValidarPesquisa(EspacoDeConhecimentoSearchDto filtro)
    {
        ValidacaoDeEntrada.ExigirId(filtro.IdUsuario, "Proprietário é obrigatório na pesquisa de espaços de conhecimento.",
            nameof(filtro));
        ValidacaoDeEntrada.ExigirFaixa(filtro.Limite, 1, EspacoDeConhecimentoAppService.LimiteMaximoDaPesquisa,
            nameof(filtro.Limite));
    }
}
