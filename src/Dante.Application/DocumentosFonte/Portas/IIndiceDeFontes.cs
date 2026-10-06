using Dante.Application.BuscaDoBrain;
using Dante.Application.SegurancaDoBrain;
namespace Dante.Application.DocumentosFonte;

public interface IIndiceDeFontes
{
    Task<IReadOnlyList<ResultadoDaBuscaDto>> BuscarAsync(AcessoAoBrain acesso, BuscaDoBrainSearchDto filtro,
        ModeloEmbedding? modelo, float[]? vetor, CancellationToken cancellationToken = default);
    Task<TrechoDaFonteDto?> ObterTrechoAsync(AcessoAoBrain acesso, Guid id, int revisao, int numero, CancellationToken cancellationToken = default);
    Task ReconstruirAsync(AcessoAoBrain acesso, CancellationToken cancellationToken = default);
    Task<int> ReindexarAsync(AcessoAoBrain acesso, IGeradorDeEmbedding embeddings, int limite, CancellationToken cancellationToken = default);
}
