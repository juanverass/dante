using Dante.Application.Comum;

namespace Dante.Application.Projetos;

internal static class ProjetoValidator
{
    internal static void ValidarPesquisa(ProjetoSearchDto filtro)
    {
        ValidacaoDeEntrada.ExigirId(filtro.IdEspacoDeConhecimento, "Espaço de conhecimento é obrigatório na pesquisa de projetos.",
            nameof(filtro));
        ValidacaoDeEntrada.ExigirFaixa(filtro.Limite, 1, ProjetoAppService.LimiteMaximoDaPesquisa, nameof(filtro.Limite));
    }
}
