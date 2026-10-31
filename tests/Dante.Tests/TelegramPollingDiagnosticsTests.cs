using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Dante.Application.Agentes;
using Dante.Worker.Jobs;
using Dante.Worker.Telegram;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dante.Tests;

// Instrumentação do polling do Telegram: tudo com handlers simulados ou sockets locais, nunca a API real.
public sealed class TelegramPollingDiagnosticsTests
{
    private const string Token = "123456:SEGREDO-DO-BOT";
    private const string MessageText = "conteudo-privado-do-usuario";

    [Fact]
    public async Task SuccessfulPollLogsStartDurationStatusAndHttpVersionWithoutContent()
    {
        var logger = new CapturingLogger<TelegramBotApi>();
        using var http = new HttpClient(new StubHandler((_, _) => Task.FromResult(Updates())));
        var api = Api(http, logger);

        var updates = await api.GetUpdatesAsync(7, CancellationToken.None);
        await api.GetUpdatesAsync(43, CancellationToken.None);

        Assert.Single(updates);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Debug && e.Message.Contains("getUpdates #1 iniciado"));
        var done = Assert.Single(logger.Entries, e => e.Message.Contains("getUpdates #1 concluído"));
        Assert.Equal(LogLevel.Debug, done.Level);
        Assert.Contains("HTTP 200 HTTP/1.1", done.Message);
        Assert.Contains("1 updates", done.Message);
        // The second request reports the interval since the previous one ended.
        Assert.Contains(logger.Entries, e => e.Message.Contains("getUpdates #2 iniciado") &&
            e.Message.Contains("intervalo desde o anterior") && !e.Message.Contains("(null)"));
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Warning);
        AssertNoSecrets(logger);
    }

    [Fact]
    public async Task WorkerShutdownIsClassifiedAsShutdownAndNotWarned()
    {
        var logger = new CapturingLogger<TelegramBotApi>();
        using var shutdown = new CancellationTokenSource();
        using var http = new HttpClient(new StubHandler(async (_, token) =>
        {
            shutdown.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Updates();
        }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Api(http, logger).GetUpdatesAsync(0, shutdown.Token));

        var failure = Assert.Single(logger.Entries, e => e.Message.Contains("falhou"));
        Assert.Equal(LogLevel.Debug, failure.Level);
        Assert.Contains(DiagnosticoHttpDoTelegram.Encerramento, failure.Message);
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task HttpClientTimeoutIsClassifiedAsTimeoutWithInnerTypeAndDuration()
    {
        var logger = new CapturingLogger<TelegramBotApi>();
        // The production signature: the request never completes and HttpClient.Timeout fires first.
        using var http = new HttpClient(new StubHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Updates();
        })) { Timeout = TimeSpan.FromMilliseconds(200) };

        var error = await Assert.ThrowsAsync<TaskCanceledException>(() =>
            Api(http, logger).GetUpdatesAsync(0, CancellationToken.None));

        Assert.IsType<TimeoutException>(error.InnerException);
        var failure = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("TaskCanceledException", failure.Message);
        Assert.Contains("interna: TimeoutException", failure.Message);
        Assert.Contains(DiagnosticoHttpDoTelegram.TimeoutDoHttpClient, failure.Message);
        Assert.Contains("HTTP - HTTP/-", failure.Message);
        AssertNoSecrets(logger);
    }

    [Fact]
    public async Task HttpErrorsLogStatusAndCodesButNeverTheExceptionMessage()
    {
        var logger = new CapturingLogger<TelegramBotApi>();
        // HttpRequestException messages may embed the token-bearing URL; only types and enumerated codes are logged.
        using var http = new HttpClient(new StubHandler((request, _) => throw new HttpRequestException(
            HttpRequestError.ConnectionError, $"falhou {request.RequestUri}",
            new SocketException((int)SocketError.TimedOut))));

        await Assert.ThrowsAsync<HttpRequestException>(() => Api(http, logger).GetUpdatesAsync(0, CancellationToken.None));

        var failure = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("HttpRequestError=ConnectionError, SocketError=TimedOut", failure.Message);
        Assert.Contains(DiagnosticoHttpDoTelegram.SemCancelamento, failure.Message);

        logger.Entries.Clear();
        using var unauthorized = new HttpClient(new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
                { Content = new StringContent($$"""{"ok":false,"description":"{{MessageText}}"}""") })));
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            Api(unauthorized, logger).GetUpdatesAsync(0, CancellationToken.None));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("HTTP 401 HTTP/1.1"));
        AssertNoSecrets(logger);
    }

    [Fact]
    public async Task SlowResponsesAreWarnedWithTheirDurationEvenWhenTheyComplete()
    {
        var logger = new CapturingLogger<TelegramBotApi>();
        var time = new ManualTime();
        var delays = new Queue<TimeSpan>([TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(97)]);
        using var http = new HttpClient(new StubHandler((_, _) =>
        {
            time.Advance(delays.Dequeue());
            return Task.FromResult(Updates());
        }));
        var api = Api(http, logger, time);

        await api.GetUpdatesAsync(0, CancellationToken.None);
        await api.GetUpdatesAsync(0, CancellationToken.None);

        var normal = Assert.Single(logger.Entries, e => e.Message.Contains("getUpdates #1 concluído"));
        Assert.Equal(LogLevel.Debug, normal.Level);
        Assert.Contains("25000 ms", normal.Message);
        var slow = Assert.Single(logger.Entries, e => e.Message.Contains("getUpdates #2 concluído"));
        Assert.Equal(LogLevel.Warning, slow.Level);
        Assert.Contains("97000 ms", slow.Message);
        AssertNoSecrets(logger);
    }

    [Fact]
    public void CancellationOriginSeparatesShutdownTimeoutAndOtherCancellations()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var timeout = new TaskCanceledException("t", new TimeoutException());

        Assert.Equal(DiagnosticoHttpDoTelegram.Encerramento,
            DiagnosticoHttpDoTelegram.OrigemDoCancelamento(timeout, cancelled.Token));
        Assert.Equal(DiagnosticoHttpDoTelegram.TimeoutDoHttpClient,
            DiagnosticoHttpDoTelegram.OrigemDoCancelamento(timeout, CancellationToken.None));
        Assert.Equal(DiagnosticoHttpDoTelegram.OutraOrigem,
            DiagnosticoHttpDoTelegram.OrigemDoCancelamento(new TaskCanceledException(), CancellationToken.None));
        Assert.Equal(DiagnosticoHttpDoTelegram.SemCancelamento,
            DiagnosticoHttpDoTelegram.OrigemDoCancelamento(new HttpRequestException("x"), cancelled.Token));
    }

    [Fact]
    public async Task PollingTimeoutIsLoggedWithOriginRetryDelayAndRecovery()
    {
        var logger = new CapturingLogger<TelegramPollingService>();
        var api = new ScriptedBotApi(
            () => throw new TaskCanceledException($"https://api.telegram.org/bot{Token}/getUpdates", new TimeoutException()));
        using var service = Service(api, logger);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await api.Recovered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        var failure = Assert.Single(logger.Entries, e => e.Message.StartsWith("Falha no polling do Telegram"));
        Assert.Equal(LogLevel.Warning, failure.Level);
        Assert.Contains("(TaskCanceledException)", failure.Message);
        Assert.Contains("tentando novamente em 3s", failure.Message);
        Assert.Contains("Interna: TimeoutException", failure.Message);
        Assert.Contains(DiagnosticoHttpDoTelegram.TimeoutDoHttpClient, failure.Message);
        Assert.Contains("falhas seguidas: 1", failure.Message);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information &&
            e.Message.Contains("restabelecido após 1 falha(s)"));
        Assert.True(api.Calls[1] - api.Calls[0] >= TimeSpan.FromSeconds(2.9), "a nova tentativa respeita o intervalo de 3s");
        AssertNoSecrets(logger);
    }

    [Fact]
    public async Task PollingStopDuringRequestIsNotLoggedAsFailure()
    {
        var logger = new CapturingLogger<TelegramPollingService>();
        var api = new ScriptedBotApi();
        using var service = Service(api, logger);

        await service.StartAsync(CancellationToken.None);
        await api.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await service.StopAsync(CancellationToken.None);

        Assert.DoesNotContain(logger.Entries, e => e.Message.StartsWith("Falha no polling do Telegram"));
    }

    [Fact]
    public async Task ConnectDiagnosticsReportTheAddressFamilyAndKeepTheDefaultConnectBehavior()
    {
        var logger = new CapturingLogger<TelegramBotApi>();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accept = listener.AcceptTcpClientAsync();

        await using (var stream = await new ConexaoTcpDoTelegram(logger).ConectarAsync(new DnsEndPoint("127.0.0.1", port), CancellationToken.None))
        {
            using var server = await accept;
            await stream.WriteAsync(new byte[] { 1 });
            Assert.Equal(1, await server.GetStream().ReadAsync(new byte[1]));
        }

        var connected = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, connected.Level);
        Assert.Contains("estabelecida via InterNetwork em", connected.Message);
    }

    [Fact]
    public async Task ConnectFailuresReportTheSocketErrorAndAbandonment()
    {
        var logger = new CapturingLogger<TelegramBotApi>();
        int closedPort;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            closedPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        await Assert.ThrowsAsync<SocketException>(async () =>
            await new ConexaoTcpDoTelegram(logger).ConectarAsync(new DnsEndPoint("127.0.0.1", closedPort), CancellationToken.None));
        var refused = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, refused.Level);
        Assert.Contains("SocketError=ConnectionRefused", refused.Message);
        Assert.Contains(DiagnosticoHttpDoTelegram.SemCancelamento, refused.Message);

        logger.Entries.Clear();
        using var abandoned = new CancellationTokenSource();
        abandoned.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await new ConexaoTcpDoTelegram(logger).ConectarAsync(new DnsEndPoint("127.0.0.1", closedPort), abandoned.Token));
        Assert.Contains("conexão abandonada pelo HttpClient", Assert.Single(logger.Entries).Message);
    }

    [Fact]
    public void DiagnosticHandlerKeepsTheDefaultHttpClientTimeoutAndCancellation()
    {
        var handler = DiagnosticoHttpDoTelegram.CriarHandler(new CapturingLogger<TelegramBotApi>());
        using var http = new HttpClient(handler);

        // Same 100s timeout and connect timeout as `new HttpClient()`: diagnostics only, no behavior change.
        Assert.Equal(TimeSpan.FromSeconds(100), http.Timeout);
        Assert.Equal(new SocketsHttpHandler().ConnectTimeout, handler.ConnectTimeout);
        Assert.NotNull(handler.ConnectCallback);
    }

    private static TelegramBotApi Api(HttpClient http, ILogger<TelegramBotApi> logger, TimeProvider? time = null) =>
        new(http, Options.Create(new TelegramOptions { BotToken = Token }), logger, time);

    private static HttpResponseMessage Updates() => new(HttpStatusCode.OK)
    {
        Content = new StringContent($$$"""
            {"ok":true,"result":[{"update_id":42,"message":{"chat":{"id":-123},"from":{"id":999,"username":"alguem"},"text":"{{{MessageText}}}"}}]}
            """)
    };

    private static TelegramPollingService Service(ITelegramBotApi api, ILogger<TelegramPollingService> logger)
    {
        var options = Options.Create(new TelegramOptions { BotToken = Token, AllowedUserIds = "123" });
        return new TelegramPollingService(api, options, new TelegramUserAuthorizer(options),
            new UnusedRunner(), new UnusedRunner(), new JobRegistry(), logger);
    }

    private static void AssertNoSecrets<T>(CapturingLogger<T> logger)
    {
        foreach (var entry in logger.Entries)
        {
            Assert.DoesNotContain(Token, entry.Message);
            Assert.DoesNotContain("SEGREDO", entry.Message);
            Assert.DoesNotContain("api.telegram.org", entry.Message);
            Assert.DoesNotContain(MessageText, entry.Message);
            Assert.DoesNotContain("alguem", entry.Message);
        }
    }

    // First call runs the scripted failures in order; afterwards it succeeds once, then waits for shutdown.
    private sealed class ScriptedBotApi(params Func<IReadOnlyList<TelegramUpdate>>[] failures) : ITelegramBotApi
    {
        private int calls;
        public TaskCompletionSource Recovered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<TimeSpan> CallTimes { get; } = new();
        public TimeSpan[] Calls => CallTimes.ToArray();
        private readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();

        public async Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(long offset, CancellationToken cancellationToken)
        {
            CallTimes.Enqueue(clock.Elapsed);
            var index = calls++;
            if (index < failures.Length) return failures[index]();
            if (index == failures.Length && failures.Length > 0)
            {
                Recovered.TrySetResult();
                return [];
            }
            Waiting.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return [];
        }

        public Task SendMessageAsync(long chatId, string text, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => handle(request, cancellationToken);
    }

    private sealed class ManualTime : TimeProvider
    {
        private long ticks = 1;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
        public void Advance(TimeSpan by) => Interlocked.Add(ref ticks, by.Ticks);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
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

    private sealed class UnusedRunner : ICodexRunner, IClaudeRunner
    {
        public Task<AgentProcessResult> RunAsync(string prompt, string workingDirectory,
            CancellationToken cancellationToken = default, bool generalMode = false,
            IReadOnlyDictionary<string, string>? environment = null, string? model = null, string? effort = null,
            IReadOnlyList<Dante.Application.Anexos.Attachment>? attachments = null, IReadOnlyList<string>? additionalDirectories = null) =>
            throw new InvalidOperationException("Runner não deveria ser chamado.");
    }
}
