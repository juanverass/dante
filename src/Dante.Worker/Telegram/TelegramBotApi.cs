using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Dante.Worker.Telegram;

public sealed class TelegramBotApi(HttpClient httpClient, IOptions<TelegramOptions> options) : ITelegramBotApi
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(
        long offset,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, MethodUrl("getUpdates"))
        {
            Content = JsonContent.Create(new
            {
                offset,
                timeout = 25,
                allowed_updates = new[] { "message" }
            })
        };

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var envelope = await response.Content.ReadFromJsonAsync<TelegramEnvelope<TelegramUpdate[]>>(
            JsonOptions, cancellationToken);

        if (envelope is not { Ok: true, Result: not null })
        {
            throw new JsonException("Resposta inválida da API do Telegram.");
        }

        return envelope.Result;
    }

    public async Task SendMessageAsync(long chatId, string text, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, MethodUrl("sendMessage"))
        {
            Content = JsonContent.Create(new { chat_id = chatId, text })
        };

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var delay = response.Headers.RetryAfter?.Delta;
            if (response.Headers.RetryAfter?.Date is { } retryAt)
                delay = retryAt - DateTimeOffset.UtcNow;
            try
            {
                if (response.Content is not null)
                {
                    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                    if (document.RootElement.TryGetProperty("parameters", out var parameters) &&
                        parameters.TryGetProperty("retry_after", out var seconds) &&
                        seconds.TryGetInt32(out var value))
                        delay = TimeSpan.FromSeconds(value);
                }
            }
            catch (JsonException) { /* Back off even if Telegram returned an invalid body. */ }
            throw new TelegramRateLimitException(delay);
        }
        response.EnsureSuccessStatusCode();

        var envelope = await response.Content.ReadFromJsonAsync<TelegramEnvelope<JsonElement>>(
            JsonOptions, cancellationToken);

        if (envelope is not { Ok: true })
        {
            throw new JsonException("Resposta inválida da API do Telegram.");
        }
    }

    private Uri MethodUrl(string method)
    {
        var token = options.Value.BotToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("Telegram__BotToken não configurado.");
        }

        return new Uri($"https://api.telegram.org/bot{token}/{method}");
    }

    private sealed record TelegramEnvelope<T>(
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("result")] T? Result);
}

internal sealed class TelegramRateLimitException(TimeSpan? retryAfter)
    : HttpRequestException("Telegram limitou a taxa de mensagens.", null, HttpStatusCode.TooManyRequests)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
