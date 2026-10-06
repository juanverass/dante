using Dante.Application.BuscaDoBrain;
using Dante.Domain.Conhecimentos;

namespace Dante.Application.ConstrucaoDeContexto;

internal static class ElegibilidadeDeContexto
{
    internal static bool AtendeFiltros(Conhecimento k, BuscaDoBrainSearchDto filtros) => !(
filtros.Tipo is not null && k.Tipo!=filtros.Tipo || filtros.Tipos.Count>0 && !filtros.Tipos.Contains(k.Tipo) || filtros.Status is not null && k.Status!=filtros.Status ||
                filtros.Sensibilidade is not null && k.Sensibilidade!=filtros.Sensibilidade ||
                filtros.Tags.Any(t=>!k.Tags.Contains(t,StringComparer.OrdinalIgnoreCase)) ||
                filtros.ValidoEm is { } validoEm && !k.EstaValidoEm(validoEm) ||
                filtros.CriadoDesde is { } desde && k.CriadoEm<desde || filtros.CriadoAte is { } ate && k.CriadoEm>=ate);
}
