using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using Dante.Application.IntegracaoDeCi;

namespace Dante.Infrastructure.IntegracaoDeCi;

// Transporte somente leitura da API REST do GitHub (#237, AD-76). Só existe GET: o tipo não sabe montar outro método.
// Cada requisição tem o tempo limite da política; timeout, falha de rede, 5xx e limite de taxa são repetidos com
// backoff exponencial e jitter até o número de tentativas; o 403 de limite de taxa (cota zerada ou Retry-After) é
// separado do 403 de permissão, que nunca é repetido. Espera de limite de taxa acima do máximo da política vira
// LimiteDeTaxa com RepetirApos, sem prender a chamada. Redirects não são seguidos automaticamente: quem chama decide,
// e o destino de um redirect nunca recebe a credencial. Mensagens de falha não trazem URL, cabeçalho nem corpo.
internal sealed class ClienteDaApiDoGitHub(HttpClient http, PoliticaDeConsultaDeCi politica, TimeProvider relogio,
    Func<TimeSpan, CancellationToken, Task>? esperar = null, Func<double>? sortear = null)
{
    internal static readonly Uri Base = new("https://api.github.com/");
    internal const string VersaoDaApi = "2022-11-28";
    private static readonly TimeSpan EsperaBase = TimeSpan.FromSeconds(1);
    // Limite de taxa secundário sem cabeçalho: a documentação do provedor manda esperar ao menos um minuto.
    private static readonly TimeSpan EsperaSemCabecalho = TimeSpan.FromMinutes(1);

    private readonly PoliticaDeConsultaDeCi politica = politica.Validada();
    private readonly Func<TimeSpan, CancellationToken, Task> esperar = esperar ?? ((espera, token) => Task.Delay(espera, token));
    private readonly Func<double> sortear = sortear ?? Random.Shared.NextDouble;

    internal PoliticaDeConsultaDeCi Politica => politica;

    // GET com repetição. `ler` roda dentro do tempo limite da mesma tentativa e recebe toda resposta que não é
    // transitória (2xx, 3xx e 4xx que não são limite de taxa); falha ao ler o corpo conta como transitória. credencial
    // nula manda a requisição sem Authorization (destino de redirect de log).
    internal async Task<T> ObterAsync<T>(Uri endereco, string? credencial,
        Func<HttpResponseMessage, CancellationToken, Task<T>> ler, CancellationToken cancellationToken)
    {
        if (endereco.Scheme != Uri.UriSchemeHttps)
            throw new FalhaNaConsultaDeCiException(MotivoDaFalhaNaConsultaDeCi.Inconsistente,
                "O provedor de CI indicou um endereço sem https; a leitura foi recusada.");

        for (var tentativa = 1; ; tentativa++)
        {
            var ultima = tentativa >= politica.TentativasPorRequisicao;
            TimeSpan? espera;
            using (var prazo = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                prazo.CancelAfter(politica.TempoLimiteDaRequisicao);
                try
                {
                    using var requisicao = Requisicao(endereco, credencial);
                    using var resposta = await http.SendAsync(requisicao, HttpCompletionOption.ResponseHeadersRead, prazo.Token);
                    var codigo = (int)resposta.StatusCode;
                    if (LimiteDeTaxa(resposta) is { } aguardar)
                    {
                        if (ultima || aguardar > politica.EsperaMaximaPorLimiteDeTaxa)
                            throw new FalhaNaConsultaDeCiException(MotivoDaFalhaNaConsultaDeCi.LimiteDeTaxa,
                                "O provedor de CI limitou a taxa de consultas; tente de novo mais tarde.", aguardar);
                        espera = aguardar;
                    }
                    else if (codigo >= 500)
                    {
                        if (ultima)
                            throw new FalhaNaConsultaDeCiException(MotivoDaFalhaNaConsultaDeCi.Indisponivel,
                                "O provedor de CI está indisponível no momento.", RetryAfter(resposta));
                        espera = Backoff(tentativa);
                    }
                    else return await ler(resposta, prazo.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    if (ultima)
                        throw new FalhaNaConsultaDeCiException(MotivoDaFalhaNaConsultaDeCi.Indisponivel,
                            "O provedor de CI não respondeu dentro do tempo limite.");
                    espera = Backoff(tentativa);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException)
                {
                    if (ultima)
                        throw new FalhaNaConsultaDeCiException(MotivoDaFalhaNaConsultaDeCi.Indisponivel,
                            "Não foi possível falar com o provedor de CI.");
                    espera = Backoff(tentativa);
                }
            }

            await esperar(espera.Value, cancellationToken);
        }
    }

    private static HttpRequestMessage Requisicao(Uri endereco, string? credencial)
    {
        var requisicao = new HttpRequestMessage(HttpMethod.Get, endereco);
        requisicao.Headers.UserAgent.Add(new ProductInfoHeaderValue("dante-ci", "1"));
        if (credencial is null) return requisicao;
        requisicao.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        requisicao.Headers.Add("X-GitHub-Api-Version", VersaoDaApi);
        requisicao.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credencial);
        return requisicao;
    }

    // 429 sempre; 403 só com cota zerada ou Retry-After (limite secundário). 403 sem esses sinais é permissão.
    private TimeSpan? LimiteDeTaxa(HttpResponseMessage resposta)
    {
        var cotaZerada = Cabecalho(resposta, "x-ratelimit-remaining") == "0";
        var retryAfter = RetryAfter(resposta);
        if (resposta.StatusCode != HttpStatusCode.TooManyRequests
            && !(resposta.StatusCode == HttpStatusCode.Forbidden && (cotaZerada || retryAfter is not null)))
            return null;
        if (retryAfter is { } espera) return espera;
        if (cotaZerada && long.TryParse(Cabecalho(resposta, "x-ratelimit-reset"), NumberStyles.None,
                CultureInfo.InvariantCulture, out var reinicio))
        {
            var ate = DateTimeOffset.FromUnixTimeSeconds(reinicio) - relogio.GetUtcNow();
            return ate > TimeSpan.Zero ? ate : TimeSpan.Zero;
        }

        return EsperaSemCabecalho;
    }

    private TimeSpan? RetryAfter(HttpResponseMessage resposta) =>
        resposta.Headers.RetryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } data } => data - relogio.GetUtcNow() is var ate && ate > TimeSpan.Zero ? ate : TimeSpan.Zero,
            _ => null
        };

    // 1 s, 2 s, 4 s... mais jitter de até 100% da base, para tentativas concorrentes não baterem juntas.
    private TimeSpan Backoff(int tentativa)
    {
        var baseDaEspera = EsperaBase * Math.Pow(2, tentativa - 1);
        return baseDaEspera + baseDaEspera * sortear();
    }

    private static string? Cabecalho(HttpResponseMessage resposta, string nome) =>
        resposta.Headers.TryGetValues(nome, out var valores) ? valores.FirstOrDefault()?.Trim() : null;
}
