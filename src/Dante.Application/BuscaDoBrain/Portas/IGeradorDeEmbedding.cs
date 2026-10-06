namespace Dante.Application.BuscaDoBrain;

public interface IGeradorDeEmbedding
{
    ModeloEmbedding? Modelo { get; }
    bool Externo { get; }
    Task<float[]?> GerarAsync(string texto, CancellationToken cancellationToken = default);
}
