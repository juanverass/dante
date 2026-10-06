namespace Dante.Application.MetricasDoBrain;

// Métricas locais da continuidade (#148): contagens, estimativas e IDs de escopo/sessão, nunca conteúdo.
// Campo nulo é dado indisponível ou não avaliado, nunca zero presumido.
public interface IRegistroDeMetricasDoBrain
{
    Task RegistrarAsync(MetricaDoBrainDto metrica, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MetricaDoBrainDto>> ListarAsync(Guid idTenant, Guid idUsuario, Guid idEspaco, Guid? idProjeto,
        CancellationToken cancellationToken = default);
}
