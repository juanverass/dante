using Dante.Application.BuscaDoBrain;
using Dante.Domain.Conhecimentos;
using Dante.Domain.CapturaDeConhecimento;
using Dante.Domain.DocumentosFonte;

namespace Dante.Application.ConstrucaoDeContexto;

internal static class ElegibilidadeDeContexto
{
    internal static bool AtendeFiltros(CandidatoDeConhecimento c, BuscaDoBrainSearchDto f) => !(
        f.Status is not null || f.NaturezaDeOrigem is { } n && c.Natureza != n || f.Tipo is { } t && c.Tipo != t ||
        f.Tipos.Count > 0 && !f.Tipos.Contains(c.Tipo) || f.Sensibilidade is { } s && c.Sensibilidade != s ||
        f.Tags.Any(tag => !c.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) ||
        f.CriadoDesde is { } desde && c.CriadoEm < desde || f.CriadoAte is { } ate && c.CriadoEm >= ate);
    // Mesma separação de fontes brutas adotada pelo índice de fontes: filtros de conhecimento não as promovem.
    internal static bool AtendeFiltros(DocumentoFonte d, BuscaDoBrainSearchDto f) => !(
        f.Status is not null || f.Tipo is not null || f.Tipos.Count > 0 || f.NaturezaDeOrigem is not null || f.Tags.Count > 0 ||
        f.Sensibilidade is { } s && d.Sensibilidade != s ||
        f.CriadoDesde is { } desde && d.AtualizadoEm < desde || f.CriadoAte is { } ate && d.AtualizadoEm >= ate);
    internal static bool AtendeFiltros(Conhecimento k, BuscaDoBrainSearchDto filtros) => !(
filtros.NaturezaDeOrigem is { } natureza && k.NaturezaDeOrigem!=natureza || filtros.Tipo is not null && k.Tipo!=filtros.Tipo || filtros.Tipos.Count>0 && !filtros.Tipos.Contains(k.Tipo) || filtros.Status is not null && k.Status!=filtros.Status ||
                filtros.Sensibilidade is not null && k.Sensibilidade!=filtros.Sensibilidade ||
                filtros.Tags.Any(t=>!k.Tags.Contains(t,StringComparer.OrdinalIgnoreCase)) ||
                filtros.ValidoEm is { } validoEm && !k.EstaValidoEm(validoEm) ||
                filtros.CriadoDesde is { } desde && k.CriadoEm<desde || filtros.CriadoAte is { } ate && k.CriadoEm>=ate);
}
