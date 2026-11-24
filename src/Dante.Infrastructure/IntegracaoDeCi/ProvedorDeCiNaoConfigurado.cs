using Dante.Application.IntegracaoDeCi;

namespace Dante.Infrastructure.IntegracaoDeCi;

// Adapter fail-closed da porta de CI (#236, AD-76) enquanto nenhum provedor real existe: toda consulta responde "não
// configurada", sem rede, credencial nem estado. O primeiro provedor (#237) substitui este registro.
internal sealed class ProvedorDeCiNaoConfigurado : IProvedorDeCi
{
    private const string Mensagem = "Nenhum provedor de CI está configurado.";

    public Task<ResultadoDaConsultaDeCi> ConsultarAsync(ConsultaDeCi consulta, CancellationToken cancellationToken = default) =>
        Task.FromException<ResultadoDaConsultaDeCi>(Falha());

    public Task<ExecucaoDeCiDto> ObterExecucaoAsync(IdentidadeDaExecucaoDeCi execucao,
        CancellationToken cancellationToken = default) => Task.FromException<ExecucaoDeCiDto>(Falha());

    public Task<LogDeJobDeCiDto> ObterLogDoJobAsync(IdentidadeDoJobDeCi job, int limiteEmBytes,
        CancellationToken cancellationToken = default) => Task.FromException<LogDeJobDeCiDto>(Falha());

    private static FalhaNaConsultaDeCiException Falha() => new(MotivoDaFalhaNaConsultaDeCi.NaoConfigurada, Mensagem);
}
