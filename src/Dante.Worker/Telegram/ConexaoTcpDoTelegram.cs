using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Dante.Worker.Telegram;

// Conexão TCP do cliente do Telegram com Happy Eyeballs (RFC 8305, sem a corrida de DNS): endereços intercalados por
// família na ordem do DNS, nova tentativa a cada AtrasoEntreTentativas ou assim que a anterior falha, vence a primeira
// que conectar. O connect padrão do .NET tenta um endereço por vez e espera o timeout TCP do kernel (~127 s no Linux),
// mais que o HttpClient.Timeout: um IPv6 que não responde ao SYN impedia o IPv4 de ser tentado.
internal sealed class ConexaoTcpDoTelegram(
    ILogger logger,
    Func<string, CancellationToken, Task<IPAddress[]>>? resolver = null,
    Func<IPAddress, int, CancellationToken, Task<Stream>>? conectar = null,
    TimeSpan? atrasoEntreTentativas = null)
{
    // Connection Attempt Delay recomendado pela RFC 8305.
    public static readonly TimeSpan AtrasoPadrao = TimeSpan.FromMilliseconds(250);

    private readonly Func<string, CancellationToken, Task<IPAddress[]>> resolver = resolver ?? Dns.GetHostAddressesAsync;
    private readonly Func<IPAddress, int, CancellationToken, Task<Stream>> conectar = conectar ?? ConectarSocketAsync;
    private readonly TimeSpan atraso = atrasoEntreTentativas ?? AtrasoPadrao;

    public async ValueTask<Stream> ConectarAsync(DnsEndPoint destino, CancellationToken cancellationToken)
    {
        var inicio = Stopwatch.GetTimestamp();
        try
        {
            var enderecos = Intercalar(await resolver(destino.Host, cancellationToken));
            if (enderecos.Count == 0) throw new SocketException((int)SocketError.HostNotFound);
            var (stream, vencedor, falhas, abandonadas) = await DisputarAsync(enderecos, destino.Port, cancellationToken);
            logger.LogInformation(
                "Conexão TCP com o Telegram estabelecida via {AddressFamily} em {DurationMs} ms (falharam: {FailedFamilies}; abandonadas: {AbandonedFamilies}).",
                vencedor, (long)Stopwatch.GetElapsedTime(inicio).TotalMilliseconds, Familias(falhas), Familias(abandonadas));
            return stream;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Conexão TCP com o Telegram falhou após {DurationMs} ms ({ErrorType}; {Detail}; cancelamento: {CancellationOrigin}).",
                (long)Stopwatch.GetElapsedTime(inicio).TotalMilliseconds, exception.GetType().Name,
                DiagnosticoHttpDoTelegram.Detalhe(exception),
                cancellationToken.IsCancellationRequested ? "conexão abandonada pelo HttpClient"
                    : DiagnosticoHttpDoTelegram.SemCancelamento);
            throw;
        }
    }

    private async Task<(Stream Stream, AddressFamily Vencedor, List<AddressFamily> Falhas, List<AddressFamily> Abandonadas)>
        DisputarAsync(IReadOnlyList<IPAddress> enderecos, int porta, CancellationToken cancellationToken)
    {
        // Sem `using`: as perdedoras ainda observam este token depois do retorno; quem o descarta é a limpeza.
        var disputa = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pendentes = new Dictionary<Task<Stream>, IPAddress>();
        var falhas = new List<AddressFamily>();
        Exception? ultimaFalha = null;
        var proxima = 0;
        CancellationTokenSource? esperaCts = null;
        Task? espera = null;
        // O cancelamento do chamador participa de toda espera da disputa: sem ele, com todos os endereços já
        // iniciados, só uma tentativa que observasse o token acordaria o laço. Sinal nunca falha (TrySetResult).
        var cancelado = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registroDoCancelamento = cancellationToken.Register(
            static estado => ((TaskCompletionSource)estado!).TrySetResult(), cancelado);

        void IniciarProxima()
        {
            var endereco = enderecos[proxima++];
            pendentes.Add(conectar(endereco, porta, disputa.Token), endereco);
            esperaCts?.Cancel();
            esperaCts?.Dispose();
            esperaCts = null;
            espera = null;
            if (proxima < enderecos.Count)
            {
                esperaCts = CancellationTokenSource.CreateLinkedTokenSource(disputa.Token);
                espera = Task.Delay(atraso, esperaCts.Token);
            }
        }

        try
        {
            IniciarProxima();
            while (true)
            {
                var sinais = pendentes.Keys.Append(cancelado.Task);
                Task concluida = await Task.WhenAny(espera is null ? sinais : sinais.Append(espera));
                // Preserva a semântica do token do chamador; o finally cancela e rastreia todas as pendentes.
                cancellationToken.ThrowIfCancellationRequested();

                if (concluida == espera)
                {
                    IniciarProxima();
                    continue;
                }

                var tentativa = (Task<Stream>)concluida;
                var endereco = pendentes[tentativa];
                pendentes.Remove(tentativa);
                if (tentativa.IsCompletedSuccessfully)
                {
                    var abandonadas = pendentes.Values.Select(e => e.AddressFamily).ToList();
                    return (tentativa.Result, endereco.AddressFamily, falhas, abandonadas);
                }

                falhas.Add(endereco.AddressFamily);
                ultimaFalha = tentativa.Exception?.InnerException ?? new SocketException((int)SocketError.ConnectionAborted);
                // RFC 8305: a falha antecipa a próxima tentativa em vez de esperar o atraso.
                if (proxima < enderecos.Count) IniciarProxima();
                else if (pendentes.Count == 0) throw ultimaFalha;
            }
        }
        finally
        {
            // Toda tentativa ainda pendente (perdedoras, ou todas no cancelamento/falha) é cancelada já e entregue à
            // limpeza, sem que o retorno espere por ela: uma tentativa que ignore o token não segura a vencedora.
            try { disputa.Cancel(); }
            finally { Rastrear(FinalizarPendentesAsync([.. pendentes], disputa, esperaCts)); }
        }
    }

    // Limpezas em andamento, para que nada fique sem dono: os testes (e um diagnóstico) podem aguardá-las.
    private readonly ConcurrentDictionary<Task, byte> limpezas = new();

    internal Task LimpezasConcluidasAsync() => Task.WhenAll(limpezas.Keys);

    private void Rastrear(Task limpeza)
    {
        limpezas.TryAdd(limpeza, 0);
        // FinalizarPendentesAsync nunca falha; a continuação só tira a limpeza do registro.
        _ = limpeza.ContinueWith(concluida => limpezas.TryRemove(concluida, out _), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    // Espera cada tentativa em paralelo, observa a exceção de todas e descarta o stream de quem conectou tarde.
    // Só depois de todas terminarem descarta os CancellationTokenSource de que elas dependiam.
    private async Task FinalizarPendentesAsync(KeyValuePair<Task<Stream>, IPAddress>[] pendentes,
        CancellationTokenSource disputa, CancellationTokenSource? espera)
    {
        try
        {
            await Task.WhenAll(pendentes.Select(p => DescartarAsync(p.Key, p.Value.AddressFamily)));
        }
        finally
        {
            espera?.Dispose();
            disputa.Dispose();
        }
    }

    private async Task DescartarAsync(Task<Stream> tentativa, AddressFamily familia)
    {
        Stream stream;
        try
        {
            stream = await tentativa;
        }
        catch
        {
            // Cancelada ou falhou: exceção observada aqui; o conector já descartou o próprio socket.
            return;
        }
        try
        {
            await stream.DisposeAsync();
            logger.LogDebug("Tentativa via {AddressFamily} concluiu depois da disputa e foi descartada.", familia);
        }
        catch (Exception exception)
        {
            logger.LogDebug("Falha ao descartar a tentativa via {AddressFamily} ({ErrorType}).", familia,
                exception.GetType().Name);
        }
    }

    // RFC 8305 §4: intercala as famílias, começando pela primeira que o DNS devolveu.
    internal static IReadOnlyList<IPAddress> Intercalar(IReadOnlyList<IPAddress> enderecos)
    {
        if (enderecos.Count == 0) return [];
        var primeira = enderecos[0].AddressFamily;
        var preferidos = new Queue<IPAddress>(enderecos.Where(e => e.AddressFamily == primeira));
        var outros = new Queue<IPAddress>(enderecos.Where(e => e.AddressFamily != primeira));
        var resultado = new List<IPAddress>(enderecos.Count);
        while (preferidos.Count > 0 || outros.Count > 0)
        {
            if (preferidos.TryDequeue(out var p)) resultado.Add(p);
            if (outros.TryDequeue(out var o)) resultado.Add(o);
        }
        return resultado;
    }

    internal static async Task<Stream> ConectarSocketAsync(IPAddress endereco, int porta, CancellationToken cancellationToken)
    {
        var socket = new Socket(endereco.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(endereco, porta, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static string Familias(List<AddressFamily> familias) =>
        familias.Count == 0 ? "-" : string.Join(",", familias);
}
