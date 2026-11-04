namespace Dante.Application.BuscaDoBrain;

public interface IGeradorDeEmbedding
{
    ModeloEmbedding? Modelo { get; }
    bool Externo { get; }
    // Similaridade cosseno mínima calibrada para este modelo (#280). Sem ela, a busca não usa a parte
    // semântica (fail-closed): um modelo não calibrado nunca injeta resultados fracos.
    double? SimilaridadeMinima { get; }
    Task<float[]?> GerarAsync(string texto, CancellationToken cancellationToken = default);
}
