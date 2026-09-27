using System.Net.Http.Json;
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
