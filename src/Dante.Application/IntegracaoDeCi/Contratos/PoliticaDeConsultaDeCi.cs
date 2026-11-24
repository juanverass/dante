namespace Dante.Application.IntegracaoDeCi;

// Limites que todo adapter de provedor obedece (AD-76). Só GET idempotente é repetido, com backoff exponencial e
// jitter, em timeout, 5xx e limite de taxa; credencial negada, sem permissão ou recurso ausente nunca é repetido.
// Espera de limite de taxa maior que EsperaMaximaPorLimiteDeTaxa vira FalhaNaConsultaDeCiException(LimiteDeTaxa) com
// RepetirApos, sem prender a chamada. Estado final de uma tentativa é imutável e pode ficar em cache sem expirar;
// estado não final só vale por ValidadeDoCacheNaoFinal. O polling começa no intervalo mínimo e dobra até o máximo
// enquanto nada muda; ele observa o CI e nunca o dirige.
public sealed record PoliticaDeConsultaDeCi
{
    public static PoliticaDeConsultaDeCi Padrao { get; } = new();

    public TimeSpan TempoLimiteDaRequisicao { get; init; } = TimeSpan.FromSeconds(15);
    public int TentativasPorRequisicao { get; init; } = 3;
    public TimeSpan EsperaMaximaPorLimiteDeTaxa { get; init; } = TimeSpan.FromSeconds(60);
    public int ItensPorPagina { get; init; } = 100;
    public int PaginasMaximas { get; init; } = 5;
    public TimeSpan ValidadeDoCacheNaoFinal { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan IntervaloMinimoDePolling { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan IntervaloMaximoDePolling { get; init; } = TimeSpan.FromMinutes(10);
    public int LimiteDoLogEmBytes { get; init; } = 64 * 1024;
    public TimeSpan EsperaSemRunnerAteSinalizar { get; init; } = TimeSpan.FromMinutes(15);

    public PoliticaDeConsultaDeCi Validada() => CiValidator.ExigirPolitica(this);
}
