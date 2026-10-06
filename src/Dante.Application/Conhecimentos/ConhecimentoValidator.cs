using Dante.Application.Comum;

namespace Dante.Application.Conhecimentos;

internal static class ConhecimentoValidator
{
    // A tag é validada já normalizada; o AppService aplica a mesma normalização antes de consultar.
    internal static void ValidarPesquisa(ConhecimentoSearchDto filtro)
    {
        if (filtro.IdEspacoDeConhecimento == Guid.Empty || filtro.IdProjeto == Guid.Empty ||
            (filtro.SomenteSemProjeto && filtro.IdProjeto is not null)) throw new ArgumentException("Escopo de pesquisa inválido.");
        if ((filtro.Tipo is { } tipo && !Enum.IsDefined(tipo)) || (filtro.Status is { } status && !Enum.IsDefined(status)))
            throw new ArgumentException("Classificação de pesquisa inválida.");
        ValidacaoDeEntrada.ExigirFaixa(filtro.Limite, 1, ConhecimentoAppService.LimiteMaximoDaPesquisa, nameof(filtro.Limite));
        if (filtro.ValidoEm == default(DateTimeOffset)) throw new ArgumentException("Instante de validade inválido.");
        var tag = string.IsNullOrWhiteSpace(filtro.Tag) ? null : filtro.Tag.Trim();
        if (tag?.Length > 100 || tag?.Any(char.IsControl) == true) throw new ArgumentException("Tag de pesquisa inválida.");
    }
}
