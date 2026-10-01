using System.Net;
using System.Text.Json;
using Dante.Worker.Telegram;
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

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => handle(request);
    }
}
