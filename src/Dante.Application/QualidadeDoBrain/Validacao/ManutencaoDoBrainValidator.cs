namespace Dante.Application.QualidadeDoBrain;

internal static class ManutencaoDoBrainValidator
{
    // Só a forma do pedido; existência, escopo e revisão das duplicatas são verificados pelo AppService.
    internal static void ValidarConsolidacao(RevisaoEsperadaDto destino, IReadOnlyList<RevisaoEsperadaDto> duplicatas)
    {
        if (duplicatas.Count is < 1 or > 20 || duplicatas.Select(x => x.IdConhecimento).Distinct().Count() != duplicatas.Count ||
            duplicatas.Any(x => x.IdConhecimento == destino.IdConhecimento))
            throw new ArgumentException("Consolidação exige duplicatas distintas e limite de 20.");
    }
}
