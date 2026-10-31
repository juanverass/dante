using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Dante.Worker.Telegram;
using Microsoft.Extensions.Logging;

namespace Dante.Tests;

// Happy Eyeballs do cliente do Telegram, com resolver e conexões simulados (endereços de documentação, RFC 3849/5737).
public sealed class ConexaoTcpDoTelegramTests
{
    private static readonly IPAddress V6 = IPAddress.Parse("2001:db8::1");
    private static readonly IPAddress V4 = IPAddress.Parse("192.0.2.1");
    private static readonly DnsEndPoint Telegram = new("api.telegram.org", 443);

    [Fact]
    public async Task UnreachableIpv6FallsBackToIpv4WithoutWaitingForTheTcpTimeout()
    {
        var rede = new RedeSimulada { [V6] = Comportamento.SemResposta, [V4] = Comportamento.Conecta };
        var logger = new CapturingLogger();
        var relogio = Stopwatch.StartNew();

        var stream = await Conexao(rede, logger, TimeSpan.FromMilliseconds(50)).ConectarAsync(Telegram, CancellationToken.None);

        Assert.True(relogio.Elapsed < TimeSpan.FromSeconds(5), $"fallback levou {relogio.Elapsed}");
        Assert.Same(rede.StreamDe(V4), stream);
        Assert.Equal([V6, V4], rede.Tentativas);
        Assert.Contains(V6, rede.Canceladas);
        var linha = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, linha.Level);
        Assert.Contains("estabelecida via InterNetwork em", linha.Message);
        Assert.Contains("abandonadas: InterNetworkV6", linha.Message);
        await rede.AssertSemVazamentoAsync(vencedor: stream);
    }

    [Fact]
    public async Task WorkingIpv6WinsWithoutTryingIpv4()
    {
        var rede = new RedeSimulada { [V6] = Comportamento.Conecta, [V4] = Comportamento.Conecta };
        var logger = new CapturingLogger();

        var stream = await Conexao(rede, logger, TimeSpan.FromSeconds(10)).ConectarAsync(Telegram, CancellationToken.None);

        Assert.Same(rede.StreamDe(V6), stream);
        Assert.Equal([V6], rede.Tentativas);
        Assert.Contains("estabelecida via InterNetworkV6", Assert.Single(logger.Entries).Message);
        await rede.AssertSemVazamentoAsync(vencedor: stream);
    }

    [Fact]
    public async Task RefusedIpv4ImmediatelyStartsIpv6WithoutWaitingTheAttemptDelay()
    {
        // DNS devolvendo IPv4 primeiro: a falha antecipa a próxima tentativa (RFC 8305), o atraso de 10 s não corre.
        var rede = new RedeSimulada([V4, V6]) { [V4] = Comportamento.Recusa, [V6] = Comportamento.Conecta };
        var logger = new CapturingLogger();
        var relogio = Stopwatch.StartNew();

        var stream = await Conexao(rede, logger, TimeSpan.FromSeconds(10)).ConectarAsync(Telegram, CancellationToken.None);

        Assert.True(relogio.Elapsed < TimeSpan.FromSeconds(5), $"a falha não antecipou a tentativa: {relogio.Elapsed}");
        Assert.Same(rede.StreamDe(V6), stream);
        Assert.Contains("falharam: InterNetwork;", Assert.Single(logger.Entries).Message);
        await rede.AssertSemVazamentoAsync(vencedor: stream);
    }

    [Fact]
    public async Task BothFamiliesUnavailableFailWithTheSocketErrorAndNoLeftovers()
    {
        var rede = new RedeSimulada { [V6] = Comportamento.Recusa, [V4] = Comportamento.Recusa };
        var logger = new CapturingLogger();

        var erro = await Assert.ThrowsAsync<SocketException>(async () =>
            await Conexao(rede, logger, TimeSpan.FromMilliseconds(50)).ConectarAsync(Telegram, CancellationToken.None));

        Assert.Equal(SocketError.ConnectionRefused, erro.SocketErrorCode);
        Assert.Equal([V6, V4], rede.Tentativas);
        var linha = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, linha.Level);
        Assert.Contains("SocketError=ConnectionRefused", linha.Message);
        await rede.AssertSemVazamentoAsync(vencedor: null);
    }

    [Fact]
    public async Task NoAddressesIsAHostNotFoundFailure()
    {
        var rede = new RedeSimulada([]);

        var erro = await Assert.ThrowsAsync<SocketException>(async () =>
            await Conexao(rede, new CapturingLogger(), null).ConectarAsync(Telegram, CancellationToken.None));

        Assert.Equal(SocketError.HostNotFound, erro.SocketErrorCode);
    }

    [Fact]
    public async Task CancellationDuringTheAttemptsStopsEveryAttemptAndIsReportedAsAbandoned()
    {
        // Ambas sem resposta: só o token do chamador (HttpClient.Timeout ou encerramento) encerra a disputa.
        var rede = new RedeSimulada { [V6] = Comportamento.SemResposta, [V4] = Comportamento.SemResposta };
        var logger = new CapturingLogger();
        using var cancelamento = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await Conexao(rede, logger, TimeSpan.FromMilliseconds(50)).ConectarAsync(Telegram, cancelamento.Token));

        Assert.Equal([V6, V4], rede.Tentativas);
        Assert.Equal(2, rede.Canceladas.Count);
        Assert.Contains("conexão abandonada pelo HttpClient", Assert.Single(logger.Entries).Message);
        await rede.AssertSemVazamentoAsync(vencedor: null);
    }

    [Fact]
    public async Task ALoserThatConnectsAfterTheWinnerIsDisposed()
    {
        // O IPv6 ignora o cancelamento e conecta depois do IPv4: esse stream não pode ficar aberto.
        var rede = new RedeSimulada
        {
            [V6] = Comportamento.ConectaTardeIgnorandoCancelamento,
            [V4] = Comportamento.Conecta
        };

        var stream = await Conexao(rede, new CapturingLogger(), TimeSpan.FromMilliseconds(50))
            .ConectarAsync(Telegram, CancellationToken.None);

        Assert.Same(rede.StreamDe(V4), stream);
        await rede.AssertSemVazamentoAsync(vencedor: stream);
        Assert.True(rede.StreamDe(V6)!.Descartado);
        Assert.False(((StreamRastreado)stream).Descartado);
    }

    [Fact]
    public async Task WinnerIsReturnedWithoutWaitingForALoserThatIgnoresCancellation()
    {
        // Cenário A: a perdedora não coopera e só conclui quando o teste decide; a vencedora não pode esperar por ela.
        var liberarV6 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tokenV6 = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rede = new RedeSimulada { [V4] = Comportamento.Conecta };
        rede.Roteiros[V6] = async token =>
        {
            tokenV6.SetResult(token);
            await liberarV6.Task;
            return new StreamRastreado();
        };

        var stream = await Conexao(rede, new CapturingLogger(), TimeSpan.FromMilliseconds(20))
            .ConectarAsync(Telegram, CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Same(rede.StreamDe(V4), stream);
        Assert.True((await tokenV6.Task).IsCancellationRequested, "a perdedora recebe o cancelamento ao fim da disputa");
        Assert.False(rede.Conexao!.LimpezasConcluidasAsync().IsCompleted);
        liberarV6.SetResult();
        await rede.AssertSemVazamentoAsync(vencedor: stream);
        Assert.True(rede.StreamDe(V6)!.Descartado);
    }

    [Fact]
    public async Task SimultaneousConnectionsReturnExactlyOneAndDisposeTheOther()
    {
        // Cenário B: o IPv4 libera o IPv6 ao ser iniciado, e as duas tentativas concluem antes de a disputa observá-las.
        var liberar = new TaskCompletionSource();
        var rede = new RedeSimulada();
        rede.Roteiros[V6] = async _ =>
        {
            await liberar.Task;
            return new StreamRastreado();
        };
        rede.Roteiros[V4] = _ =>
        {
            liberar.SetResult();
            return Task.FromResult(new StreamRastreado());
        };

        var stream = await Conexao(rede, new CapturingLogger(), TimeSpan.FromMilliseconds(20))
            .ConectarAsync(Telegram, CancellationToken.None);

        await rede.AssertSemVazamentoAsync(vencedor: stream);
        Assert.Equal(2, new[] { rede.StreamDe(V6), rede.StreamDe(V4) }.Count(s => s is not null));
        Assert.Single(new[] { rede.StreamDe(V6)!, rede.StreamDe(V4)! }, s => !s.Descartado);
    }

    [Fact]
    public async Task LoserExceptionsAreObservedAndNeverBecomeUnobservedTaskExceptions()
    {
        // Cenário C: perdedoras que falham depois da vitória, com exceção comum e com cancelamento.
        var naoObservadas = new ConcurrentBag<Exception>();
        void Registrar(object? _, UnobservedTaskExceptionEventArgs e)
        {
            if (e.Exception.Flatten().InnerExceptions.Any(x => x is FalhaDaPerdedoraException)) naoObservadas.Add(e.Exception);
        }
        TaskScheduler.UnobservedTaskException += Registrar;
        try
        {
            var v6b = IPAddress.Parse("2001:db8::2");
            var liberar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var rede = new RedeSimulada([V6, v6b, V4]) { [V4] = Comportamento.Conecta };
            var iniciadas = new CountdownEvent(2);
            rede.Roteiros[V6] = async _ =>
            {
                iniciadas.Signal();
                await liberar.Task;
                throw new FalhaDaPerdedoraException();
            };
            rede.Roteiros[v6b] = async token =>
            {
                iniciadas.Signal();
                await liberar.Task;
                token.ThrowIfCancellationRequested();
                throw new UnreachableException();
            };
            // Intercalado: V6, V4, v6b. O IPv4 só conecta depois de as duas perdedoras estarem em andamento.
            rede.Roteiros[V4] = async _ =>
            {
                while (!iniciadas.IsSet) await Task.Delay(5);
                return new StreamRastreado();
            };

            var stream = await Conexao(rede, new CapturingLogger(), TimeSpan.FromMilliseconds(10))
                .ConectarAsync(Telegram, CancellationToken.None);
            liberar.SetResult();
            await rede.AssertSemVazamentoAsync(vencedor: stream);

            Assert.Same(rede.StreamDe(V4), stream);
            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            Assert.Empty(naoObservadas);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Registrar;
        }
    }

    [Fact]
    public async Task GeneralCancellationReachesEveryAttemptAndLeavesNoConnectionOpen()
    {
        // Cenário D: duas tentativas cooperam e uma terceira conecta mesmo assim, depois do cancelamento.
        var v6b = IPAddress.Parse("2001:db8::2");
        var rede = new RedeSimulada([V6, v6b, V4]) { [V6] = Comportamento.SemResposta, [V4] = Comportamento.SemResposta };
        var tokenV6b = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        rede.Roteiros[v6b] = async token =>
        {
            tokenV6b.SetResult(token);
            while (!token.IsCancellationRequested) await Task.Delay(5, CancellationToken.None);
            return new StreamRastreado();
        };
        using var cancelamento = new CancellationTokenSource();
        var logger = new CapturingLogger();

        var conexao = Conexao(rede, logger, TimeSpan.FromMilliseconds(20)).ConectarAsync(Telegram, cancelamento.Token);
        await tokenV6b.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancelamento.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await conexao.AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        await rede.AssertSemVazamentoAsync(vencedor: null);
        Assert.Equal([V6, V4, v6b], rede.Tentativas);
        Assert.Equal(new[] { V4, V6 }.OrderBy(a => a.ToString()), rede.Canceladas.OrderBy(a => a.ToString()));
        Assert.True((await tokenV6b.Task).IsCancellationRequested);
        Assert.True(rede.StreamDe(v6b)!.Descartado);
        Assert.Contains("conexão abandonada pelo HttpClient",
            Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning).Message);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Debug &&
            e.Message.Contains("InterNetworkV6 concluiu depois da disputa e foi descartada"));
    }

    [Fact]
    public async Task AFailingDisposeDoesNotStopTheCleanupOfTheOtherLosers()
    {
        // Cenário E: duas perdedoras conectam depois da vitória; o descarte da primeira lança, o da segunda acontece.
        var v6b = IPAddress.Parse("2001:db8::2");
        var liberar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var iniciadas = new CountdownEvent(2);
        var rede = new RedeSimulada([V6, v6b, V4]);
        rede.Roteiros[V6] = async _ =>
        {
            iniciadas.Signal();
            await liberar.Task;
            return new StreamRastreado(falharAoDescartar: true);
        };
        rede.Roteiros[v6b] = async _ =>
        {
            iniciadas.Signal();
            await liberar.Task;
            return new StreamRastreado();
        };
        rede.Roteiros[V4] = async _ =>
        {
            while (!iniciadas.IsSet) await Task.Delay(5);
            return new StreamRastreado();
        };

        var stream = await Conexao(rede, new CapturingLogger(), TimeSpan.FromMilliseconds(10))
            .ConectarAsync(Telegram, CancellationToken.None);
        liberar.SetResult();
        await rede.AssertSemVazamentoAsync(vencedor: stream);

        Assert.Same(rede.StreamDe(V4), stream);
        Assert.True(rede.StreamDe(V6)!.Descartado);
        Assert.True(rede.StreamDe(v6b)!.Descartado);
    }

    [Fact]
    public void AddressesAreInterleavedByFamilyStartingWithTheFirstDnsFamily()
    {
        var v6b = IPAddress.Parse("2001:db8::2");
        var v4b = IPAddress.Parse("192.0.2.2");

        Assert.Equal([V6, V4, v6b, v4b], ConexaoTcpDoTelegram.Intercalar([V6, v6b, V4, v4b]));
        Assert.Equal([V4, V6, v4b], ConexaoTcpDoTelegram.Intercalar([V4, v4b, V6]));
        Assert.Empty(ConexaoTcpDoTelegram.Intercalar([]));
    }

    [Fact]
    public async Task HttpRequestSucceedsThroughTheFallbackWhileIpv6IsUnreachable()
    {
        // Ponta a ponta com HttpClient e socket real em loopback: o IPv6 simulado nunca responde, o IPv4 é o servidor local.
        using var servidor = new TcpListener(IPAddress.Loopback, 0);
        servidor.Start();
        var porta = ((IPEndPoint)servidor.LocalEndpoint).Port;
        var atendimento = AtenderUmaVezAsync(servidor);
        var canceladas = new ConcurrentBag<IPAddress>();
        var conexao = new ConexaoTcpDoTelegram(new CapturingLogger(),
            (_, _) => Task.FromResult(new[] { V6, IPAddress.Loopback }),
            async (endereco, p, token) =>
            {
                if (!endereco.Equals(V6)) return await ConexaoTcpDoTelegram.ConectarSocketAsync(endereco, p, token);
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException) { canceladas.Add(endereco); throw; }
                throw new UnreachableException();
            },
            TimeSpan.FromMilliseconds(50));
        using var http = new HttpClient(new SocketsHttpHandler
        {
            ConnectCallback = (contexto, token) => conexao.ConectarAsync(contexto.DnsEndPoint, token)
        }) { Timeout = TimeSpan.FromSeconds(10) };

        var resposta = await http.GetStringAsync($"http://api.telegram.test:{porta}/");

        Assert.Equal("ok", resposta);
        Assert.Contains(V6, canceladas);
        await atendimento;
    }

    [Fact]
    public void TelegramHandlerUsesTheHappyEyeballsConnectWithDefaultTimeouts()
    {
        var handler = DiagnosticoHttpDoTelegram.CriarHandler(new CapturingLogger());
        using var http = new HttpClient(handler);

        Assert.NotNull(handler.ConnectCallback);
        Assert.Equal(new SocketsHttpHandler().ConnectTimeout, handler.ConnectTimeout);
        Assert.Equal(TimeSpan.FromSeconds(100), http.Timeout);
    }

    private static ConexaoTcpDoTelegram Conexao(RedeSimulada rede, ILogger logger, TimeSpan? atraso) =>
        rede.Conexao = new(logger, rede.ResolverAsync, rede.ConectarAsync, atraso);

    private static async Task AtenderUmaVezAsync(TcpListener servidor)
    {
        using var cliente = await servidor.AcceptTcpClientAsync();
        var stream = cliente.GetStream();
        var buffer = new byte[4096];
        var recebido = new StringBuilder();
        while (!recebido.ToString().Contains("\r\n\r\n"))
        {
            var lidos = await stream.ReadAsync(buffer);
            if (lidos == 0) return;
            recebido.Append(Encoding.ASCII.GetString(buffer, 0, lidos));
        }
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"));
    }

    private enum Comportamento { Conecta, Recusa, SemResposta, ConectaTardeIgnorandoCancelamento }

    private sealed class RedeSimulada(IPAddress[]? dns = null) : Dictionary<IPAddress, Comportamento>
    {
        private readonly IPAddress[]? dns = dns;
        private readonly ConcurrentQueue<IPAddress> tentativas = new();
        private readonly ConcurrentDictionary<IPAddress, StreamRastreado> streams = new();
        private int emAndamento;

        public IPAddress[] Tentativas => tentativas.ToArray();
        public ConcurrentBag<IPAddress> Canceladas { get; } = [];
        public ConexaoTcpDoTelegram? Conexao { get; set; }
        // Roteiro próprio por endereço, para conexões que ignoram o cancelamento ou concluem num instante escolhido.
        public Dictionary<IPAddress, Func<CancellationToken, Task<StreamRastreado>>> Roteiros { get; } = [];

        public StreamRastreado? StreamDe(IPAddress endereco) => streams.GetValueOrDefault(endereco);

        public Task<IPAddress[]> ResolverAsync(string host, CancellationToken token) =>
            Task.FromResult(dns ?? [V6, V4]);

        public async Task<Stream> ConectarAsync(IPAddress endereco, int porta, CancellationToken token)
        {
            tentativas.Enqueue(endereco);
            Interlocked.Increment(ref emAndamento);
            try
            {
                if (Roteiros.TryGetValue(endereco, out var roteiro))
                    return streams[endereco] = await roteiro(token);
                switch (this[endereco])
                {
                    case Comportamento.Recusa:
                        await Task.Yield();
                        throw new SocketException((int)SocketError.ConnectionRefused);
                    case Comportamento.SemResposta:
                        try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                        catch (OperationCanceledException) { Canceladas.Add(endereco); throw; }
                        throw new UnreachableException();
                    case Comportamento.ConectaTardeIgnorandoCancelamento:
                        await Task.Delay(200, CancellationToken.None);
                        break;
                    default:
                        await Task.Yield();
                        break;
                }
                return streams[endereco] = new StreamRastreado();
            }
            finally
            {
                Interlocked.Decrement(ref emAndamento);
            }
        }

        // Concluída a limpeza das perdedoras, nenhuma tentativa segue em andamento e só o vencedor continua aberto.
        public async Task AssertSemVazamentoAsync(Stream? vencedor)
        {
            await Conexao!.LimpezasConcluidasAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, Volatile.Read(ref emAndamento));
            foreach (var stream in streams.Values)
                Assert.Equal(!ReferenceEquals(stream, vencedor), stream.Descartado);
        }
    }

    private sealed class StreamRastreado(bool falharAoDescartar = false) : MemoryStream
    {
        private int descartado;
        public bool Descartado => Volatile.Read(ref descartado) == 1;

        protected override void Dispose(bool disposing)
        {
            Interlocked.Exchange(ref descartado, 1);
            base.Dispose(disposing);
            if (disposing && falharAoDescartar) throw new IOException("falha simulada no descarte");
        }
    }

    private sealed class FalhaDaPerdedoraException() : Exception("falha simulada da perdedora");

    private sealed class CapturingLogger : ILogger
    {
        public readonly List<(LogLevel Level, string Message)> Entries = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
