using System.Net.Http;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Dante.Worker.Telegram;

// Diagnóstico do transporte HTTP do Telegram. Registra só tipos, códigos, famílias de endereço e tempos:
// a mensagem de uma exceção HTTP e a URL da requisição carregam o token do bot e nunca vão para o log.
internal static class DiagnosticoHttpDoTelegram
{
    public const string Encerramento = "encerramento do Worker";
    public const string TimeoutDoHttpClient = "timeout do HttpClient";
    public const string OutraOrigem = "outra origem";
    public const string SemCancelamento = "não é cancelamento";

    // O token do chamador vence: um timeout que coincida com o encerramento é encerramento.
    // HttpClient.Timeout chega como TaskCanceledException com TimeoutException interna (.NET 5+).
    public static string OrigemDoCancelamento(Exception exception, CancellationToken tokenDoChamador) =>
        exception is not OperationCanceledException ? SemCancelamento
        : tokenDoChamador.IsCancellationRequested ? Encerramento
        : exception.InnerException is TimeoutException ? TimeoutDoHttpClient
        : OutraOrigem;

    public static string? TipoInterno(Exception exception) => exception.InnerException?.GetType().Name;

    // Códigos enumerados da cadeia de exceções: dizem se falhou DNS, connect, TLS ou a resposta, sem texto livre.
    public static string Detalhe(Exception exception)
    {
        string? httpError = null, socketError = null;
        for (var atual = exception; atual is not null; atual = atual.InnerException)
        {
            if (httpError is null && atual is HttpRequestException { HttpRequestError: var erro } &&
                erro != HttpRequestError.Unknown)
                httpError = erro.ToString();
            if (socketError is null && atual is SocketException socket) socketError = socket.SocketErrorCode.ToString();
        }
        return $"HttpRequestError={httpError ?? "-"}, SocketError={socketError ?? "-"}";
    }

    // Mesmo transporte que o HttpClient padrão usa no Linux (timeouts e demais opções padrão), trocando só o connect
    // pelo Happy Eyeballs do ConexaoTcpDoTelegram, que também registra família conectada, tempo e falhas.
    public static SocketsHttpHandler CriarHandler(ILogger logger)
    {
        var conexao = new ConexaoTcpDoTelegram(logger);
        return new SocketsHttpHandler
        {
            ConnectCallback = (contexto, cancellationToken) => conexao.ConectarAsync(contexto.DnsEndPoint, cancellationToken)
        };
    }
}
