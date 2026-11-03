namespace Dante.Application.CapturaDeConhecimento;

internal static class CandidatoDeConhecimentoValidator
{
    internal static void ValidarFiltros(IReadOnlyList<string>? tags, string? texto)
    {
        if (tags is not null && (tags.Count > 50 || tags.Any(t => string.IsNullOrWhiteSpace(t) || t.Length > 100)) ||
            texto is not null && (string.IsNullOrWhiteSpace(texto) || texto.Length > 2000))
            throw new ArgumentException("Filtro de candidatos inválido.");
    }
}
