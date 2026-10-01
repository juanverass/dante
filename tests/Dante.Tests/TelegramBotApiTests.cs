using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Dante.Worker.Agents;
using Dante.Worker.Jobs;
using Dante.Worker.Sessions;
using Dante.Worker.Telegram;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dante.Tests;

public sealed class TelegramBotApiTests
{
    [Fact]
    public async Task PollsAndRepliesThroughTelegramApi()
    {
        var requests = new List<(string Path, JsonElement Body)>();
        using var httpClient = new HttpClient(new StubHandler(async request =>
        {
            requests.Add((request.RequestUri!.AbsolutePath,
                JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement.Clone()));

            var result = request.RequestUri.AbsolutePath.EndsWith("getUpdates", StringComparison.Ordinal)
                ? """{"ok":true,"result":[{"update_id":42,"message":{"chat":{"id":-123},"from":{"id":123,"username":"someone_else"},"text":"/ping"}}]}"""
                : """{"ok":true,"result":{"message_id":5}}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(result) };
        }));
        var api = new TelegramBotApi(httpClient, Options.Create(new TelegramOptions { BotToken = "test-token" }));

        var updates = await api.GetUpdatesAsync(41, CancellationToken.None);
        await api.SendMessageAsync(updates[0].Message!.Chat.Id, "pong", CancellationToken.None);
        await api.SendChatActionAsync(updates[0].Message!.Chat.Id, "typing", CancellationToken.None);

        Assert.Equal(42, updates[0].UpdateId);
        Assert.Equal("/ping", updates[0].Message!.Text);
        Assert.Equal(123, updates[0].Message!.From!.Id);
        Assert.Equal("/bottest-token/getUpdates", requests[0].Path);
        Assert.Equal(41, requests[0].Body.GetProperty("offset").GetInt64());
        Assert.Equal(25, requests[0].Body.GetProperty("timeout").GetInt32());
        Assert.Equal(-123, requests[1].Body.GetProperty("chat_id").GetInt64());
        Assert.Equal("pong", requests[1].Body.GetProperty("text").GetString());
        Assert.Equal("/bottest-token/sendChatAction", requests[2].Path);
        Assert.Equal(-123, requests[2].Body.GetProperty("chat_id").GetInt64());
        Assert.Equal("typing", requests[2].Body.GetProperty("action").GetString());
    }

    [Fact]
    public async Task RejectsUnsuccessfulTelegramResponse()
    {
        using var httpClient = new HttpClient(new StubHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":false,"description":"bad request"}""")
            })));
        var api = new TelegramBotApi(httpClient, Options.Create(new TelegramOptions { BotToken = "test-token" }));

        await Assert.ThrowsAsync<JsonException>(() => api.GetUpdatesAsync(0, CancellationToken.None));
    }

    [Fact]
    public async Task SendMessageReportsRateLimitForDeliveryRetry()
    {
        using var httpClient = new HttpClient(new StubHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("""{"ok":false,"parameters":{"retry_after":2}}""")
            })));
        var api = new TelegramBotApi(httpClient, Options.Create(new TelegramOptions { BotToken = "test-token" }));

        var error = await Assert.ThrowsAnyAsync<HttpRequestException>(() =>
            api.SendMessageAsync(-123, "texto", CancellationToken.None));

        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
    }

    [Fact]
    public async Task InlineApprovalUsesBotApiKeyboardCallbackAndEditContracts()
    {
        var requests = new List<(string Path, JsonElement Body)>();
        using var http = new HttpClient(new StubHandler(async request =>
        {
            requests.Add((request.RequestUri!.AbsolutePath,
                JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement.Clone()));
            var result = request.RequestUri.AbsolutePath.EndsWith("getUpdates", StringComparison.Ordinal)
                ? """{"ok":true,"result":[{"update_id":43,"callback_query":{"id":"cb","from":{"id":123},"data":"ap:S000001:T000001:R000001:0","message":{"message_id":5,"chat":{"id":-123}}}}]}"""
                : """{"ok":true,"result":{"message_id":5}}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(result) };
        }));
        var api = new TelegramBotApi(http, Options.Create(new TelegramOptions { BotToken = "test" }));
        var keyboard = new TelegramInlineKeyboard([new[] { new TelegramInlineButton("Aprovar", "opaque") }]);
        Assert.Equal(5, await api.SendApprovalAsync(-123, "aprovação", keyboard, CancellationToken.None));
        var update = Assert.Single(await api.GetUpdatesAsync(0, CancellationToken.None));
        Assert.Equal("cb", update.CallbackQuery!.Id);
        Assert.Equal(5, update.CallbackQuery.Message!.MessageId);
        await api.AnswerCallbackAsync("cb", "Aprovado", CancellationToken.None);
        await api.EditApprovalAsync(-123, 5, "Aprovado", CancellationToken.None);
        Assert.Equal("opaque", requests[0].Body.GetProperty("reply_markup").GetProperty("inline_keyboard")[0][0]
            .GetProperty("callback_data").GetString());
        Assert.Contains(requests[1].Body.GetProperty("allowed_updates").EnumerateArray(),
            item => item.GetString() == "callback_query");
        Assert.EndsWith("answerCallbackQuery", requests[2].Path);
        Assert.Equal("cb", requests[2].Body.GetProperty("callback_query_id").GetString());
        Assert.EndsWith("editMessageText", requests[3].Path);
        Assert.Equal(5, requests[3].Body.GetProperty("message_id").GetInt64());
        Assert.Empty(requests[3].Body.GetProperty("reply_markup").GetProperty("inline_keyboard").EnumerateArray());
    }

    [Theory]
    [InlineData("Bad Request: can't parse entities: secret diagnostic", true)]
    [InlineData("Bad Request: Unsupported start tag echoed-content", true)]
    [InlineData("Bad Request: chat not found", false)]
    public async Task OnlyMarkupErrorsAreClassifiedForSafeFallback(string description, bool markupError)
    {
        JsonElement sent = default;
        using var http = new HttpClient(new StubHandler(async request =>
        {
            sent = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement.Clone();
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { ok = false, description }))
            };
        }));
        var api = new TelegramBotApi(http, Options.Create(new TelegramOptions { BotToken = "test" }));
        var message = Assert.Single(new TelegramMessageFormatter().Format("```python\nprint('<b>')\n```"));
        var error = await Assert.ThrowsAnyAsync<HttpRequestException>(() =>
            api.SendFormattedMessageAsync(-123, message, null, default));
        Assert.Equal(markupError, error is TelegramMarkupException);
        Assert.DoesNotContain(description, error.Message);
        Assert.Equal("HTML", sent.GetProperty("parse_mode").GetString());
        Assert.Equal(message.Html, sent.GetProperty("text").GetString());
    }

    [Fact]
    public async Task HttpMarkupRejectionDeliversPlainFallbackWithoutParseMode()
    {
        var requests = new List<JsonElement>();
        using var http = new HttpClient(new StubHandler(async request =>
        {
            requests.Add(JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement.Clone());
            return requests.Count == 1
                ? new HttpResponseMessage(HttpStatusCode.BadRequest)
                  { Content = new StringContent("""{"ok":false,"description":"Bad Request: can't parse entities"}""") }
                : new HttpResponseMessage(HttpStatusCode.OK)
                  { Content = new StringContent("""{"ok":true,"result":{"message_id":5}}""") };
        }));
        var api = new TelegramBotApi(http, Options.Create(new TelegramOptions { BotToken = "test" }));
        var delivery = new TelegramDeliveryService(api,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TelegramDeliveryService>.Instance);
        await delivery.DeliverJobAsync("J1", 123, -123, "```diff\n-a < b\n+c > d\n```", default);
        Assert.Equal(TelegramDeliveryState.Delivered, delivery.Get("J1", 123)!.State);
        Assert.Equal(2, requests.Count);
        Assert.Equal("HTML", requests[0].GetProperty("parse_mode").GetString());
        Assert.False(requests[1].TryGetProperty("parse_mode", out _));
        Assert.False(requests[1].TryGetProperty("reply_markup", out _));
        Assert.Equal("-a < b\n+c > d\n", requests[1].GetProperty("text").GetString());
    }

    [Fact]
    public async Task PingReplyOmitsUnsetOptionalFieldsThatTelegramRejectsAsNull()
    {
        var telegram = new BotApiLikeHandler();
        using var http = new HttpClient(telegram);
        var api = new TelegramBotApi(http, Options.Create(new TelegramOptions { BotToken = "test" }));

        await api.SendMessageAsync(-123, "pong", default);

        var body = Assert.Single(telegram.Accepted);
        Assert.Equal("pong", body.GetProperty("text").GetString());
        Assert.False(body.TryGetProperty("parse_mode", out _));
        Assert.False(body.TryGetProperty("reply_markup", out _));
    }

    [Fact]
    public async Task OrdinarySessionReplyReachesTelegramAsHtmlWithoutKeyboard()
    {
        var telegram = new BotApiLikeHandler();
        using var http = new HttpClient(telegram);
        var api = new TelegramBotApi(http, Options.Create(new TelegramOptions { BotToken = "test" }));
        var delivery = new TelegramDeliveryService(api, NullLogger<TelegramDeliveryService>.Instance)
            { PartInterval = TimeSpan.Zero };
        var session = new AgentSessionSnapshot("S000001", AgentKind.Claude, 123,
            JobExecutionContext.General("/tmp/general"), AgentPermissionProfile.Manual,
            AgentSessionState.Running, "T000001", 0, [], true, DateTimeOffset.UtcNow, null, null);
        delivery.RegisterSession(session.Id, 123, -123, false);
        delivery.SetActiveSession(123, session.Id);

        foreach (AgentEvent evt in new AgentEvent[]
                 {
                     new MessageDeltaEvent("item", "Estou bem, "),
                     new MessageCompletedEvent("item", "Estou bem, e você? <tudo> & 'certo'"),
                     new TurnCompletedEvent(AgentTurnOutcome.Completed)
                 })
            await delivery.PublishAsync(session, evt with { SessionId = session.Id, TurnId = "T000001" }, default);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (delivery.Get(session.Id, 123)?.State == TelegramDeliveryState.Pending)
            await Task.Delay(10, deadline.Token);
        Assert.Equal(TelegramDeliveryState.Delivered, delivery.Get(session.Id, 123)!.State);
        var body = Assert.Single(telegram.Accepted);
        Assert.Equal("HTML", body.GetProperty("parse_mode").GetString());
        Assert.False(body.TryGetProperty("reply_markup", out _));
        Assert.Equal("Estou bem, e voc&#234;? &lt;tudo&gt; &amp; &#39;certo&#39;\n",
            body.GetProperty("text").GetString());
    }

    [Theory]
    [InlineData("Bad Request: unsupported parse_mode")]
    [InlineData("Bad Request: object expected as reply markup")]
    public async Task InvalidRequestFieldsAreNotMistakenForRejectedMarkup(string description)
    {
        using var http = new HttpClient(new StubHandler(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { ok = false, description }))
            })));
        var api = new TelegramBotApi(http, Options.Create(new TelegramOptions { BotToken = "test" }));
        var message = Assert.Single(new TelegramMessageFormatter().Format("texto"));

        var error = await Assert.ThrowsAnyAsync<HttpRequestException>(() =>
            api.SendFormattedMessageAsync(-123, message, null, default));

        Assert.IsNotType<TelegramMarkupException>(error);
        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
    }

    [Fact]
    public async Task RejectedDeliveryLogsStatusWithoutTokenOrTelegramDiagnostic()
    {
        using var http = new HttpClient(new StubHandler(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"ok":false,"description":"Bad Request: echoed-content"}""")
            })));
        var api = new TelegramBotApi(http, Options.Create(new TelegramOptions { BotToken = "secret-token" }));
        var logger = new CapturingLogger();
        var delivery = new TelegramDeliveryService(api, logger);

        await delivery.DeliverJobAsync("J1", 123, -123, "texto", default);

        Assert.Equal(TelegramDeliveryState.Failed, delivery.Get("J1", 123)!.State);
        var line = Assert.Single(logger.Lines);
        Assert.Contains("HTTP 400", line);
        Assert.DoesNotContain("secret-token", line);
        Assert.DoesNotContain("api.telegram.org", line);
        Assert.DoesNotContain("echoed-content", line);
    }

    private sealed class CapturingLogger : ILogger<TelegramDeliveryService>
    {
        public readonly ConcurrentQueue<string> Lines = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning) Lines.Enqueue(formatter(state, exception));
        }
    }

    // Mirrors what the real Bot API answered for sendMessage: optional fields sent as JSON null are invalid, not absent.
    private sealed class BotApiLikeHandler : HttpMessageHandler
    {
        public readonly ConcurrentQueue<JsonElement> Accepted = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement.Clone();
            var error =
                body.TryGetProperty("parse_mode", out var mode) && mode.ValueKind != JsonValueKind.String
                    ? "Bad Request: unsupported parse_mode"
                    : body.TryGetProperty("reply_markup", out var markup) && markup.ValueKind != JsonValueKind.Object
                        ? "Bad Request: object expected as reply markup"
                        : null;
            if (error is not null)
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { ok = false, error_code = 400, description = error }))
                };
            Accepted.Enqueue(body);
            return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("""{"ok":true,"result":{"message_id":5}}""") };
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => handle(request);
    }
}
