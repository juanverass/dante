namespace Dante.Application.BuscaDoBrain;

internal static class BuscaDoBrainValidator
{
    internal static void ValidarBusca(BuscaDoBrainSearchDto filtro)
    {
        if (filtro.Limite is < 1 or > 100 || filtro.Deslocamento is < 0 or > 10000 || filtro.Texto.Length > 2000 ||
            filtro.Tipos.Count > 12 || filtro.Tipos.Any(t => !Enum.IsDefined(t)) || filtro.Tags.Count > 10 || filtro.Tags.Any(t => string.IsNullOrWhiteSpace(t) || t.Length > 100) ||
            filtro.IdConhecimento == Guid.Empty || string.IsNullOrWhiteSpace(filtro.Texto) && filtro.IdConhecimento is null ||
            filtro.CriadoDesde >= filtro.CriadoAte || filtro.ValidoEm == default(DateTimeOffset) ||
            filtro.Tipo is { } tipo && !Enum.IsDefined(tipo) || filtro.Status is { } status && !Enum.IsDefined(status) ||
            filtro.Sensibilidade is { } s && !Enum.IsDefined(s)) throw new ArgumentException("Filtro de busca inválido.");
    }
}
