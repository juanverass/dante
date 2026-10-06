using Dante.Application.SegurancaDoBrain;
namespace Dante.Application.BuscaDoBrain;

public interface IIndiceDeBusca
{
    Task<bool> VetoresDisponiveisAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MatchDaBusca>> BuscarAsync(AcessoAoBrain acesso, BuscaDoBrainSearchDto filtro,
        ModeloEmbedding? modelo, float[]? vetor, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Guid>> ListarPendentesAsync(AcessoAoBrain acesso, ModeloEmbedding modelo, int limite,
        CancellationToken cancellationToken = default);
    Task<bool> GravarAsync(Guid id, int revisao, ModeloEmbedding modelo, float[] vetor, CancellationToken cancellationToken = default);
    Task LimparModeloAsync(AcessoAoBrain acesso, ModeloEmbedding modelo, CancellationToken cancellationToken = default);
}
